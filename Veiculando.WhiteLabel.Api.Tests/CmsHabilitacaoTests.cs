using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Veiculando.WhiteLabel.Api.Configurations;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services.Cms;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests;

/// <summary>
/// Normalização da seção Cms (sem banco).
/// </summary>
public class CmsConfiguracaoTests
{
    [Theory]
    [InlineData("https://aurum-landing-page-three.vercel.app/")]
    [InlineData("https://aurum-landing-page-three.vercel.app")]
    [InlineData("  https://aurum-landing-page-three.vercel.app//  ")]
    public void SiteUrl_sai_sem_barra_final(string configurado)
    {
        Config(siteUrl: configurado).SiteUrl.Should().Be("https://aurum-landing-page-three.vercel.app");
    }

    [Theory]
    [InlineData("http://aurum-landing-page-three.vercel.app")]
    [InlineData("aurum-landing-page-three.vercel.app")]
    [InlineData("/ofertas")]
    [InlineData("javascript:alert(1)")]
    public void SiteUrl_que_nao_e_https_absoluta_vira_null(string configurado)
    {
        Config(siteUrl: configurado).SiteUrl.Should().BeNull();
    }

    [Fact]
    public void SupabaseUrl_sai_sem_barra_final()
    {
        Config(supabaseUrl: "https://cms-ficticio.supabase.co/").SupabaseUrl
            .Should().Be("https://cms-ficticio.supabase.co");
    }

    [Theory]
    [InlineData(null, "chave")]
    [InlineData("https://cms-ficticio.supabase.co", null)]
    [InlineData("  ", "chave")]
    [InlineData("https://cms-ficticio.supabase.co", "   ")]
    public void Sem_url_ou_sem_chave_as_chaves_nao_estao_presentes(string url, string chave)
    {
        Config(supabaseUrl: url, chave: chave).ChavesPresentes.Should().BeFalse();
    }

    [Fact]
    public void Codigos_sao_comparados_com_trim_e_sem_diferenciar_maiusculas()
    {
        var config = Config(afiliadas: new[] { " prview ", "", null, "AURM, OUTRA" });

        config.AfiliadaHabilitada("PRVIEW").Should().BeTrue();
        config.AfiliadaHabilitada(" Prview ").Should().BeTrue();
        config.AfiliadaHabilitada("aurm").Should().BeTrue();
        config.AfiliadaHabilitada("OUTRA").Should().BeTrue();
        config.AfiliadaHabilitada("AF9601").Should().BeFalse();
        config.AfiliadaHabilitada("").Should().BeFalse();
        config.AfiliadaHabilitada(null).Should().BeFalse();
        config.AfiliadasHabilitadas.Should().BeEquivalentTo("prview", "AURM", "OUTRA");
    }

    [Fact]
    public void ToString_nao_expoe_a_chave()
    {
        var chave = ChaveFicticia();
        Config(chave: chave).ToString().Should().NotContain(chave);
    }

    [Fact]
    public void Branding_com_cms_e_copia_e_nao_altera_o_objeto_do_cache()
    {
        var cacheado = new WlBrandingPublico { NomeExibicao = "Marca", PrimaryColor = "#112233" };

        var ligado = cacheado.ComCms(true, "https://lp.teste");
        var desligado = cacheado.ComCms(false, "https://lp.teste");

        cacheado.CmsHabilitado.Should().BeFalse();
        cacheado.CmsSiteUrl.Should().BeNull();
        ligado.Should().NotBeSameAs(cacheado);
        ligado.NomeExibicao.Should().Be("Marca");
        ligado.CmsHabilitado.Should().BeTrue();
        ligado.CmsSiteUrl.Should().Be("https://lp.teste");
        desligado.CmsSiteUrl.Should().BeNull("com o módulo desligado o front não recebe URL nenhuma");
    }

    internal static string ChaveFicticia() => "sb_" + "secret_" + new string('t', 24);

