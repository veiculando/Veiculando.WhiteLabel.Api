using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Veiculando.WhiteLabel.Api.Tests.Infrastructure
{
    /// <summary>
    /// <see cref="HttpMessageHandler"/> no lugar do Supabase do CMS.
    /// </summary>
    /// <remarks>
    /// O CI nunca chama o Supabase real: o preview escreve no mesmo projeto que a
    /// LP de produção lê (ADR-CMS-004), então um teste que escapasse para a rede
    /// publicaria dado de teste no site. O fake grava cada chamada (método, URL,
    /// headers e corpo) para o teste conferir o que sairia, e responde com o que
    /// o teste programar em <see cref="Responder"/>.
    /// </remarks>
    public sealed class SupabaseFake : HttpMessageHandler
    {
        public sealed record Chamada(HttpMethod Metodo, Uri Uri, IReadOnlyDictionary<string, string> Headers, string Corpo)
        {
            /// <summary>Caminho + query, decodificados, para asserts legíveis.</summary>
            public string Alvo => Uri.UnescapeDataString(Uri.PathAndQuery);

            /// <summary>Query crua, como saiu no fio.</summary>
            public string QueryCrua => Uri.Query.TrimStart('?');
        }

        private readonly ConcurrentQueue<Chamada> _chamadas = new();

        public IReadOnlyList<Chamada> Chamadas => _chamadas.ToList();

        /// <summary>Resposta programada. O padrão devolve 200 com <c>[]</c>.</summary>
        public Func<Chamada, CancellationToken, Task<HttpResponseMessage>> Responder { get; set; } =
            (_, _) => Task.FromResult(Json(HttpStatusCode.OK, "[]"));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var headers = request.Headers
                .Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
                .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);

            var corpo = request.Content == null ? null : await request.Content.ReadAsStringAsync(ct);
            var chamada = new Chamada(request.Method, request.RequestUri!, headers, corpo);
            _chamadas.Enqueue(chamada);

            return await Responder(chamada, ct);
        }

        public static HttpResponseMessage Json(HttpStatusCode status, string corpo, string contentRange = null)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(corpo, Encoding.UTF8, "application/json"),
            };

            if (contentRange != null)
                response.Content.Headers.TryAddWithoutValidation("Content-Range", contentRange);

            return response;
        }
    }
}
