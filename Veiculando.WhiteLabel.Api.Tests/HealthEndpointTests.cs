using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Veiculando.WhiteLabel.Api.Middleware;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests;

public class HealthEndpointTests
{
    [Theory]
    [InlineData("/health", HttpStatusCode.OK, 0)]
    [InlineData("/health/tenant", HttpStatusCode.NotFound, 1)]
    [InlineData("/api/wl/config/branding", HttpStatusCode.NotFound, 1)]
    public async Task Liveness_is_host_independent_without_bypassing_tenant_routes(
        string path, HttpStatusCode expected, int resolverCalls)
    {
        var resolver = new UnknownHostResolver();
        using var server = new TestServer(new WebHostBuilder()
            .UseEnvironment("Testing")
            .ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string>
            {
                ["ConnectionStrings:Veiculando"] = "Server=unused.invalid;Database=health-test;Integrated Security=true",
                ["JwtSettings:Secret"] = "health-test-only-secret-at-least-32-characters"
            }))
            .UseStartup<Startup>()
            .ConfigureTestServices(services => services.AddSingleton<IWlTenantResolver>(resolver)));
        using var client = server.CreateClient();
        client.BaseAddress = new Uri("http://127.0.0.1:8080");
        using var response = await client.GetAsync(path);
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(resolverCalls, resolver.Calls);
        if (expected == HttpStatusCode.OK)
            Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    private sealed class UnknownHostResolver : IWlTenantResolver
    {
        public int Calls { get; private set; }
        public Task<WlTenantInfo> ResolverAsync(string host) { Calls++; return Task.FromResult<WlTenantInfo>(null); }
        public Task<WlBrandingPublico> ObterBrandingAsync(int id) => throw new InvalidOperationException("Health must not query branding");
        public void InvalidarDominio(string host) { }
        public void InvalidarBranding(int id) { }
    }
}
