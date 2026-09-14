using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Veiculando.Domain.Enums;
using Veiculando.WhiteLabel.Api.Configurations;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests
{
    public class SeedTokenIsolationTests
    {
        [Fact]
        public async Task Cache_do_seed_e_isolado_por_afiliada()
        {
            using var cache = new MemoryCache(new MemoryCacheOptions());
            using var handlerA = new LoginHandler(71, "token-a");
            using var handlerB = new LoginHandler(72, "token-b");
            using var httpA = new HttpClient(handlerA) { BaseAddress = new Uri("http://core-a/") };
            using var httpB = new HttpClient(handlerB) { BaseAddress = new Uri("http://core-b/") };

            var clientA = CreateClient(httpA, cache, 71);
            var clientB = CreateClient(httpB, cache, 72);

            var autenticadoA = await clientA.GetAuthenticatedClientAsync();
            await clientA.GetAuthenticatedClientAsync();
            var autenticadoB = await clientB.GetAuthenticatedClientAsync();
            await clientB.GetAuthenticatedClientAsync();

            autenticadoA.DefaultRequestHeaders.Authorization!.Parameter.Should().Be("token-a");
            autenticadoB.DefaultRequestHeaders.Authorization!.Parameter.Should().Be("token-b");
            handlerA.LoginCalls.Should().Be(1);
            handlerB.LoginCalls.Should().Be(1);
        }

        [Fact]
        public async Task Resposta_do_core_de_outro_tenant_e_rejeitada_e_nao_cacheada()
        {
            using var cache = new MemoryCache(new MemoryCacheOptions());
            using var handler = new LoginHandler(72, "token-invalido");
            using var http = new HttpClient(handler) { BaseAddress = new Uri("http://core/") };
            var client = CreateClient(http, cache, 71);

            Func<Task> primeiraTentativa = async () => await client.GetAuthenticatedClientAsync();
            await primeiraTentativa.Should().ThrowAsync<InvalidOperationException>();

            handler.AfiliadaId = 71;
            handler.Token = "token-valido";
            var autenticado = await client.GetAuthenticatedClientAsync();

            autenticado.DefaultRequestHeaders.Authorization!.Parameter.Should().Be("token-valido");
            handler.LoginCalls.Should().Be(2);
        }

        private static VeiculandoApiClient CreateClient(
            HttpClient httpClient,
            IMemoryCache cache,
            int afiliadaId)
        {
            var tenant = new FakeTenantContext(afiliadaId);
            var resolver = new FixedSeedAccountResolver(new SeedAccountOptions
            {
                Email = $"seed-{afiliadaId}@teste.local",
                Password = "segredo-de-teste"
            });

            return new VeiculandoApiClient(
                httpClient,
                cache,
                resolver,
                tenant,
                NullLogger<VeiculandoApiClient>.Instance);
        }

        private sealed class FixedSeedAccountResolver : ISeedAccountResolver
        {
            private readonly SeedAccountOptions _options;

            public FixedSeedAccountResolver(SeedAccountOptions options) => _options = options;

            public SeedAccountOptions Resolve() => _options;
        }

        private sealed class FakeTenantContext : ITenantContext
        {
            public FakeTenantContext(int afiliadaId)
            {
                AfiliadaId = afiliadaId;
                Resolvido = true;
            }

            public int AfiliadaId { get; }
            public string Host => $"tenant-{AfiliadaId}.teste";
            public WlDominioTipoEnum Tipo => WlDominioTipoEnum.Painel;
            public bool Resolvido { get; }

            public void Definir(WlTenantInfo tenant) =>
                throw new NotSupportedException();
        }

        private sealed class LoginHandler : HttpMessageHandler
        {
            public LoginHandler(int afiliadaId, string token)
            {
                AfiliadaId = afiliadaId;
                Token = token;
            }

            public int AfiliadaId { get; set; }
            public string Token { get; set; }
            public int LoginCalls { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                LoginCalls++;
                var json = $"{{\"token\":\"{Token}\",\"expires\":60,\"afiliadaId\":{AfiliadaId}}}";

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }
        }
    }
}

