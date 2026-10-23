using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests;

/// <summary>
/// Produção do WL (ADR-WL-018): Cloudflare -> cloudflared -> edge -> BFF. O edge
/// acrescenta o IP do cloudflared ao X-Forwarded-For, então o BFF recebe
/// "&lt;cliente&gt;, &lt;cloudflared&gt;" vindo do IP do edge. O rate limit particiona por
/// <c>Connection.RemoteIpAddress</c>: se o BFF desembrulhar só um salto, todo
/// cliente vira o cloudflared e o limite de 10 logins/min passa a ser global.
/// </summary>
public class ProxyReversoAtrasDoTunnelTests
{
    private const string Edge = "172.30.0.10";
    private const string Cloudflared = "172.30.0.11";
    private const string Cliente = "203.0.113.7";

    private static TestServer Servidor(params string[] proxies)
    {
        var config = new Dictionary<string, string>
        {
            ["ConnectionStrings:Veiculando"] = "Server=unused.invalid;Database=proxy-test;Integrated Security=true",
            ["JwtSettings:Secret"] = "proxy-test-only-secret-at-least-32-characters"
        };
        for (var i = 0; i < proxies.Length; i++)
            config[$"ReverseProxy:KnownProxies:{i}"] = proxies[i];
        return new TestServer(new WebHostBuilder()
            .UseEnvironment("Testing")
            .ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(config))
            .UseStartup<Startup>());
    }

    /// <summary>Roda o ForwardedHeadersMiddleware com as opções que o Startup configurou.</summary>
    private static async Task<string> IpVistoPeloBff(TestServer servidor, string remoto, string xff)
    {
        var opcoes = servidor.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>();
        var contexto = new DefaultHttpContext();
        contexto.Connection.RemoteIpAddress = IPAddress.Parse(remoto);
        contexto.Request.Headers["X-Forwarded-For"] = xff;
        contexto.Request.Headers["X-Forwarded-Proto"] = "https";
        var middleware = new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance, opcoes);
        await middleware.Invoke(contexto);
        return contexto.Connection.RemoteIpAddress!.ToString();
    }

    [Fact]
    public async Task Cliente_atras_do_cloudflared_e_do_edge_e_o_ip_visto()
    {
        using var servidor = Servidor(Edge, Cloudflared);
        Assert.Equal(Cliente, await IpVistoPeloBff(servidor, Edge, $"{Cliente}, {Cloudflared}"));
    }

    [Fact]
    public async Task X_Forwarded_For_forjado_a_esquerda_nao_muda_a_particao()
    {
        using var servidor = Servidor(Edge, Cloudflared);
        var honesto = await IpVistoPeloBff(servidor, Edge, $"{Cliente}, {Cloudflared}");
        var forjado = await IpVistoPeloBff(servidor, Edge, $"198.51.100.66, {Cliente}, {Cloudflared}");
        Assert.Equal(honesto, forjado);
    }

    [Fact]
    public async Task Requisicao_que_nao_vem_do_edge_nao_e_desembrulhada()
    {
        using var servidor = Servidor(Edge, Cloudflared);
        // Alguém na rede que não é proxy conhecido não escolhe o próprio IP.
        Assert.Equal("172.30.0.99", await IpVistoPeloBff(servidor, "172.30.0.99", $"{Cliente}, {Cloudflared}"));
    }

    [Fact]
    public async Task Esquema_https_do_tunnel_continua_chegando()
    {
        using var servidor = Servidor(Edge, Cloudflared);
        var opcoes = servidor.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>();
        var contexto = new DefaultHttpContext();
        contexto.Connection.RemoteIpAddress = IPAddress.Parse(Edge);
        contexto.Request.Scheme = "http";
        contexto.Request.Headers["X-Forwarded-For"] = $"{Cliente}, {Cloudflared}";
        contexto.Request.Headers["X-Forwarded-Proto"] = "https";
        await new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance, opcoes).Invoke(contexto);
        Assert.Equal("https", contexto.Request.Scheme);
    }

    [Fact]
    public void Sem_KnownProxies_o_comportamento_padrao_nao_muda()
    {
        using var servidor = Servidor();
        var opcoes = servidor.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
        Assert.Equal(1, opcoes.ForwardLimit);
        Assert.Equal(new ForwardedHeadersOptions().KnownProxies.Count, opcoes.KnownProxies.Count);
    }

    [Fact]
    public void Preview_com_um_proxy_continua_com_um_salto()
    {
        using var servidor = Servidor("172.29.0.10");
        Assert.Equal(1, servidor.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value.ForwardLimit);
    }
}