    private static CmsConfiguracao Config(
        string supabaseUrl = "https://cms-ficticio.supabase.co",
        string chave = "chave",
        string siteUrl = null,
        string[] afiliadas = null) =>
        new(Options.Create(new CmsOptions
        {
            SupabaseUrl = supabaseUrl,
            ServiceRoleKey = chave,
            SiteUrl = siteUrl,
            AfiliadasHabilitadas = afiliadas,
        }), NullLogger<CmsConfiguracao>.Instance);
}

/// <summary>
/// Habilitação por afiliada, branding e o 404 do módulo desligado (VEI-RD-19b).
/// </summary>
[Collection(DatabaseCollection.Nome)]
public class CmsHabilitacaoTests
{
    private const string SiteUrlPreview = "https://aurum-landing-page-three.vercel.app/";

    private readonly SqlServerFixture _db;

    public CmsHabilitacaoTests(SqlServerFixture db) => _db = db;

    [Fact]
    public async Task Afiliada_habilitada_expoe_cms_no_branding_com_site_url_sem_barra()
    {
        const int afiliada = 9601;
        using var factory = await CriarAsync(afiliada, ConfigCompleta(habilitadas: "AF9601"));

        var branding = await BrandingAsync(factory);

        branding["cmsHabilitado"]!.GetValue<bool>().Should().BeTrue();
        branding["cmsSiteUrl"]!.GetValue<string>().Should().Be("https://aurum-landing-page-three.vercel.app");
        branding["nomeExibicao"]!.GetValue<string>().Should().Be("Exibidora 9601", "o branding existente continua vindo");
    }

