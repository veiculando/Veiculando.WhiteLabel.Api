using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Veiculando.Shared;
using Veiculando.WhiteLabel.Api.Services;

namespace Veiculando.WhiteLabel.Api.Tests.Infrastructure
{
    /// <summary>
    /// Sobe o BFF em memoria apontado para o SQL Server do <see cref="SqlServerFixture"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Tenant.</b> O <c>TenantMiddleware</c> ignora o header
    /// <c>X-Tenant-AfiliadaId</c> e usa sempre <c>WL:AfiliadaId</c> da
    /// configuracao — o servidor e a fonte de verdade (ADR-WL-005). Por isso o
    /// tenant e parametro da factory: cada instancia representa uma exibidora.
    /// Testes de isolamento sobem a instancia da afiliada A e tentam alcancar
    /// recursos da B, que e a superficie de ataque real.</para>
    ///
    /// <para><b>Autenticacao.</b> Nao ha handler de auth falso: os testes logam
    /// pelo endpoint de verdade. Isso exercita BCrypt, a emissao de claims e as
    /// policies do <c>AuthorizationSetup</c> — que sao justamente o que se quer
    /// verificar. Um handler falso testaria o handler.</para>
    ///
    /// <para><b>Rate limit.</b> <c>RateLimitLogin</c> permite 10 requisicoes por
    /// janela por IP, e numa suite todas vem do mesmo IP. Por isso o token e
    /// cacheado por (email, senha): logar uma vez por operador mantem a suite
    /// abaixo do limite sem precisar desligar o limitador — desliga-lo esconderia
    /// uma regressao no proprio limitador.</para>
    /// </remarks>
    public sealed class WlApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly ConcurrentDictionary<string, string> _tokens = new();

        /// <summary>Afiliada que esta instancia do BFF representa.</summary>
        public int AfiliadaId { get; }

        public string Host { get; }

        /// <summary>Captura o que o BFF encaminhou ao core.</summary>
        public CoreApiStub Core { get; } = new();

        /// <summary>Dublê de e-mail — captura o que seria enviado pela recuperação de senha.</summary>
        public FakeWlPasswordEmailSender EmailSender { get; } = new();
        public FakeWlUploadStorage Uploads { get; } = new();

        /// <summary>Captura o que o BFF pediu ao FileServer (PDF de PI).</summary>
        public FileServerStub FileServer { get; } = new();

        public WlApiFactory(SqlServerFixture db, int afiliadaId, string? host = null)
        {
            _connectionString = db.ConnectionString;
            AfiliadaId = afiliadaId;
            Host = host ?? $"afiliada-{afiliadaId}.teste";
            Core.AfiliadaId = afiliadaId;
            Seed.DominioAsync(afiliadaId, Host, ativo: true).GetAwaiter().GetResult();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Veiculando"] = _connectionString,
                    // >= 32 caracteres: o AuthenticationSetup recusa subir abaixo
                    // disso, e essa validacao tambem esta sob teste.
                    ["JwtSettings:Secret"] = "segredo-de-teste-com-mais-de-32-caracteres-1234567890",
                    ["JwtSettings:ExpirationInMinutes"] = "60",
                    ["JwtSettings:Issuer"] = "wl-tests",
                    ["JwtSettings:ValidAt"] = "wl-tests",

                    ["CoreApiUrl"] = "http://core.invalido/",

                    // Host inalcançável de proposito: se algum caminho escapar do
                    // FileServerStub a chamada falha em vez de sair para a rede.
                    ["FileServerUrl"] = "http://fileserver.invalido/",

                    // Conta de servico: o VeiculandoApiClient recusa autenticar sem
                    // ela. Os valores nao importam — quem responde e o CoreApiStub —
                    // mas a ausencia faz o cliente lancar antes de chegar na rede.
                    [$"SeedAccounts:{AfiliadaId}:Email"] = $"conta-servico-{AfiliadaId}@teste.local",
                    [$"SeedAccounts:{AfiliadaId}:Password"] = "irrelevante-o-stub-responde",
                });
            });

            builder.ConfigureTestServices(services =>
            {
                // O DI do BFF preenche EnvironmentSettings.ConnectionString a partir
                // da configuracao, mas ele e um campo estatico e outra factory pode
                // te-lo sobrescrito. Reafirmar aqui evita que a ordem de execucao
                // dos testes decida contra qual banco o EF vai falar.
                EnvironmentSettings.ConnectionString = _connectionString;

                // Intercepta o HttpClient tipado do cliente do core.
                services.AddHttpClient<IVeiculandoApiClient, VeiculandoApiClient>(client =>
                    {
                        client.BaseAddress = new Uri("http://core.invalido/");
                    })
                    .ConfigurePrimaryHttpMessageHandler(() => Core);

                // Substitui o transporte real de e-mail (SendGrid) pelo dublê: os
                // testes de recuperação de senha não devem depender de rede nem
                // de uma API key de verdade.
                services.AddSingleton<IWlPasswordEmailSender>(EmailSender);
                services.AddSingleton<IWlUploadStorage>(Uploads);

                // Intercepta o cliente tipado do FileServer, pelo mesmo mecanismo
                // usado no core: o teste precisa observar se a chamada saiu.
                services.AddHttpClient<IWlPiPdfSource, FileServerPiPdfSource>(client =>
                    {
                        client.BaseAddress = new Uri("http://fileserver.invalido/");
                    })
                    .ConfigurePrimaryHttpMessageHandler(() => FileServer);
            });
        }

        /// <summary>
        /// Cliente HTTP autenticado como o operador informado.
        /// </summary>
        public async Task<HttpClient> ClienteAutenticadoAsync(string email, string senha)
        {
            var token = await ObterTokenAsync(email, senha);

            var client = CriarCliente();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }

        /// <summary>Cliente sem autenticacao, para verificar 401.</summary>
        public HttpClient ClienteAnonimo(string? host = null) => CriarCliente(host);

        public Task<string> ObterTokenParaTesteAsync(string email, string senha) =>
            ObterTokenAsync(email, senha);

        private HttpClient CriarCliente(string? host = null) =>
            CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri($"https://{host ?? Host}")
            });

        private async Task<string> ObterTokenAsync(string email, string senha)
        {
            var chave = $"{AfiliadaId}|{email}";

            if (_tokens.TryGetValue(chave, out var cacheado))
                return cacheado;

            using var client = CriarCliente();
            var resposta = await client.PostAsJsonAsync("/api/wl/auth/login", new { Email = email, Senha = senha });

            if (!resposta.IsSuccessStatusCode)
            {
                var corpo = await resposta.Content.ReadAsStringAsync();
                throw new InvalidOperationException(
                    $"Login falhou para '{email}' na afiliada {AfiliadaId}: {(int)resposta.StatusCode} {corpo}");
            }

            var conteudo = await resposta.Content.ReadFromJsonAsync<RespostaLogin>()
                           ?? throw new InvalidOperationException("Resposta de login vazia.");

            _tokens[chave] = conteudo.Token;
            return conteudo.Token;
        }

        private sealed record RespostaLogin(string Token, int ExpiresInMinutes, string Nome, string Email, string[] Permissoes);
    }
}
