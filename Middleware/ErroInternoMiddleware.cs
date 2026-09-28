using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Veiculando.WhiteLabel.Api.Middleware
{
    /// <summary>
    /// Transforma todo 5xx do BFF em ProblemDetails com <c>traceId</c> e loga a
    /// exceção de forma estruturada com o mesmo traceId (VEI-RD-102).
    /// </summary>
    /// <remarks>
    /// <para>Antes disso um 500 saía com corpo vazio e não havia como ligar a
    /// resposta vista pelo cliente a uma linha de log — foi o que impediu o
    /// diagnóstico de D3/D4 no assurance da Sprint 10.0. O traceId do corpo é
    /// o que se passa em <c>-f trace=</c> para o workflow preview-logs.</para>
    ///
    /// <para>A exceção não vai para o logger como objeto: <c>ex.ToString()</c>
    /// seria escrito sem filtro, e mensagens de SqlException/HttpRequestException
    /// podem carregar connection string ou token. Tipo, mensagem e stack saem
    /// como campos, já mascarados. Query string também fica fora do log.</para>
    /// </remarks>
    public class ErroInternoMiddleware
    {
        private static readonly Regex Segredos = new(
            @"(Bearer\s+)[^\s""',;]+" +
            @"|eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+(\.[A-Za-z0-9_-]*)?" +
            @"|((?:Password|Pwd|AccountKey|SharedAccessKey|User\s*Id|Uid)\s*=\s*)[^;""']*",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly RequestDelegate _next;
        private readonly ILogger<ErroInternoMiddleware> _logger;

        public ErroInternoMiddleware(RequestDelegate next, ILogger<ErroInternoMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var traceId = Activity.Current?.Id ?? context.TraceIdentifier;
            context.Response.OnStarting(() =>
            {
                context.Response.Headers["X-Trace-Id"] = traceId;
                return Task.CompletedTask;
            });

            try
            {
                await _next(context);
            }
            catch (Exception ex) when (!context.Response.HasStarted)
            {
                _logger.LogError(
                    "Erro nao tratado traceId={TraceId} metodo={Metodo} rota={Rota} afiliadaId={AfiliadaId} tipo={TipoExcecao} mensagem={Mensagem} stack={Stack}",
                    traceId,
                    context.Request.Method,
                    context.Request.Path.Value,
                    context.User?.FindFirst("AfiliadaId")?.Value,
                    ex.GetType().FullName,
                    Mascarar(ex.Message),
                    Mascarar(ex.ToString()));

                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await EscreverProblemaAsync(context, traceId);
                return;
            }

            // 5xx sem corpo (ex.: StatusCode(500) cru): ainda dá tempo de escrever.
            if (context.Response.StatusCode >= 500 && !context.Response.HasStarted)
            {
                _logger.LogWarning(
                    "Resposta 5xx sem corpo traceId={TraceId} metodo={Metodo} rota={Rota} status={Status}",
                    traceId, context.Request.Method, context.Request.Path.Value, context.Response.StatusCode);
                await EscreverProblemaAsync(context, traceId);
            }
        }

        public static string Mascarar(string texto) =>
            string.IsNullOrEmpty(texto)
                ? texto
                : Segredos.Replace(texto, m =>
                    m.Groups[1].Success ? m.Groups[1].Value + "***" :
                    m.Groups[3].Success ? m.Groups[3].Value + "***" :
                    "***");

        private static Task EscreverProblemaAsync(HttpContext context, string traceId)
        {
            var status = context.Response.StatusCode;
            var problema = new ProblemDetails
            {
                Title = "Erro interno no servidor.",
                Status = status,
            };
            problema.Extensions["traceId"] = traceId;

            return context.Response.WriteAsJsonAsync(problema, (System.Text.Json.JsonSerializerOptions)null,
                "application/problem+json");
        }
    }
}
