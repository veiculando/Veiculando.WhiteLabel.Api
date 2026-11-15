using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Veiculando.Shared.Helpers;

namespace Veiculando.WhiteLabel.Api.Services
{
    /// <summary>
    /// VEI-RD-171e: grava a foto de checking do app de campo no FileServer legado,
    /// no mesmo caminho do <c>CheckingHandler</c> do Core
    /// (<c>checking/exib/{codigoPI}/{codigoPeca}_{ticks}.jpg</c>, minúsculo), para as
    /// telas de checking do Admin, da Afiliada e do Checking exibirem a imagem.
    /// </summary>
    /// <remarks>
    /// Vai pela rota interna (<c>FileServerUrl</c>, rede Docker), não pelo domínio
    /// público. A URL gravada em <c>CheckingFoto.UrlArquivo</c> é a pública
    /// (<c>FileServerPublicUrl</c>), no mesmo formato das fotos do Veiculando.Checking.
    /// </remarks>
    public interface ICheckingFileServer
    {
        /// <returns>A URL pública da foto.</returns>
        /// <exception cref="HttpRequestException">O FileServer recusou ou não respondeu.</exception>
        Task<string> EnviarAsync(string codigoPedidoInsercao, string codigoPeca, byte[] foto, string contentType,
            DateTime agora, CancellationToken ct);
    }

    public sealed class FileServerChecking : ICheckingFileServer
    {
        private readonly HttpClient _http;
        private readonly string _urlPublica;

        public FileServerChecking(HttpClient http, string urlPublica)
        {
            _http = http;
            _urlPublica = urlPublica.TrimEnd('/');
        }

        public static string Caminho(string codigoPedidoInsercao, string codigoPeca, string contentType, DateTime agora) =>
            "checking/" + FileHelper.NormalizeFileName(
                $"exib/{codigoPedidoInsercao}/{codigoPeca}_{agora.Ticks}{(contentType == "image/png" ? ".png" : ".jpg")}");

        public async Task<string> EnviarAsync(string codigoPedidoInsercao, string codigoPeca, byte[] foto, string contentType,
            DateTime agora, CancellationToken ct)
        {
            var caminho = Caminho(codigoPedidoInsercao, codigoPeca, contentType, agora);
            using var conteudo = new ByteArrayContent(foto);
            conteudo.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            // O FileServer usa o nome do arquivo como caminho relativo dentro de conteudo/.
            using var form = new MultipartFormDataContent { { conteudo, "file", caminho } };
            using var resposta = await _http.PostAsync("upload/conteudo", form, ct);
            resposta.EnsureSuccessStatusCode();
            return $"{_urlPublica}/conteudo/{caminho}";
        }
    }
}

namespace Veiculando.WhiteLabel.Api.Services
{
    using System.Data.Entity;
    using System.Linq;
    using Microsoft.Extensions.Logging;
    using Veiculando.Data.Contexts;

    /// <summary>
    /// VEI-RD-171e/171j: copia uma foto de checking já gravada com <c>wl-upload:</c> para o
    /// FileServer legado e troca <c>CheckingFoto.UrlArquivo</c> pela URL pública. Roda depois
    /// do banco (o FileServer não tem exclusão, então nunca sobra arquivo órfão). Falha do
    /// FileServer não derruba a requisição: a foto segue no storage WL e fica no log.
    /// </summary>
    public sealed class CheckingFotoLegado
    {
        private readonly VeiculandoDataContext _db;
        private readonly ICheckingFileServer _fileServer;
        private readonly ILogger<CheckingFotoLegado> _logger;

        public CheckingFotoLegado(VeiculandoDataContext db, ICheckingFileServer fileServer, ILogger<CheckingFotoLegado> logger) =>
            (_db, _fileServer, _logger) = (db, fileServer, logger);

        public async Task CopiarAsync(int idFoto, int idPedidoItem, string urlAtual, byte[] conteudo, string contentType,
            CancellationToken ct)
        {
            try
            {
                var codigos = await _db.CheckingItens.AsNoTracking().Where(i => i.IdPedidoItem == idPedidoItem)
                    .Select(i => new { Pi = i.PedidoInsercaoItem.PedidoInsercao.Codigo, Peca = i.Peca.Codigo }).SingleAsync(ct);
                var url = await _fileServer.EnviarAsync(codigos.Pi, codigos.Peca, conteudo, contentType, DateTime.Now, ct);
                await _db.Database.ExecuteSqlCommandAsync(
                    "UPDATE CheckingFoto SET UrlArquivo = @p0 WHERE Id = @p1 AND UrlArquivo = @p2", ct, url, idFoto, urlAtual);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "CHECKING_FILESERVER_FALHOU foto={Foto} chave={Chave}", idFoto, urlAtual);
            }
        }
    }
}
