using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using System.Linq;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests;

[CollectionDefinition("Snaps support", DisableParallelization = true)]
public sealed class SnapsSupportCollection { }

[Collection("Snaps support")]
public sealed class SnapsSupportProxyTests
{
    internal const string Prefix = "/api/wl/snaps/public/projects/53896f31-9d7f-458b-ab63-8bd6ddb549f6";
    internal const string Chat = "/support/chats/11111111-1111-4111-8111-111111111111";
    private const string Card = "/support/cards/22222222-2222-4222-8222-222222222222";

    [Theory]
    [InlineData("missing", 401)]
    [InlineData("invalid", 401)]
    [InlineData("expired", 401)]
    [InlineData("different-tenant", 401)]
    [InlineData("different-host", 401)]
    [InlineData("conflict", 401)]
    [InlineData("duplicate", 401)]
    [InlineData("no-grant", 403)]
    [InlineData("app", 403)]
    [InlineData("core", 401)]
    public async Task Only_an_authorized_operator_on_the_issuing_host_can_call_support(string scenario, int expected)
    {
        using var fixture = new ProxyFixture();
        using var client = fixture.CreateProxyClient(scenario == "different-host" ? "alias.invalid" : "wl.invalid");
        using var request = new HttpRequestMessage(HttpMethod.Get, Prefix + "/support");
        var token = scenario switch
        {
            "missing" => null,
            "invalid" => "not-a-jwt",
            "expired" => Token(expired: true),
            "different-tenant" => Token(tenant: 2),
            "no-grant" => Token(grant: false),
            "app" => Token(actor: "app"),
            "core" => Token(actor: "core"),
            _ => Token()
        };
        if (token != null)
            request.Headers.TryAddWithoutValidation("X-API-Key", token);
        if (scenario == "conflict")
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(grant: false));
        if (scenario == "duplicate")
            request.Headers.TryAddWithoutValidation("X-API-Key", token);

