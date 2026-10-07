using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using FluentAssertions;
using Veiculando.WhiteLabel.Api.Configurations;
using Veiculando.WhiteLabel.Api.Services.Cms;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests;

/// <summary>
/// api/wl/cms/{banners,marcas,depoimentos} de ponta a ponta, com o Supabase em
/// memória (VEI-RD-19e). O CI nunca chama o Supabase real.
/// </summary>
[Collection(DatabaseCollection.Nome)]
public class CmsControllersTests
{
    private const string SupabaseUrl = "https://cms-ficticio.supabase.co";
    private const string Publico = SupabaseUrl + "/storage/v1/object/public/cms-assets/";
    private const string Uuid = "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}";

    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D };
    private static readonly string Chave = "sb_" + "secret_" + new string('c', 30);

    private readonly SqlServerFixture _db;

    /// <summary>E-mail do operador do último AmbienteAsync: único por chamada, porque os Theory repetem a afiliada.</summary>
    private string _email;

    public CmsControllersTests(SqlServerFixture db) => _db = db;

    // ---------- autorização ----------

    [Fact]
    public async Task Sem_ConteudoGerenciar_responde_403()
    {
        var (factory, _, _) = await AmbienteAsync(9620, AuthorizationSetup.UsuarioAfiliadaGerenciar);
        using var _f = factory;
        using var client = await factory.ClienteAutenticadoAsync(_email, Seed.SenhaPadrao);

        (await client.GetAsync("/api/wl/cms/banners")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsync("/api/wl/cms/marcas", Form(("name", "X")))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync("/api/wl/cms/depoimentos/resumo")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        factory.Supabase.Handler.Chamadas.Should().BeEmpty();
    }

    // ---------- listagem ----------

    [Fact]
    public async Task Lista_paginada_no_padrao_WlPagina_com_total_do_content_range()
    {
        var (factory, client, _) = await AmbienteAsync(9621);
        using var _f = factory;
        using var _c = client;
        for (var i = 1; i <= 12; i++)
            factory.Supabase.Inserir(CmsSupabase.Banners, BannerLinha($"Banner Fictício {i}", ordem: i));

        var pagina = await JsonAsync(await client.GetAsync("/api/wl/cms/banners?status=inativo&page=2&pageSize=10"));

        pagina.AsObject().Select(p => p.Key).Should().BeEquivalentTo("itens", "page", "pageSize", "total", "totalPaginas");
        pagina["page"]!.GetValue<int>().Should().Be(2);
        pagina["total"]!.GetValue<int>().Should().Be(12);
        pagina["totalPaginas"]!.GetValue<int>().Should().Be(2);
        pagina["itens"]!.AsArray().Should().HaveCount(2);

        var fixture = Fixture("cms-banners-lista")["itens"]![0]!.AsObject().Select(p => p.Key);
        pagina["itens"]![0]!.AsObject().Select(p => p.Key).Should().BeEquivalentTo(fixture);

        var chamada = factory.Supabase.Handler.Chamadas.Last();
        chamada.Alvo.Should().Contain("is_active=eq.false").And.Contain("limit=10").And.Contain("offset=10");

        var alem = await JsonAsync(await client.GetAsync("/api/wl/cms/banners?page=5&pageSize=10"));
        alem["itens"]!.AsArray().Should().BeEmpty();
        alem["total"]!.GetValue<int>().Should().Be(12);
    }

    [Theory]
    [InlineData("a,b", "\"*a,b*\"")]
    [InlineData("x)", "\"*x)*\"")]
    [InlineData("50%", "\"*50\\\\%*\"")]
    [InlineData("a_b", "\"*a\\\\_b*\"")]
    [InlineData("\"", "\"*\\\"*\"")]
    [InlineData("title.eq.1", "\"*title.eq.1*\"")]
    public async Task Busca_com_caracteres_especiais_nao_injeta_filtro(string busca, string valorEsperado)
    {
        var (factory, client, _) = await AmbienteAsync(9622);
        using var _f = factory;
        using var _c = client;

        var resposta = await client.GetAsync($"/api/wl/cms/banners?busca={Uri.EscapeDataString(busca)}");

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
        var parametros = Parametros(factory.Supabase.Handler.Chamadas.Last());
        parametros.Keys.Should().BeEquivalentTo(new[] { "select", "or", "order", "limit", "offset" }, "nenhum filtro extra");
        parametros["or"].Should().Be($"(title.ilike.{valorEsperado},destino.ilike.{valorEsperado})");
    }

    [Fact]
    public async Task Busca_so_de_asterisco_nao_filtra()
    {
        var (factory, client, _) = await AmbienteAsync(9623);
        using var _f = factory;
        using var _c = client;

        (await client.GetAsync("/api/wl/cms/banners?busca=*")).StatusCode.Should().Be(HttpStatusCode.OK);

        Parametros(factory.Supabase.Handler.Chamadas.Last()).Keys.Should().NotContain("or");
    }

    [Fact]
    public async Task Busca_por_percentual_e_literal()
    {
        var (factory, client, _) = await AmbienteAsync(9624);
        using var _f = factory;
        using var _c = client;
        factory.Supabase.Inserir(CmsSupabase.Banners, BannerLinha("Oferta 50% off"));
        factory.Supabase.Inserir(CmsSupabase.Banners, BannerLinha("Oferta 500 unidades"));

        var pagina = await JsonAsync(await client.GetAsync("/api/wl/cms/banners?busca=50%25"));

        pagina["itens"]!.AsArray().Select(i => i!["title"]!.GetValue<string>()).Should().Equal("Oferta 50% off");
    }

    [Fact]
    public async Task Status_invalido_e_400()
    {
        var (factory, client, _) = await AmbienteAsync(9625);
        using var _f = factory;
        using var _c = client;

        var resposta = await client.GetAsync("/api/wl/cms/marcas?status=publicado");

        resposta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonAsync(resposta))["message"].Should().NotBeNull();
    }

    [Fact]
    public async Task Filtros_de_empresa_e_data_em_depoimentos()
    {
        var (factory, client, _) = await AmbienteAsync(9626);
        using var _f = factory;
        using var _c = client;

        (await client.GetAsync("/api/wl/cms/depoimentos?empresa=B&desde=2026-09-01")).StatusCode.Should().Be(HttpStatusCode.OK);

        var chamada = factory.Supabase.Handler.Chamadas.Last();
        chamada.QueryCrua.Should().Contain("company=eq.%22B%22");
        chamada.QueryCrua.Should().Contain("created_at=gte.2026-09-01T00%3A00%3A00-03%3A00");

        var invalida = await client.GetAsync("/api/wl/cms/depoimentos?desde=01-09-2026");
        invalida.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---------- criação ----------

    [Fact]
    public async Task Criar_banner_grava_arquivo_com_nome_gerado_e_audita()
    {
        var (factory, client, operadorId) = await AmbienteAsync(9627);
        using var _f = factory;
        using var _c = client;
        factory.Supabase.Inserir(CmsSupabase.Banners, BannerLinha("Existente", ordem: 4));

        var resposta = await client.PostAsync("/api/wl/cms/banners", Form(
            ("title", "Banner Fictício Novo"), ("tipoDestino", "link"), ("destino", "https://exemplo.com.br/oferta"),
            ("ativo", "false"), Arquivo("imagem", "foto-do-cliente.png", Png)));

        resposta.StatusCode.Should().Be(HttpStatusCode.Created);
        var salvo = await JsonAsync(resposta);
        salvo.AsObject().Select(p => p.Key).Should().BeEquivalentTo("item", "avisos");
        salvo["avisos"]!.AsArray().Should().BeEmpty();

        var item = salvo["item"]!;
        item["imageUrl"]!.GetValue<string>().Should().MatchRegex($"^{Publico.Replace(".", "\\.")}banners/{Uuid}\\.png$");
        item["displayOrder"]!.GetValue<int>().Should().Be(5, "max + 1");
        item["ativo"]!.GetValue<bool>().Should().BeFalse();
        factory.Supabase.Objetos.Should().ContainSingle(o => o.StartsWith("cms-assets/banners/") && !o.Contains("foto-do-cliente"));

        var auditoria = factory.Supabase.Linhas(CmsSupabase.Auditoria).Single();
        auditoria["acao"]!.ToString().Should().Be("criar");
        auditoria["wl_usuario_id"]!.GetValue<int>().Should().Be(operadorId);
        auditoria["usuario_email"]!.ToString().Should().Be(_email);
        auditoria["afiliada_id"]!.GetValue<int>().Should().Be(9627);
    }

    [Fact]
    public async Task Post_sem_ativo_nasce_inativo()
    {
        var (factory, client, _) = await AmbienteAsync(9628);
        using var _f = factory;
        using var _c = client;

        var resposta = await client.PostAsync("/api/wl/cms/depoimentos", Form(
            ("author", "Pessoa Fictícia"), ("content", "Depoimento fictício.")));

        resposta.StatusCode.Should().Be(HttpStatusCode.Created);
        var insert = factory.Supabase.Handler.Chamadas.Single(c => c.Metodo == HttpMethod.Post && c.Alvo == "/rest/v1/depoimentos");
        JsonNode.Parse(insert.Corpo)!["is_active"]!.GetValue<bool>().Should().BeFalse("is_active vai sempre, mesmo sem o campo");
        (await JsonAsync(resposta))["item"]!["ativo"]!.GetValue<bool>().Should().BeFalse();
    }

    [Theory]
    [InlineData("http://exemplo.com.br")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/ofertas")]
    [InlineData("")]
    public async Task Destino_inseguro_e_rejeitado(string destino)
    {
        var (factory, client, _) = await AmbienteAsync(9629);
        using var _f = factory;
        using var _c = client;

        var resposta = await client.PostAsync("/api/wl/cms/banners", Form(
            ("title", "Banner Fictício"), ("tipoDestino", "link"), ("destino", destino), Arquivo("imagem", "a.png", Png)));

        resposta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonAsync(resposta))["message"]!.GetValue<string>().Should().Contain("https://");
        factory.Supabase.Objetos.Should().BeEmpty();
    }

    [Fact]
    public async Task Banner_html_sem_arquivo_e_rejeitado()
    {
        var (factory, client, _) = await AmbienteAsync(9630);
        using var _f = factory;
        using var _c = client;

        var resposta = await client.PostAsync("/api/wl/cms/banners", Form(
            ("title", "Hotsite Fictício"), ("tipoDestino", "html"), Arquivo("imagem", "a.png", Png)));

        resposta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        factory.Supabase.Objetos.Should().BeEmpty();
    }

    [Fact]
    public async Task Hotsite_com_url_relativa_gera_aviso_e_grava_no_cms_html()
    {
        var (factory, client, _) = await AmbienteAsync(9631);
        using var _f = factory;
        using var _c = client;

        var resposta = await client.PostAsync("/api/wl/cms/banners", Form(
            ("title", "Hotsite Fictício"), ("tipoDestino", "html"), ("destino", "https://ignorado.exemplo"),
            Arquivo("imagem", "a.png", Png),
            Arquivo("html", "oferta.html", Encoding.UTF8.GetBytes("<html><body><img src='img/a.png'></body></html>"))));

        resposta.StatusCode.Should().Be(HttpStatusCode.Created);
        var salvo = await JsonAsync(resposta);
        salvo["item"]!["htmlPath"]!.GetValue<string>().Should().MatchRegex($"^banners/{Uuid}\\.html$");
        salvo["item"]!["destino"].Should().BeNull("banner html não tem destino");
        salvo["avisos"]!.AsArray().Select(a => a!.GetValue<string>())
            .Should().Equal(Fixture("cms-banner-salvo-avisos")["avisos"]!.AsArray().Select(a => a!.GetValue<string>()));
        factory.Supabase.Objetos.Should().Contain($"cms-html/{salvo["item"]!["htmlPath"]!.GetValue<string>()}");
    }

    [Fact]
    public async Task Upload_com_magic_bytes_errados_nao_grava_nada()
    {
        var (factory, client, _) = await AmbienteAsync(9632);
        using var _f = factory;
        using var _c = client;

        var resposta = await client.PostAsync("/api/wl/cms/marcas", Form(
            ("name", "Marca Fictícia"), Arquivo("imagem", "logo.png", Encoding.ASCII.GetBytes("%PDF-1.7 fictício"), "image/png")));

        resposta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        factory.Supabase.Handler.Chamadas.Should().NotContain(c => c.Metodo == HttpMethod.Post, "nem Storage nem tabela");
    }

    [Fact]
    public async Task Svg_com_script_e_rejeitado_e_svg_limpo_so_vale_em_marcas()
    {
        var (factory, client, _) = await AmbienteAsync(9633);
        using var _f = factory;
        using var _c = client;
        var limpo = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><rect width=\"1\" height=\"1\"/></svg>");
        var sujo = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"alert(1)\"/>");

        var comScript = await client.PostAsync("/api/wl/cms/marcas", Form(("name", "Marca Fictícia"), Arquivo("imagem", "l.svg", sujo)));
        comScript.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonAsync(comScript))["message"]!.GetValue<string>().Should().StartWith("SVG inseguro");

        (await client.PostAsync("/api/wl/cms/marcas", Form(("name", "Marca Fictícia"), Arquivo("imagem", "l.svg", limpo))))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        (await client.PostAsync("/api/wl/cms/banners", Form(
                ("title", "Banner Fictício"), ("destino", "#contato"), Arquivo("imagem", "l.svg", limpo))))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Corpo_acima_de_8_mb_recebe_413()
    {
        var (factory, client, _) = await AmbienteAsync(9634);
        using var _f = factory;
        using var _c = client;
        var grande = new byte[9 * 1024 * 1024];
        Png.CopyTo(grande, 0);

        var resposta = await client.PostAsync("/api/wl/cms/banners", Form(
            ("title", "Banner Fictício"), ("destino", "#contato"), Arquivo("imagem", "g.png", grande)));

        resposta.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        factory.Supabase.Objetos.Should().BeEmpty();
    }

    [Fact]
    public async Task Falha_da_auditoria_nao_derruba_a_escrita()
    {
        var (factory, client, _) = await AmbienteAsync(9635);
        using var _f = factory;
        using var _c = client;
        factory.Supabase.Falhar = c => c.Metodo == HttpMethod.Post && c.Alvo == "/rest/v1/cms_auditoria"
            ? SupabaseFake.Json(HttpStatusCode.InternalServerError, "{}")
            : null;

        var resposta = await client.PostAsync("/api/wl/cms/depoimentos", Form(("author", "Pessoa Fictícia"), ("content", "Texto.")));

        resposta.StatusCode.Should().Be(HttpStatusCode.Created);
        factory.Supabase.Linhas(CmsSupabase.Depoimentos).Should().ContainSingle();
    }

    // ---------- atualização, status e troca ----------

    [Fact]
    public async Task Troca_de_imagem_em_banner_ativo_adia_a_remocao_e_audita()
    {
        var (factory, client, _) = await AmbienteAsync(9636);
        using var _f = factory;
        using var _c = client;
        factory.Supabase.Objetos.Add("cms-assets/banners/antigo.png");
        var linha = factory.Supabase.Inserir(CmsSupabase.Banners,
            BannerLinha("Banner Fictício", ativo: true, imagem: Publico + "banners/antigo.png"));
        var id = linha["id"]!.ToString();

        var resposta = await client.PutAsync($"/api/wl/cms/banners/{id}", Form(
            ("title", "Banner Fictício Editado"), ("destino", "#contato"), Arquivo("imagem", "novo.png", Png)));

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
        var item = (await JsonAsync(resposta))["item"]!;
        item["title"]!.GetValue<string>().Should().Be("Banner Fictício Editado");
        item["ativo"]!.GetValue<bool>().Should().BeTrue("PUT sem ativo mantém o status");
        factory.Supabase.Objetos.Should().Contain("cms-assets/banners/antigo.png", "registro ativo: a varredura remove depois");

        var acoes = factory.Supabase.Linhas(CmsSupabase.Auditoria).Select(a => a["acao"]!.ToString());
        acoes.Should().BeEquivalentTo("trocar_arquivo", "editar");
    }

    [Fact]
    public async Task Varredura_roda_na_escrita_de_outro_recurso()
    {
        var (factory, client, _) = await AmbienteAsync(9637);
        using var _f = factory;
        using var _c = client;
        factory.Supabase.Objetos.Add("cms-assets/banners/velho.png");
        var pendente = factory.Supabase.Inserir(CmsSupabase.Auditoria, new JsonObject
        {
            ["acao"] = "trocar_arquivo",
            ["recurso"] = CmsSupabase.Banners,
            ["antes"] = new JsonObject { ["image_url"] = Publico + "banners/velho.png" },
            ["arquivo_limpo_em"] = null,
            ["created_at"] = DateTimeOffset.UtcNow.AddMinutes(-30).ToString("O"),
        });

        (await client.PostAsync("/api/wl/cms/depoimentos", Form(("author", "Pessoa Fictícia"), ("content", "Texto."))))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var limite = DateTime.UtcNow.AddSeconds(10);
        while (factory.Supabase.Objetos.Contains("cms-assets/banners/velho.png") && DateTime.UtcNow < limite)
            await Task.Delay(50);

        factory.Supabase.Objetos.Should().NotContain("cms-assets/banners/velho.png");
        factory.Supabase.Linhas(CmsSupabase.Auditoria).Single(a => a["id"]!.ToString() == pendente["id"]!.ToString())
            ["arquivo_limpo_em"].Should().NotBeNull();
    }

    /// <summary>
    /// Os 9 endpoints de escrita do CMS, um por um: cada um grava auditoria e
    /// dispara a varredura.
    /// </summary>
    /// <remarks>
    /// Os outros testes provam isso por amostragem, e amostragem não fecha o
    /// critério da 19f ("toda escrita audita", "a varredura roda em QUALQUER
    /// escrita"): um endpoint novo que esqueça de auditar passaria despercebido.
    /// Aqui cada escrita começa com um arquivo pendente de mais de 10 minutos e
    /// precisa deixá-lo limpo.
    /// </remarks>
    [Theory]
    [InlineData("banners", "POST", "criar")]
    [InlineData("banners", "PUT", "editar")]
    [InlineData("banners", "PATCH", "inativar")]
    [InlineData("marcas", "POST", "criar")]
    [InlineData("marcas", "PUT", "editar")]
    [InlineData("marcas", "PATCH", "inativar")]
    [InlineData("depoimentos", "POST", "criar")]
    [InlineData("depoimentos", "PUT", "editar")]
    [InlineData("depoimentos", "PATCH", "inativar")]
    public async Task Toda_escrita_audita_e_dispara_a_varredura(string recurso, string metodo, string acaoEsperada)
    {
        var (factory, client, _) = await AmbienteAsync(9650);
        using var _f = factory;
        using var _c = client;

        // Um arquivo antigo esperando limpeza há mais de 10 min, de um recurso
        // que NÃO é o que vai ser escrito.
        const string pendenteCaminho = "cms-assets/marcas/pendente.png";
        factory.Supabase.Objetos.Add(pendenteCaminho);
        var pendente = factory.Supabase.Inserir(CmsSupabase.Auditoria, new JsonObject
        {
            ["acao"] = "trocar_arquivo",
            ["recurso"] = CmsSupabase.Marcas,
            ["antes"] = new JsonObject { ["image_url"] = Publico + "marcas/pendente.png" },
            ["arquivo_limpo_em"] = null,
            ["created_at"] = DateTimeOffset.UtcNow.AddMinutes(-30).ToString("O"),
        });

        var existente = SemearParaEscrita(factory, recurso);
        var auditoriaAntes = factory.Supabase.Linhas(CmsSupabase.Auditoria).Count;

        var resposta = metodo switch
        {
            "POST" => await client.PostAsync($"/api/wl/cms/{recurso}", CorpoDe(recurso)),
            "PUT" => await client.PutAsync($"/api/wl/cms/{recurso}/{existente}", CorpoDe(recurso, comArquivo: false)),
            _ => await client.PatchAsJsonAsync($"/api/wl/cms/{recurso}/{existente}/status", new { ativo = false }),
        };

        resposta.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);

        var novas = factory.Supabase.Linhas(CmsSupabase.Auditoria).Skip(auditoriaAntes).ToList();
        novas.Select(a => a["acao"]!.ToString()).Should().Contain(acaoEsperada,
            $"{metodo} de {recurso} precisa gravar em cms_auditoria");
        novas.Should().OnlyContain(a => a["wl_usuario_id"] != null && a["usuario_email"] != null && a["afiliada_id"] != null);

        var limite = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < limite && (
            factory.Supabase.Objetos.Contains(pendenteCaminho) ||
            factory.Supabase.Linhas(CmsSupabase.Auditoria)
                .Single(a => a["id"]!.ToString() == pendente["id"]!.ToString())["arquivo_limpo_em"] == null))
            await Task.Delay(50);

        factory.Supabase.Objetos.Should().NotContain(pendenteCaminho,
            $"{metodo} de {recurso} precisa disparar a varredura");
        factory.Supabase.Linhas(CmsSupabase.Auditoria)
            .Single(a => a["id"]!.ToString() == pendente["id"]!.ToString())["arquivo_limpo_em"]
            .Should().NotBeNull();
    }

    /// <summary>Uma linha ativa do recurso, para o PUT e o PATCH terem alvo.</summary>
    private static string SemearParaEscrita(WlApiFactory factory, string recurso)
    {
        var linha = recurso switch
        {
            "banners" => BannerLinha("Banner Fictício", ativo: true),
            "marcas" => new JsonObject
            {
                ["name"] = "Marca Fictícia",
                ["image_url"] = Publico + $"marcas/{Guid.NewGuid()}.png",
                ["display_order"] = 1,
                ["is_active"] = true,
            },
            _ => new JsonObject
            {
                ["author"] = "Pessoa Fictícia",
                ["content"] = "Depoimento fictício.",
                ["display_order"] = 1,
                ["is_active"] = true,
            },
        };

        return factory.Supabase.Inserir(recurso, linha)["id"]!.ToString();
    }

    private static MultipartFormDataContent CorpoDe(string recurso, bool comArquivo = true)
    {
        var partes = new List<object>();
        switch (recurso)
        {
            case "banners":
                partes.Add(("title", "Banner Fictício"));
                partes.Add(("destino", "#contato"));
                break;
            case "marcas":
                partes.Add(("name", "Marca Fictícia"));
                break;
            default:
                partes.Add(("author", "Pessoa Fictícia"));
                partes.Add(("content", "Depoimento fictício."));
                break;
        }

        // Imagem obrigatória no POST de banners e marcas; no PUT é opcional, e a
        // omissão exercita o caminho sem troca de arquivo.
        if (comArquivo && recurso != "depoimentos")
            partes.Add(Arquivo("imagem", "a.png", Png));

        return Form(partes.ToArray());
    }

    [Fact]
    public async Task Inativar_e_a_remocao_e_nao_ha_delete()
    {
        var (factory, client, _) = await AmbienteAsync(9638);
        using var _f = factory;
        using var _c = client;
        var linha = factory.Supabase.Inserir(CmsSupabase.Depoimentos, new JsonObject
        {
            ["author"] = "Pessoa Fictícia", ["content"] = "Texto.", ["is_active"] = true, ["display_order"] = 1,
        });
        var id = linha["id"]!.ToString();

        var resposta = await client.PatchAsJsonAsync($"/api/wl/cms/depoimentos/{id}/status", new { ativo = false });

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonAsync(resposta))["item"]!["ativo"]!.GetValue<bool>().Should().BeFalse();
        factory.Supabase.Linhas(CmsSupabase.Depoimentos).Single()["is_active"]!.GetValue<bool>().Should().BeFalse();
        factory.Supabase.Linhas(CmsSupabase.Auditoria).Single()["acao"]!.ToString().Should().Be("inativar");

        (await client.DeleteAsync($"/api/wl/cms/depoimentos/{id}")).StatusCode
            .Should().BeOneOf(HttpStatusCode.MethodNotAllowed, HttpStatusCode.NotFound);
        factory.Supabase.Linhas(CmsSupabase.Depoimentos).Should().ContainSingle();
    }

    [Fact]
    public async Task Id_inexistente_devolve_404()
    {
        var (factory, client, _) = await AmbienteAsync(9639);
        using var _f = factory;
        using var _c = client;
        var id = Guid.NewGuid();

        (await client.GetAsync($"/api/wl/cms/marcas/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PutAsync($"/api/wl/cms/marcas/{id}", Form(("name", "Marca Fictícia"))))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PatchAsJsonAsync($"/api/wl/cms/marcas/{id}/status", new { ativo = true }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        factory.Supabase.Objetos.Should().BeEmpty();
    }

    // ---------- resumo e indisponibilidade ----------

    [Fact]
    public async Task Resumo_de_depoimentos_vem_da_rpc()
    {
        var (factory, client, _) = await AmbienteAsync(9640);
        using var _f = factory;
        using var _c = client;
        factory.Supabase.Rpcs["cms_depoimentos_resumo"] = _ =>
            "{\"total\":5,\"publicados\":3,\"ocultos\":2,\"novosNoMes\":1,\"empresas\":2}";

        var resumo = await JsonAsync(await client.GetAsync("/api/wl/cms/depoimentos/resumo"));

        JsonNode.DeepEquals(resumo, Fixture("cms-depoimentos-resumo")).Should().BeTrue();
        factory.Supabase.Handler.Chamadas.Single().Alvo.Should().Be("/rest/v1/rpc/cms_depoimentos_resumo");
    }

    [Fact]
    public async Task Supabase_indisponivel_devolve_503_legivel_sem_a_chave()
    {
        var (factory, client, _) = await AmbienteAsync(9641);
        using var _f = factory;
        using var _c = client;
        factory.Supabase.Falhar = _ => SupabaseFake.Json(HttpStatusCode.ServiceUnavailable, "{\"message\":\"pausado\"}");

        var resposta = await client.GetAsync("/api/wl/cms/marcas");

        resposta.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var corpo = await resposta.Content.ReadAsStringAsync();
        JsonNode.DeepEquals(JsonNode.Parse(corpo), Fixture("cms-erro")).Should().BeTrue();
        corpo.Should().NotContain(Chave);
    }

    // ---------- apoio ----------

    private async Task<(WlApiFactory Factory, HttpClient Client, int OperadorId)> AmbienteAsync(
        int afiliada, string permissao = AuthorizationSetup.ConteudoGerenciar)
    {
        var host = $"cms-api-{afiliada}.teste";
        await Seed.DominioAsync(afiliada, host, ativo: true);
        _email = $"cms{afiliada}-{Guid.NewGuid().ToString("N")[..8]}@exemplo.com";
        var operadorId = await Seed.OperadorAsync(afiliada, _email, new[] { permissao });

        var factory = new WlApiFactory(_db, afiliada, host, new Dictionary<string, string?>
        {
            ["Cms:SupabaseUrl"] = SupabaseUrl,
            ["Cms:ServiceRoleKey"] = Chave,
            ["Cms:SiteUrl"] = "https://lp.exemplo",
            ["Cms:AfiliadasHabilitadas:0"] = $"AF{afiliada}",
        });

        if (permissao != AuthorizationSetup.ConteudoGerenciar)
            return (factory, null, operadorId);

        return (factory, await factory.ClienteAutenticadoAsync(_email, Seed.SenhaPadrao), operadorId);
    }

    private static JsonObject BannerLinha(string titulo, int ordem = 1, bool ativo = false, string imagem = null) => new()
    {
        ["title"] = titulo,
        ["image_url"] = imagem ?? Publico + $"banners/{Guid.NewGuid()}.png",
        ["tipo_destino"] = "link",
        ["destino"] = "#contato",
        ["html_path"] = null,
        ["display_order"] = ordem,
        ["is_active"] = ativo,
        ["updated_at"] = DateTimeOffset.UtcNow.ToString("O"),
    };

    private static MultipartFormDataContent Form(params object[] partes)
    {
        var form = new MultipartFormDataContent();
        foreach (var parte in partes)
        {
            switch (parte)
            {
                case ValueTuple<string, string> campo:
                    form.Add(new StringContent(campo.Item2), campo.Item1);
                    break;
                case (string nome, string arquivo, ByteArrayContent conteudo):
                    form.Add(conteudo, nome, arquivo);
                    break;
            }
        }
        return form;
    }

    private static (string, string, ByteArrayContent) Arquivo(string campo, string nome, byte[] bytes, string tipo = "application/octet-stream")
    {
        var conteudo = new ByteArrayContent(bytes);
        conteudo.Headers.ContentType = new MediaTypeHeaderValue(tipo);
        return (campo, nome, conteudo);
    }

    private static async Task<JsonNode> JsonAsync(HttpResponseMessage resposta) =>
        JsonNode.Parse(await resposta.Content.ReadAsStringAsync())!;

    private static JsonNode Fixture(string nome) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Contratos", nome + ".json")))!;

    private static Dictionary<string, string> Parametros(SupabaseFake.Chamada chamada) =>
        chamada.QueryCrua.Split('&').Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
}