    [Fact]
    public async Task Afiliada_fora_da_lista_nao_carrega_o_modulo_mesmo_com_chaves_e_permissao()
    {
        // O cenário que a habilitação por afiliada existe para barrar: o BFF é
        // compartilhado, as chaves estão no ambiente e o admin de outra exibidora
        // tem permissão.
        const int afiliada = 9602;
        using var factory = await CriarAsync(afiliada, ConfigCompleta(habilitadas: "AF9601"));
        var email = "cms-fora-lista@exemplo.com";
        await Seed.OperadorAsync(afiliada, email, new[] { AuthorizationSetup.UsuarioAfiliadaGerenciar });

        var branding = await BrandingAsync(factory);
        branding["cmsHabilitado"]!.GetValue<bool>().Should().BeFalse();
        branding.AsObject().ContainsKey("cmsSiteUrl").Should().BeTrue("o campo vem presente, com null");
        branding["cmsSiteUrl"].Should().BeNull();

        using var autenticado = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);
        (await autenticado.GetAsync("/api/wl/cms/_sonda")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var anonimo = factory.ClienteAnonimo();
        (await anonimo.GetAsync("/api/wl/cms/_sonda")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task Sem_chaves_responde_404_com_ou_sem_permissao(bool comUrl, bool comChave)
    {
        var afiliada = 9603 + (comUrl ? 1 : 0) + (comChave ? 2 : 0);
        var config = new Dictionary<string, string?>
        {
            ["Cms:SiteUrl"] = SiteUrlPreview,
            ["Cms:AfiliadasHabilitadas:0"] = $"AF{afiliada}",
        };
        if (comUrl) config["Cms:SupabaseUrl"] = "https://cms-ficticio.supabase.co";
        if (comChave) config["Cms:ServiceRoleKey"] = CmsConfiguracaoTests.ChaveFicticia();

        using var factory = await CriarAsync(afiliada, config);
        var comPermissao = $"cms-sem-chave-{afiliada}@exemplo.com";
        var semPermissao = $"cms-sem-chave-sem-perm-{afiliada}@exemplo.com";
        await Seed.OperadorAsync(afiliada, comPermissao, new[] { AuthorizationSetup.UsuarioAfiliadaGerenciar });
        await Seed.OperadorAsync(afiliada, semPermissao);

        (await BrandingAsync(factory))["cmsHabilitado"]!.GetValue<bool>().Should().BeFalse();

        using var clienteCom = await factory.ClienteAutenticadoAsync(comPermissao, Seed.SenhaPadrao);
        (await clienteCom.GetAsync("/api/wl/cms/_sonda")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var clienteSem = await factory.ClienteAutenticadoAsync(semPermissao, Seed.SenhaPadrao);
        (await clienteSem.GetAsync("/api/wl/cms/_sonda")).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "404, e não 403: o 403 confirmaria que o módulo existe");
    }

    [Fact]
    public async Task Modulo_ligado_deixa_a_autorizacao_decidir()
    {
        // Contraprova da ordem: com o módulo ligado, o middleware sai do caminho e
        // quem responde é a autorização. Se o 404 dos outros testes viesse do
        // roteamento, este também daria 404.
        const int afiliada = 9608;
        using var factory = await CriarAsync(afiliada, ConfigCompleta(habilitadas: "af9608"));
        var comPermissao = "cms-ligado-com@exemplo.com";
        var semPermissao = "cms-ligado-sem@exemplo.com";
        await Seed.OperadorAsync(afiliada, comPermissao, new[] { AuthorizationSetup.UsuarioAfiliadaGerenciar });
        await Seed.OperadorAsync(afiliada, semPermissao);

        using var anonimo = factory.ClienteAnonimo();
        (await anonimo.GetAsync("/api/wl/cms/_sonda")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var clienteSem = await factory.ClienteAutenticadoAsync(semPermissao, Seed.SenhaPadrao);
        (await clienteSem.GetAsync("/api/wl/cms/_sonda")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var clienteCom = await factory.ClienteAutenticadoAsync(comPermissao, Seed.SenhaPadrao);
        (await clienteCom.GetAsync("/api/wl/cms/_sonda")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Sem_nenhuma_configuracao_cms_o_branding_continua_e_o_modulo_fica_desligado()
    {
        // O estado de toda exibidora que não é a Aurum: nenhuma chave Cms no
        // ambiente. O branding não pode quebrar por causa do módulo.
        const int afiliada = 9609;
        using var factory = await CriarAsync(afiliada, new Dictionary<string, string?>());

        var branding = await BrandingAsync(factory);

        branding["cmsHabilitado"]!.GetValue<bool>().Should().BeFalse();
        branding["cmsSiteUrl"].Should().BeNull();

        using var anonimo = factory.ClienteAnonimo();
        var resposta = await anonimo.GetAsync("/api/wl/cms/_sonda");
        resposta.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await resposta.Content.ReadAsStringAsync()).Should().Contain("\"message\"", "o 404 do módulo segue o contrato { message }");
    }

    private async Task<WlApiFactory> CriarAsync(int afiliada, IReadOnlyDictionary<string, string?> config)
    {
        var host = $"cms-{afiliada}.teste";
        await Seed.DominioAsync(afiliada, host, ativo: true);
        await Seed.BrandingAsync(afiliada, $"Exibidora {afiliada}", "https://cdn.teste/logo.svg", "#112233");
        return new WlApiFactory(_db, afiliada, host, config, comSondaCms: true);
    }

    private static Dictionary<string, string?> ConfigCompleta(string habilitadas) => new()
    {
        ["Cms:SupabaseUrl"] = "https://cms-ficticio.supabase.co/",
        ["Cms:ServiceRoleKey"] = CmsConfiguracaoTests.ChaveFicticia(),
        ["Cms:SiteUrl"] = SiteUrlPreview,
        ["Cms:AfiliadasHabilitadas:0"] = habilitadas,
    };

    private static async Task<JsonNode> BrandingAsync(WlApiFactory factory)
    {
        using var client = factory.ClienteAnonimo();
        var resposta = await client.GetAsync("/api/wl/config/branding");
        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonNode.Parse(await resposta.Content.ReadAsStringAsync())!;
    }
}