        using var response = await client.SendAsync(request);
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Equal(0, fixture.Upstream.Calls);
    }

    [Fact]
    public async Task X_api_key_is_not_a_session_on_other_wl_routes()
    {
        using var fixture = new ProxyFixture();
        using var client = fixture.CreateProxyClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/wl/auth/me");
        request.Headers.Add("X-API-Key", Token());
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/support", false)]
    [InlineData("GET", "/cards", false)]
    [InlineData("POST", "/cards", false)]
    [InlineData("GET", Card, false)]
    [InlineData("PATCH", Card, false)]
    [InlineData("PATCH", Card + "/status", false)]
    [InlineData("POST", "/support/chats", false)]
    [InlineData("POST", Chat + "/messages", true)]
    [InlineData("POST", Chat + "/proposal/repository", true)]
    [InlineData("POST", Chat + "/confirm", true)]
    [InlineData("POST", Chat + "/close", true)]
    public async Task Allowlisted_routes_preserve_query_and_payload_with_only_server_credentials(
        string method, string suffix, bool session)
    {
        using var fixture = new ProxyFixture();
        using var client = fixture.CreateProxyClient();
        const string query = "?limit=2&status=one&status=two";
        const string payload = "{\"description\":\"synthetic\"}";
        fixture.Upstream.Send = async (message, cancellation) =>
        {
            Assert.Equal("https://snaps.invalid" + Prefix["/api/wl/snaps".Length..] + suffix + query,
                message.RequestUri.AbsoluteUri);
            Assert.Equal(ProxyFixture.ServerKey, Assert.Single(message.Headers.GetValues("X-API-Key")));
            Assert.False(message.Headers.Contains("Authorization"));
            Assert.False(message.Headers.Contains("Cookie"));
            Assert.False(message.Headers.Contains("Origin"));
            Assert.Equal(session, message.Headers.Contains("X-Support-Session"));
            if (session)
                Assert.Equal("synthetic-session", Assert.Single(message.Headers.GetValues("X-Support-Session")));
            if (method != "GET")
                Assert.Equal(payload, await message.Content.ReadAsStringAsync(cancellation));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        };

        using var request = new HttpRequestMessage(new HttpMethod(method), Prefix + suffix + query);
        request.Headers.Add("X-API-Key", Token());
        request.Headers.Add("X-Support-Session", "synthetic-session");
        request.Headers.Add("Cookie", "synthetic=value");
        request.Headers.Add("Origin", "https://wl.invalid");
        if (method != "GET")
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, fixture.Upstream.Calls);
    }

    [Theory]
    [InlineData("GET", "/docs")]
    [InlineData("GET", "/roadmap")]
    [InlineData("GET", "/costs/hours")]
    [InlineData("GET", "/support?target=https://other.invalid")]
    [InlineData("GET", "/support/")]
    [InlineData("GET", "/support%2fcards")]
    [InlineData("GET", "/support/cards/not-a-uuid")]
    [InlineData("GET", "/support//cards")]
    [InlineData("POST", Chat + "/proposal")]
    [InlineData("DELETE", "/cards/22222222-2222-4222-8222-222222222222")]
    [InlineData("DELETE", Card)]
    [InlineData("PUT", "/support")]
    public async Task Paths_and_methods_outside_the_support_contract_never_reach_snaps(string method, string suffix)
    {
        using var fixture = new ProxyFixture();
        using var client = fixture.CreateProxyClient();
        using var request = Authorized(new HttpMethod(method), Prefix + suffix);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, fixture.Upstream.Calls);
    }

    [Fact]
    public async Task A_different_project_is_rejected()
    {
        using var fixture = new ProxyFixture();
        using var client = fixture.CreateProxyClient();
        using var request = Authorized(HttpMethod.Get, "/api/wl/snaps/public/projects/22222222-2222-4222-8222-222222222222/support");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, fixture.Upstream.Calls);
    }

    [Theory]
    [InlineData(302, 502)]
    [InlineData(401, 502)]
    [InlineData(403, 502)]
    [InlineData(404, 404)]
    [InlineData(409, 409)]
    [InlineData(410, 410)]
    [InlineData(422, 422)]
    [InlineData(500, 500)]
    public async Task Upstream_status_is_handled_without_reflecting_body_or_headers(int status, int expected)
    {
        using var fixture = new ProxyFixture();
        using var client = fixture.CreateProxyClient();
        fixture.Upstream.Send = (_, _) =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(ProxyFixture.ServerKey)
            };
            response.Headers.Location = new Uri("https://other.invalid/" + ProxyFixture.ServerKey);
            response.Headers.Add("X-API-Key", ProxyFixture.ServerKey);
            return Task.FromResult(response);
        };

        using var request = Authorized(HttpMethod.Get, Prefix + "/support");
        using var response = await client.SendAsync(request);
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Equal("{\"error\":\"Support request failed.\"}", await response.Content.ReadAsStringAsync());
        Assert.Null(response.Headers.Location);
        Assert.False(response.Headers.Contains("X-API-Key"));
    }

    [Fact]
    public async Task Transport_errors_do_not_log_or_return_credentials()
    {
        using var fixture = new ProxyFixture();
        using var client = fixture.CreateProxyClient();
        var token = Token();
        const string session = "synthetic-private-chat-session";
        fixture.Upstream.Send = (_, _) => throw new HttpRequestException(ProxyFixture.ServerKey);
        using var request = new HttpRequestMessage(HttpMethod.Post, Prefix + Chat + "/messages");
        request.Headers.Add("X-API-Key", token);
        request.Headers.Add("X-Support-Session", session);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var output = await response.Content.ReadAsStringAsync() + string.Join("\n", fixture.Logs.Messages);
        Assert.DoesNotContain(ProxyFixture.ServerKey, output);
        Assert.DoesNotContain(token, output);
        Assert.DoesNotContain(session, output);
    }

    [Theory]
    [InlineData("", "https://snaps.invalid")]
    [InlineData("synthetic", "")]
    [InlineData("synthetic", "http://snaps.invalid")]
    [InlineData("synthetic", "https://snaps.invalid/path")]
    public async Task Missing_or_invalid_config_only_disables_the_proxy(string key, string url)
    {
        using var fixture = new ProxyFixture(key, url);
        using var client = fixture.CreateProxyClient();
        using var request = Authorized(HttpMethod.Get, Prefix + "/support");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, fixture.Upstream.Calls);
        using var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    internal static HttpRequestMessage Authorized(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token());
        return request;
    }

    private static string Token(bool expired = false, int tenant = 1, bool grant = true, string actor = "operator")
    {
        var now = DateTime.UtcNow;
        var claims = new List<Claim> { new("WlHost", "wl.invalid") };
        if (actor != "core") claims.Add(new Claim("AfiliadaId", tenant.ToString()));
        if (actor == "operator") claims.Add(new Claim("WlUsuarioId", "7"));
        if (actor == "app") claims.Add(new Claim("WlAnuncianteId", "7"));
        if (grant) claims.Add(new Claim("permission", "SuporteGerenciar"));
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            claims: claims, notBefore: now.AddMinutes(-2), expires: now.AddMinutes(expired ? -1 : 2),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.ASCII.GetBytes(ProxyFixture.JwtKey)), SecurityAlgorithms.HmacSha256)));
    }

    internal sealed class ProxyFixture : WebApplicationFactory<Program>
    {
        internal const string ServerKey = "synthetic-snaps-server-key";
        internal static readonly string JwtKey = "synthetic-wl-" + new string('x', 40);
        internal readonly UpstreamStub Upstream = new();
        internal readonly LogCapture Logs = new();
        private readonly string _key;
        private readonly string _url;

        internal ProxyFixture(string key = ServerKey, string url = "https://snaps.invalid")
        {
            _key = key;
            _url = url;
        }

        internal HttpClient CreateProxyClient(string host = "wl.invalid") => CreateClient(
            new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://" + host), AllowAutoRedirect = false });

        internal SnapsSupportProxyTiming Timing { get; init; }

        internal async Task<IWebHost> StartKestrel()
        {
            var builder = new WebHostBuilder().UseKestrel().UseStartup<Startup>()
                .UseUrls("http://127.0.0.1:0");
            ConfigureWebHost(builder);
            var host = builder.Build();
            try
            {
                await host.StartAsync();
                return host;
            }
            catch
            {
                host.Dispose();
                throw;
            }
        }

        internal static HttpClient CreateKestrelClient(IWebHost host)
        {
            var address = host.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>().Addresses.Single();
            var client = new HttpClient(new SocketsHttpHandler { MaxResponseDrainSize = 0 })
            {
                BaseAddress = new Uri(address)
            };
            client.DefaultRequestHeaders.Host = "wl.invalid";
            return client;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string>
                {
                    ["ConnectionStrings:Veiculando"] = "Server=sql.invalid;Database=synthetic;Integrated Security=true",
                    ["JwtSettings:Secret"] = JwtKey,
                    ["GOVERNANCE_API_KEY"] = _key,
                    ["GOVERNANCE_API_URL"] = _url
                }));
            builder.ConfigureLogging(logging => logging
                .AddFilter<LogCapture>("System.Net.Http.HttpClient." + SnapsSupportProxyService.ClientName, LogLevel.Trace)
                .AddProvider(Logs));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IWlTenantResolver, TenantStub>();
                services.AddHttpClient(SnapsSupportProxyService.ClientName).ConfigurePrimaryHttpMessageHandler(() => Upstream);
                if (Timing != null)
                    services.AddSingleton(Timing);
            });
        }
    }

    internal sealed class UpstreamStub : HttpMessageHandler
    {
        private int _calls;
        internal int Calls => _calls;
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Send =
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Send(request, cancellationToken);
        }
    }

    private sealed class TenantStub : IWlTenantResolver
    {
        public Task<WlTenantInfo> ResolverAsync(string host) => Task.FromResult(
            new WlTenantInfo { AfiliadaId = 1, Host = host });
        public Task<WlBrandingPublico> ObterBrandingAsync(int afiliadaId) => throw new NotSupportedException();
        public void InvalidarDominio(string host) => throw new NotSupportedException();
        public void InvalidarBranding(int afiliadaId) => throw new NotSupportedException();
    }

    internal sealed class LogCapture : ILoggerProvider, ILogger
    {
        internal readonly ConcurrentQueue<string> Messages = new();
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception error, Func<TState, Exception, string> formatter)
            => Messages.Enqueue(formatter(state, error) + error);
        public void Dispose() { }
    }
}
