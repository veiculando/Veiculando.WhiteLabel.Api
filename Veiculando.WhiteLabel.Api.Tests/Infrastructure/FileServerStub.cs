using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Veiculando.WhiteLabel.Api.Tests.Infrastructure
{
    /// <summary>
    /// Substitui o Veiculando.FileServer nas chamadas de PDF de PI e de foto de peça.
    /// </summary>
    /// <remarks>
    /// Registra as rotas pedidas para que os testes possam afirmar sobre o que
    /// <b>não</b> foi chamado. Essa é a assercao central do card: uma PI de outra
    /// afiliada precisa virar 404 no BFF <i>antes</i> de o FileServer ser
    /// alcançado — se a chamada sair, o arquivo volta, porque o FileServer não
    /// autentica ninguém.
    /// </remarks>
    public sealed class FileServerStub : HttpMessageHandler
    {
        private readonly List<string> _rotas = new();

        /// <summary>Rotas que o BFF pediu ao FileServer, na ordem.</summary>
        public IReadOnlyList<string> Rotas => _rotas;

        /// <summary>Conteúdo devolvido no caminho feliz.</summary>
        public byte[] Conteudo { get; set; } = { 0x25, 0x50, 0x44, 0x46, 0x2D }; // "%PDF-"

        /// <summary>Resposta a devolver. Trocável para exercitar falha e 404.</summary>
        public Func<string, HttpResponseMessage>? Responder { get; set; }

        /// <summary>Fotos de peça gravadas por <c>upload/peca-foto</c>, por nome.</summary>
        public Dictionary<string, byte[]> FotosPeca { get; } = new();

        /// <summary>Faz <c>upload/peca-foto</c> responder 500.</summary>
        public bool FalharUploadFoto { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            _rotas.Add(url);

            if (Responder != null)
                return Responder(url);

            // Mesma rota e mesmo campo que o FileServer do Core aceita.
            if (request.Method == HttpMethod.Post && url.EndsWith("upload/peca-foto"))
            {
                if (FalharUploadFoto) return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                foreach (var parte in (MultipartFormDataContent)request.Content!)
                {
                    var nome = parte.Headers.ContentDisposition?.FileName?.Trim('"');
                    if (nome != null) FotosPeca[nome] = await parte.ReadAsByteArrayAsync(cancellationToken);
                }
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            const string fotos = "conteudo/pecas/fotos/";
            var i = url.IndexOf(fotos, StringComparison.Ordinal);
            if (i >= 0 && FotosPeca.TryGetValue(Uri.UnescapeDataString(url[(i + fotos.Length)..]), out var foto))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(foto)
                    {
                        Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg") }
                    }
                };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Conteudo)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf") }
                }
            };
        }
    }
}
