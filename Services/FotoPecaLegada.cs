using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Veiculando.WhiteLabel.Api.Services
{
    /// <summary>Foto de peça cadastrada pelo Core, já lida do FileServer.</summary>
    public sealed record FotoPecaLegada(byte[] Conteudo, string ContentType);

    /// <summary>
    /// Foto de peça enviada antes do WhiteLabel, guardada pelo FileServer do Core
    /// em <c>conteudo/pecas/fotos/{Peca.Foto}</c>.
    /// </summary>
    /// <remarks>
    /// <para>É a mesma peça e a mesma foto que o Core exibe; o recorte de tenant
    /// continua no BFF, que só chega aqui depois de achar a peça em
    /// <c>ITenantQueries.Pecas</c>. O browser nunca recebe a URL do FileServer.</para>
    ///
    /// <para>O FileServer roda em Linux e os arquivos estão em minúsculas, mas
    /// boa parte dos nomes gravados no banco vem em maiúsculas
    /// (<c>peca_SJCAUR2_1_2.jpg</c>). Por isso a busca tenta o nome gravado e,
    /// se não achar, o nome em minúsculas.</para>
    /// </remarks>
    public interface IFotoPecaLegadaSource
    {
        /// <returns>A foto, ou <c>null</c> se o FileServer não tem o arquivo.</returns>
        Task<FotoPecaLegada?> ObterAsync(string arquivoNome, CancellationToken ct);
    }

    /// <inheritdoc />
    public sealed class FileServerFotoPecaLegadaSource : IFotoPecaLegadaSource
    {
        private const long TamanhoMaximo = 15 * 1024 * 1024;

        private static readonly Regex NomeSeguro = new(
            @"^[A-Za-z0-9][A-Za-z0-9_.\-]{0,200}\.(jpe?g|png)$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private readonly HttpClient _http;

        public FileServerFotoPecaLegadaSource(HttpClient http) => _http = http;

        /// <summary>
        /// Nome que pode ser buscado no FileServer: só um arquivo de imagem, sem
        /// diretório. Valores como <c>"NULL"</c> gravados como texto ficam de fora.
        /// </summary>
        public static bool NomeValido(string arquivoNome) =>
            !string.IsNullOrEmpty(arquivoNome) && NomeSeguro.IsMatch(arquivoNome);

        public async Task<FotoPecaLegada?> ObterAsync(string arquivoNome, CancellationToken ct)
        {
            if (!NomeValido(arquivoNome)) return null;

            foreach (var nome in new[] { arquivoNome, arquivoNome.ToLowerInvariant() }.Distinct(StringComparer.Ordinal))
            {
                using var resposta = await _http.GetAsync(
                    $"conteudo/pecas/fotos/{Uri.EscapeDataString(nome)}", HttpCompletionOption.ResponseHeadersRead, ct);

                if (resposta.StatusCode == HttpStatusCode.NotFound) continue;
                resposta.EnsureSuccessStatusCode();

                var tipo = resposta.Content.Headers.ContentType?.MediaType;
                if (tipo != "image/jpeg" && tipo != "image/png") return null;
                if (resposta.Content.Headers.ContentLength > TamanhoMaximo) return null;

                var bytes = await resposta.Content.ReadAsByteArrayAsync(ct);
                return bytes.Length > TamanhoMaximo ? null : new FotoPecaLegada(bytes, tipo);
            }

            return null;
        }
    }
}
