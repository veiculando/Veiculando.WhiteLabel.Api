using System;
using System.Collections.Generic;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests
{
    public class WlPublicLinksTests
    {
        [Fact]
        public void Usa_https_do_host_resolvido_quando_nao_existe_override()
        {
            Criar(null).Convite("token", "qa+teste@example.com").Should()
                .Be("https://exibidora.teste/login/primeiro-acesso?token=token&email=qa%2Bteste%40example.com");
        }

        [Fact]
        public void Preview_http_requer_origem_explicita_ambiente_preview_e_opt_in()
        {
            Criar("http://exibidora.teste:9080", "Preview", true).Recuperacao("token", "qa@example.com")
                .Should().StartWith("http://exibidora.teste:9080/login/alterar-senha?");
            Action producao = () => Criar("http://exibidora.teste:9080", "Production", true).Convite("t", "e");
            Action semOptIn = () => Criar("http://exibidora.teste:9080", "Preview", false).Convite("t", "e");
            producao.Should().Throw<InvalidOperationException>();
            semOptIn.Should().Throw<InvalidOperationException>();
        }

        [Theory]
        [InlineData("https://outro-tenant.teste")]
        [InlineData("https://exibidora.teste/caminho")]
        [InlineData("https://user:password@exibidora.teste")]
        [InlineData("https://exibidora.teste/?redirect=outro")]
        [InlineData("javascript:alert(1)")]
        public void Recusa_origens_que_alteram_tenant_ou_nao_sao_origem_pura(string origem)
        {
            Action acao = () => Criar(origem).Convite("t", "e");
            acao.Should().Throw<InvalidOperationException>();
        }

        private static WlPublicLinks Criar(string origem, string ambiente = "Production", bool allowHttp = false)
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
            {
                ["WlPublicOrigins:Hosts:exibidora.teste"] = origem,
                ["WlPublicOrigins:AllowHttpPreview"] = allowHttp.ToString()
            }).Build();
            var tenant = new TenantContext();
            tenant.Definir(new WlTenantInfo { AfiliadaId = 1, Host = "exibidora.teste" });
            return new WlPublicLinks(tenant, config, new Ambiente { EnvironmentName = ambiente });
        }

        private sealed class Ambiente : IHostEnvironment
        {
            public string EnvironmentName { get; set; }
            public string ApplicationName { get; set; } = "Tests";
            public string ContentRootPath { get; set; } = ".";
            public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        }
    }
}
