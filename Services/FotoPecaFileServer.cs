using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Veiculando.Shared.Helpers;

namespace Veiculando.WhiteLabel.Api.Services
{
    /// <summary>Foto de peça já lida do FileServer.</summary>
    public sealed record FotoPecaArquivo(byte[] Conteudo, string ContentType);

    /// <summary>
    /// A foto da peça, guardada pelo FileServer do Core em
    /// <c>conteudo/pecas/fotos/{Peca.Foto}</c>.
    /// </summary>
    /// <remarks>
    /// <para>Uma foto só por peça: Core e WhiteLabel leem e gravam o mesmo arquivo,
    /// pelo mesmo caminho e com o mesmo nome (<c>{Peca.Codigo}.jpg</c>, como o
    /// <c>PecaController.AlterarFotoUpload</c> do Core). Trocou num, troca no outro.</para>
    ///
    /// <para>O recorte de tenant continua no BFF, que só chega aqui depois de
    /// achar a peça em <c>ITenantQueries.Pecas</c>. O browser nunca recebe a URL
    /// do FileServer.</para>
    ///
    /// <para>O nome é normalizado com <c>FileHelper.NormalizeFileName</c>, a
    /// mesma regra do Core (VEI-SUP-13): o FileServer grava em minúsculas e o
    /// <c>FotoPeca.Url</c> lê em minúsculas, mas há nomes no banco em
    /// maiúsculas (<c>peca_SJCAUR2_1_2.jpg</c>).</para>
    /// </remarks>
    public interface IFotoPecaFileServer
    {
        /// <returns>A foto, ou <c>null</c> se o FileServer não tem o arquivo.</returns>
        Task<FotoPecaArquivo?> ObterAsync(string arquivoNome, CancellationToken ct);

        /// <summary>
        /// Lê o arquivo que uma troca vai sobrescrever. Diferente de
        /// <see cref="ObterAsync"/>, só devolve <c>null</c> quando o arquivo não
        /// existe: qualquer outro problema lança, para a troca não seguir sem cópia.
        /// </summary>
        /// <exception cref="HttpRequestException">O FileServer não respondeu.</exception>
        /// <exception cref="InvalidOperationException">Arquivo existe, mas não pôde ser copiado.</exception>
        Task<FotoPecaArquivo?> ObterParaCopiaAsync(string arquivoNome, CancellationToken ct);

        /// <summary>
        /// Grava a foto JPG no FileServer pela rota que o Core usa
        /// (<c>upload/peca-foto</c>) e devolve o nome a gravar em <c>Peca.Foto</c>.
        /// </summary>
        /// <exception cref="HttpRequestException">O FileServer recusou ou não respondeu.</exception>
        Task<string> EnviarAsync(string codigoPeca, byte[] jpg, CancellationToken ct);
    }

    /// <inheritdoc />
    public sealed class FileServerFotoPeca : IFotoPecaFileServer
    {
        private const long TamanhoMaximo = 15 * 1024 * 1024;

        /// <summary>Maior que o da leitura: a rota antiga do Core não limita o arquivo, e a troca não pode travar nele.</summary>
        private const long TamanhoMaximoCopia = 64 * 1024 * 1024;

        /// <summary>Com <c>ResponseHeadersRead</c>, o timeout do HttpClient não cobre o corpo.</summary>
        private static readonly TimeSpan PrazoLeitura = TimeSpan.FromSeconds(30);

        private static readonly Regex NomeSeguro = new(
            @"^[A-Za-z0-9][A-Za-z0-9_.\-]{0,200}\.(jpe?g|png)$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private readonly HttpClient _http;

        public FileServerFotoPeca(HttpClient http) => _http = http;

        /// <summary>
        /// Nome que pode ser buscado no FileServer: só um arquivo de imagem, sem
        /// diretório. Valores como <c>"NULL"</c> gravados como texto ficam de fora.
        /// </summary>
        public static bool NomeValido(string arquivoNome) =>
            !string.IsNullOrEmpty(arquivoNome) && NomeSeguro.IsMatch(arquivoNome);

        /// <summary>Nome do arquivo da peça, igual ao do Core.</summary>
        public static string NomeDaPeca(string codigoPeca) =>
            FileHelper.NormalizeFileName($"{codigoPeca}.jpg");

        public Task<FotoPecaArquivo?> ObterAsync(string arquivoNome, CancellationToken ct) =>
            NomeValido(arquivoNome) ? LerAsync(arquivoNome, false, ct) : Task.FromResult<FotoPecaArquivo?>(null);

        public Task<FotoPecaArquivo?> ObterParaCopiaAsync(string arquivoNome, CancellationToken ct) =>
            LerAsync(arquivoNome, true, ct);

        private async Task<FotoPecaArquivo?> LerAsync(string arquivoNome, bool estrito, CancellationToken ct)
        {
            var nome = FileHelper.NormalizeFileName(arquivoNome);
            var tamanhoMaximo = estrito ? TamanhoMaximoCopia : TamanhoMaximo;
            using var prazo = CancellationTokenSource.CreateLinkedTokenSource(ct);
            prazo.CancelAfter(PrazoLeitura);
            using var resposta = await _http.GetAsync(
                $"conteudo/pecas/fotos/{Uri.EscapeDataString(nome)}", HttpCompletionOption.ResponseHeadersRead, prazo.Token);

            if (resposta.StatusCode == HttpStatusCode.NotFound) return null;
            resposta.EnsureSuccessStatusCode();

            var tipo = resposta.Content.Headers.ContentType?.MediaType;
            var aceitavel = (tipo == "image/jpeg" || tipo == "image/png")
                && !(resposta.Content.Headers.ContentLength > tamanhoMaximo);
            if (aceitavel)
            {
                var bytes = await resposta.Content.ReadAsByteArrayAsync(prazo.Token);
                if (bytes.Length <= tamanhoMaximo) return new FotoPecaArquivo(bytes, tipo);
            }
            if (estrito) throw new InvalidOperationException($"A foto {nome} existe, mas não pôde ser copiada.");
            return null;
        }

        public async Task<string> EnviarAsync(string codigoPeca, byte[] jpg, CancellationToken ct)
        {
            var nome = NomeDaPeca(codigoPeca);
            if (!NomeValido(nome)) throw new ArgumentException("Código de peça não serve como nome de arquivo.", nameof(codigoPeca));

            using var conteudo = new ByteArrayContent(jpg);
            conteudo.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            using var form = new MultipartFormDataContent { { conteudo, "file", nome } };
            using var resposta = await _http.PostAsync("upload/peca-foto", form, ct);
            resposta.EnsureSuccessStatusCode();
            return nome;
        }
    }
}
