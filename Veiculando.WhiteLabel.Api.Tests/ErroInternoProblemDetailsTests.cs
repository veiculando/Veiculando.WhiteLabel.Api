using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Veiculando.WhiteLabel.Api.Middleware;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests;

/// <summary>
/// VEI-RD-102: todo 5xx do BFF devolve ProblemDetails com traceId, e o log da
/// exceção carrega o mesmo traceId — é por ele que o workflow preview-logs
/// encontra a linha.
/// </summary>
public class ErroInternoProblemDetailsTests
{
    private const string TokenSecreto = "eyJhbGciOiJIUzI1NiJ9.segredo-do-teste.assinatura";

    [Fact]
    public async Task Excecao_nao_tratada_vira_500_com_traceId_que_casa_com_o_log()
    {
        var logs = new LogsCapturados();
        using var server = CriarServidor(logs);
        using var client = server.CreateClient();
        client.BaseAddress = new Uri("http://127.0.0.1:8080");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TokenSecreto);

        using var response = await client.GetAsync("/api/wl/config/branding");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var corpo = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(500, corpo.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(corpo.RootElement.GetProperty("title").GetString()));
        var traceId = corpo.RootElement.GetProperty("traceId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(traceId));

        // O corpo não expõe a exceção ao cliente.
        Assert.DoesNotContain("falha-simulada", corpo.RootElement.GetRawText());

        var linha = Assert.Single(logs.Entradas, e => e.Contains(traceId!));
        Assert.Contains("InvalidOperationException", linha);
        Assert.Contains("falha-simulada", linha);
        Assert.Contains("GET", linha);
        Assert.Contains("/api/wl/config/branding", linha);

        Assert.DoesNotContain(logs.Entradas, e => e.Contains(TokenSecreto) || e.Contains("Bearer"));
    }

    private static TestServer CriarServidor(LogsCapturados logs) =>
        new(new WebHostBuilder()
            .UseEnvironment("Testing")
            .ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Veiculando"] = "Server=unused.invalid;Database=erro-test;Integrated Security=true",
                ["JwtSettings:Secret"] = "erro-test-only-secret-at-least-32-characters"
            }))
            .ConfigureLogging(l => l.AddProvider(logs))
            .UseStartup<Startup>()
            .ConfigureTestServices(services => services.AddSingleton<IWlTenantResolver>(new ResolverQueFalha())));

    private sealed class ResolverQueFalha : IWlTenantResolver
    {
        public Task<WlTenantInfo> ResolverAsync(string host) => throw new InvalidOperationException("falha-simulada");
        public Task<WlBrandingPublico> ObterBrandingAsync(int id) => throw new InvalidOperationException("falha-simulada");
        public void InvalidarDominio(string host) { }
        public void InvalidarBranding(int id) { }
    }

    /// <summary>Formata cada entrada como o console formatter faria: mensagem + exceção.</summary>
    private sealed class LogsCapturados : ILoggerProvider
    {
        public ConcurrentBag<string> Entradas { get; } = new();
        public ILogger CreateLogger(string categoria) => new Logger(this);
        public void Dispose() { }

        private sealed class Logger : ILogger
        {
            private readonly LogsCapturados _dono;
            public Logger(LogsCapturados dono) => _dono = dono;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var campos = state is IEnumerable<KeyValuePair<string, object?>> kv
                    ? string.Join(" ", kv.Select(p => $"{p.Key}={p.Value}"))
                    : "";
                _dono.Entradas.Add($"{logLevel} {formatter(state, exception)} {campos} {exception}");
            }
        }
    }
}
