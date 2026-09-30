using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Veiculando.Domain.Enums;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services.Cms;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests;

/// <summary>
/// Troca de arquivo com remoção adiada, varredura e auditoria (VEI-RD-19f).
/// </summary>
public class CmsTrocaArquivoTests
{
    private const string Url = "https://cms-ficticio.supabase.co";
    private const string Publico = Url + "/storage/v1/object/public/cms-assets/";

    private static readonly CmsAutor Autor = new() { WlUsuarioId = 7, Email = "operador@exemplo.com", AfiliadaId = 42 };
    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    private readonly SupabaseEmMemoria _supabase = new();
    private readonly ListaLogger<CmsTrocaArquivo> _logTroca = new();
    private readonly ListaLogger<CmsAuditoria> _logAuditoria = new();
    private readonly ListaLogger<CmsVarreduraArquivos> _logVarredura = new();
    private readonly RelogioFixo _relogio;

    public CmsTrocaArquivoTests() => _relogio = new RelogioFixo(_supabase);

    // ---------- troca ----------

    [Fact]
    public async Task Troca_em_registro_ativo_nao_remove_o_antigo_e_deixa_pendente()
    {
        var banner = Banner(ativo: true, imagem: "banners/antigo-a.png");

        var resultado = await TrocarImagemAsync(banner);

        var novo = Caminho(resultado.Linha.ImageUrl);
        _supabase.Objetos.Should().Contain("cms-assets/banners/antigo-a.png", "o site ainda pode mostrar A por até 5 min");
        _supabase.Objetos.Should().Contain($"cms-assets/{novo}");

        var troca = Auditoria().Single(a => a["acao"]!.ToString() == CmsAcao.TrocarArquivo);
        troca["antes"]!["image_url"]!.ToString().Should().Be(Publico + "banners/antigo-a.png");
        troca["depois"]!["image_url"]!.ToString().Should().Be(resultado.Linha.ImageUrl);
        troca["arquivo_limpo_em"].Should().BeNull("a varredura é quem remove");
    }

    [Fact]
    public async Task Troca_em_registro_inativo_remove_o_antigo_na_hora()
    {
        var banner = Banner(ativo: false, imagem: "banners/antigo-a.png");

        await TrocarImagemAsync(banner);

        _supabase.Objetos.Should().NotContain("cms-assets/banners/antigo-a.png");
        Auditoria().Single(a => a["acao"]!.ToString() == CmsAcao.TrocarArquivo)["arquivo_limpo_em"]
            .Should().NotBeNull();
    }

    [Fact]
    public async Task Registro_que_estava_ativo_e_foi_inativado_na_mesma_troca_tambem_adia()
    {
        // O HTML em cache da LP foi gerado com o registro ativo e aponta para A.
        var banner = Banner(ativo: true, imagem: "banners/antigo-a.png");

        await TrocarImagemAsync(banner, new Dictionary<string, object> { ["is_active"] = false });

        _supabase.Objetos.Should().Contain("cms-assets/banners/antigo-a.png");
    }

    [Fact]
    public async Task Antigo_ainda_usado_por_outra_linha_nao_e_removido()
    {
        var banner = Banner(ativo: false, imagem: "logos/compartilhado.png");
        _supabase.Inserir(CmsSupabase.Marcas, new JsonObject
        {
            ["name"] = "Marca Fictícia", ["image_url"] = Publico + "logos/compartilhado.png", ["is_active"] = false,
        });

        await TrocarImagemAsync(banner);

        _supabase.Objetos.Should().Contain("cms-assets/logos/compartilhado.png");
        Auditoria().Single(a => a["acao"]!.ToString() == CmsAcao.TrocarArquivo)["arquivo_limpo_em"]
            .Should().NotBeNull("a troca não deixou nada para limpar: o arquivo tem outro dono");
    }

    [Fact]
    public async Task Url_antiga_fora_do_bucket_nunca_e_apagada()
    {
        var banner = Banner(ativo: false, imagem: null);
        Atualizar(banner, "image_url", "https://outro-servidor.exemplo/banner.png");

        await TrocarImagemAsync(banner);

        _supabase.Handler.Chamadas.Should().NotContain(c => c.Metodo == HttpMethod.Delete);
    }

    [Fact]
    public async Task Patch_com_timeout_mas_aplicado_mantem_o_novo()
    {
        // O PATCH entrou no banco e a resposta não voltou: timeout ambíguo.
        var banner = Banner(ativo: false, imagem: "banners/antigo-a.png");
        _supabase.Falhar = c => c.Metodo == HttpMethod.Patch ? SupabaseFake.Json(HttpStatusCode.GatewayTimeout, "{}") : null;
        _supabase.Aplicar = c => c.Metodo == HttpMethod.Patch;

        var resultado = await TrocarImagemAsync(banner);

        resultado.Linha.Should().NotBeNull();
        var novo = Caminho(resultado.Linha.ImageUrl);
        _supabase.Objetos.Should().Contain($"cms-assets/{novo}", "a linha aponta para ele");
        _supabase.Objetos.Should().NotContain("cms-assets/banners/antigo-a.png");
        LinhaAtual(banner)["image_url"]!.ToString().Should().Be(resultado.Linha.ImageUrl);
    }

    [Fact]
    public async Task Patch_que_falhou_remove_o_novo_e_mantem_o_antigo()
    {
        var banner = Banner(ativo: false, imagem: "banners/antigo-a.png");
        _supabase.Falhar = c => c.Metodo == HttpMethod.Patch ? SupabaseFake.Json(HttpStatusCode.ServiceUnavailable, "{}") : null;

        var acao = () => TrocarImagemAsync(banner);

        await acao.Should().ThrowAsync<CmsIndisponivelException>();
        _supabase.Objetos.Should().BeEquivalentTo(new[] { "cms-assets/banners/antigo-a.png" }, "B foi removida e A ficou");
        LinhaAtual(banner)["image_url"]!.ToString().Should().Be(Publico + "banners/antigo-a.png");
        Auditoria().Should().BeEmpty();
    }

    [Fact]
    public async Task Patch_e_releitura_falhando_nao_remove_nada_e_registra_o_caminho()
    {
        var banner = Banner(ativo: false, imagem: "banners/antigo-a.png");
        _supabase.Falhar = c => c.Metodo == HttpMethod.Patch || c.Metodo == HttpMethod.Get
            ? SupabaseFake.Json(HttpStatusCode.ServiceUnavailable, "{}")
            : null;

        var acao = () => TrocarImagemAsync(banner);

        await acao.Should().ThrowAsync<CmsIndisponivelException>();
        var novo = _supabase.Objetos.Single(o => o != "cms-assets/banners/antigo-a.png");
        _logTroca.Mensagens.Should().Contain(m => m.StartsWith("Warning") && m.Contains(novo));
        _supabase.Objetos.Should().Contain("cms-assets/banners/antigo-a.png");
    }

    [Fact]
    public async Task Falha_ao_remover_o_antigo_nao_vira_erro_e_fica_pendente()
    {
        var banner = Banner(ativo: false, imagem: "banners/antigo-a.png");
        _supabase.Falhar = c => c.Metodo == HttpMethod.Delete ? SupabaseFake.Json(HttpStatusCode.InternalServerError, "{}") : null;

        var resultado = await TrocarImagemAsync(banner);

        resultado.Linha.Should().NotBeNull("a troca deu certo; só a limpeza falhou");
        _logTroca.Mensagens.Should().Contain(m => m.StartsWith("Warning") && m.Contains("banners/antigo-a.png"));
        Auditoria().Single(a => a["acao"]!.ToString() == CmsAcao.TrocarArquivo)["arquivo_limpo_em"]
            .Should().BeNull("a varredura tenta de novo");
    }

    [Fact]
    public async Task Falha_da_auditoria_nao_desfaz_a_troca_e_vai_para_o_log_como_error()
    {
        var banner = Banner(ativo: true, imagem: "banners/antigo-a.png");
        _supabase.Falhar = c => c.Metodo == HttpMethod.Post && c.Uri.AbsolutePath.EndsWith(CmsSupabase.Auditoria)
            ? SupabaseFake.Json(HttpStatusCode.InternalServerError, "{}")
            : null;

        var resultado = await TrocarImagemAsync(banner);

        resultado.Linha.Should().NotBeNull();
        var erro = _logAuditoria.Mensagens.Should().ContainSingle(m => m.StartsWith("Error")).Subject;
        erro.Should().Contain("trocar_arquivo").And.Contain("banners/antigo-a.png").And.Contain("operador@exemplo.com")
            .And.Contain("\"afiliada_id\":42");
    }

    [Fact]
    public async Task Upload_interrompido_remove_o_que_ja_subiu()
    {
        var banner = Banner(ativo: false, imagem: "banners/antigo-a.png");
        _supabase.Falhar = c => c.Metodo == HttpMethod.Post && c.Uri.AbsolutePath.Contains("/cms-html/")
            ? SupabaseFake.Json(HttpStatusCode.ServiceUnavailable, "{}")
            : null;

        var acao = () => Troca().AtualizarAsync(CmsSupabase.Banners, banner, Linha<CmsBannerLinha>(banner),
            new Dictionary<string, object>(),
            new[]
            {
                new CmsTroca("image_url", CmsSupabase.BucketAssets, "banners", Imagem()),
                new CmsTroca("html_path", CmsSupabase.BucketHtml, "banners", Html()),
            }, Autor, CancellationToken.None);

        await acao.Should().ThrowAsync<CmsIndisponivelException>();
        _supabase.Objetos.Should().BeEquivalentTo(new[] { "cms-assets/banners/antigo-a.png" });
    }

    [Fact]
    public async Task Id_inexistente_devolve_nao_encontrada_e_remove_o_novo()
    {
        var inexistente = Guid.NewGuid();

        var resultado = await Troca().AtualizarAsync(CmsSupabase.Banners, inexistente, new CmsBannerLinha { Id = inexistente },
            new Dictionary<string, object>(),
            new[] { new CmsTroca("image_url", CmsSupabase.BucketAssets, "banners", Imagem()) },
            Autor, CancellationToken.None);

        resultado.NaoEncontrada.Should().BeTrue();
        _supabase.Objetos.Should().BeEmpty();
    }

    [Fact]
    public async Task Banner_que_deixa_de_ser_html_perde_o_hotsite()
    {
        var banner = Banner(ativo: false, imagem: "banners/a.png");
        Atualizar(banner, "html_path", "banners/hotsite-antigo.html");
        _supabase.Objetos.Add("cms-html/banners/hotsite-antigo.html");

        var resultado = await Troca().AtualizarAsync(CmsSupabase.Banners, banner, Linha<CmsBannerLinha>(banner),
            new Dictionary<string, object> { ["tipo_destino"] = "link", ["destino"] = "https://exemplo.com.br" },
            new[] { new CmsTroca("html_path", CmsSupabase.BucketHtml, "banners", null) },
            Autor, CancellationToken.None);

        resultado.Linha.HtmlPath.Should().BeNull();
        _supabase.Objetos.Should().NotContain("cms-html/banners/hotsite-antigo.html");
    }

    // ---------- varredura ----------

    [Fact]
    public async Task Varredura_remove_pendentes_com_mais_de_10_minutos_e_marca_limpo()
    {
        var velho = Pendente("banners/velho.png", minutosAtras: 11);
        var recente = Pendente("banners/recente.png", minutosAtras: 5);

        (await Varredura().VarrerAsync(CancellationToken.None)).Should().Be(1);

        _supabase.Objetos.Should().NotContain("cms-assets/banners/velho.png");
        _supabase.Objetos.Should().Contain("cms-assets/banners/recente.png", "a LP ainda pode estar servindo esse");
        LinhaAuditoria(velho)["arquivo_limpo_em"].Should().NotBeNull();
        LinhaAuditoria(recente)["arquivo_limpo_em"].Should().BeNull();
    }

    [Fact]
    public async Task Varredura_respeita_o_teto_de_20()
    {
        for (var i = 0; i < 25; i++)
            Pendente($"banners/p{i}.png", minutosAtras: 30 + i);

        (await Varredura().VarrerAsync(CancellationToken.None)).Should().Be(20);

        _supabase.Objetos.Should().HaveCount(5);
        _supabase.Handler.Chamadas.Should().Contain(c => c.Metodo == HttpMethod.Get
            && c.Alvo.Contains("cms_auditoria") && c.QueryCrua.Contains("limit=20"));
    }

    [Fact]
    public async Task Varredura_pula_caminho_ainda_referenciado_e_marca_limpo()
    {
        var pendente = Pendente("banners/em-uso.png", minutosAtras: 30);
        Banner(ativo: true, imagem: "banners/em-uso.png");

        await Varredura().VarrerAsync(CancellationToken.None);

        _supabase.Objetos.Should().Contain("cms-assets/banners/em-uso.png");
        LinhaAuditoria(pendente)["arquivo_limpo_em"].Should().NotBeNull();
    }

    [Fact]
    public async Task Falha_na_varredura_deixa_a_linha_pendente()
    {
        var pendente = Pendente("banners/velho.png", minutosAtras: 30);
        _supabase.Falhar = c => c.Metodo == HttpMethod.Delete ? SupabaseFake.Json(HttpStatusCode.InternalServerError, "{}") : null;

        (await Varredura().VarrerAsync(CancellationToken.None)).Should().Be(0);

        LinhaAuditoria(pendente)["arquivo_limpo_em"].Should().BeNull();
        _logVarredura.Mensagens.Should().Contain(m => m.StartsWith("Warning") && m.Contains("banners/velho.png"));
    }

    [Fact]
    public async Task Fila_ilegivel_nao_derruba_quem_pediu_a_varredura()
    {
        _supabase.Falhar = _ => SupabaseFake.Json(HttpStatusCode.ServiceUnavailable, "{}");

        (await Varredura().VarrerAsync(CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task Agendador_roda_a_varredura_fora_da_requisicao()
    {
        var pendente = Pendente("banners/velho.png", minutosAtras: 30);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(_relogio);
        services.AddSingleton<ISupabaseCmsClient>(_ => Client());
        services.AddSingleton(Config());
        services.AddScoped<CmsObjetos>();
        services.AddScoped<CmsVarreduraArquivos>();
        services.AddSingleton<CmsVarreduraAgendador>();
        await using var provider = services.BuildServiceProvider();

        var agendador = provider.GetRequiredService<CmsVarreduraAgendador>();
        await agendador.StartAsync(CancellationToken.None);
        try
        {
            agendador.Solicitar();
            agendador.Solicitar();
            agendador.Solicitar();

            var limite = DateTime.UtcNow.AddSeconds(10);
            while (LinhaAuditoria(pendente)["arquivo_limpo_em"] == null && DateTime.UtcNow < limite)
                await Task.Delay(20);

            LinhaAuditoria(pendente)["arquivo_limpo_em"].Should().NotBeNull();
            _supabase.Objetos.Should().NotContain("cms-assets/banners/velho.png");
        }
        finally
        {
            await agendador.StopAsync(CancellationToken.None);
        }
    }

    // ---------- auditoria e autor ----------

    [Fact]
    public async Task Auditoria_grava_snake_case_sem_id_nem_created_at()
    {
        var auditoria = new CmsAuditoria(Client(), _logAuditoria);

        await auditoria.RegistrarAsync(Autor, CmsAcao.Inativar, CmsSupabase.Depoimentos, Guid.Empty,
            new { is_active = true }, new { is_active = false });

        var corpo = JsonNode.Parse(_supabase.Handler.Chamadas.Single().Corpo)!.AsObject();
        corpo.Select(p => p.Key).Should().BeEquivalentTo(
            "wl_usuario_id", "usuario_email", "afiliada_id", "acao", "recurso", "recurso_id", "antes", "depois", "arquivo_limpo_em");
        corpo["acao"]!.ToString().Should().Be("inativar");
        corpo["antes"]!["is_active"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void Autor_vem_do_token_e_do_tenant_nunca_de_claim_de_afiliada()
    {
        var tenant = Tenant(afiliadaId: 42);
        var usuario = Usuario(("WlUsuarioId", "7"), (ClaimTypes.Email, "operador@exemplo.com"), ("AfiliadaId", "999"));

        CmsAutor.TentarLer(usuario, tenant, out var autor).Should().BeTrue();

        autor.WlUsuarioId.Should().Be(7);
        autor.Email.Should().Be("operador@exemplo.com");
        autor.AfiliadaId.Should().Be(42, "a afiliada é a do Host, resolvida no servidor");
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void Sem_claim_ou_sem_tenant_nao_ha_autor(bool comId, bool comEmail, bool comTenant)
    {
        var claims = new List<(string, string)>();
        if (comId) claims.Add(("WlUsuarioId", "7"));
        if (comEmail) claims.Add((ClaimTypes.Email, "operador@exemplo.com"));

        CmsAutor.TentarLer(Usuario(claims.ToArray()), comTenant ? Tenant(42) : new TenantContext(), out var autor)
            .Should().BeFalse();
        autor.Should().BeNull();
    }

    [Theory]
    [InlineData("Empresa A", "company=eq.%22Empresa%20A%22")]
    [InlineData("a,b", "company=eq.%22a%2Cb%22")]
    [InlineData("x)", "company=eq.%22x%29%22")]
    [InlineData("aspas\"dentro", "company=eq.%22aspas%5C%22dentro%22")]
    [InlineData("barra\\", "company=eq.%22barra%5C%5C%22")]
    [InlineData("a&limit=1", "company=eq.%22a%26limit%3D1%22")]
    public void Filtro_igual_escapa_e_codifica_o_valor(string valor, string esperado)
    {
        PostgrestFiltro.Igual("company", valor).Should().Be(esperado);
    }

    // ---------- apoio ----------

    private Task<CmsAtualizacao<CmsBannerLinha>> TrocarImagemAsync(Guid banner, Dictionary<string, object> alteracoes = null) =>
        Troca().AtualizarAsync(CmsSupabase.Banners, banner, Linha<CmsBannerLinha>(banner),
            alteracoes ?? new Dictionary<string, object>(),
            new[] { new CmsTroca("image_url", CmsSupabase.BucketAssets, "banners", Imagem()) },
            Autor, CancellationToken.None);

    private Guid Banner(bool ativo, string imagem)
    {
        if (imagem != null) _supabase.Objetos.Add($"cms-assets/{imagem}");
        var linha = _supabase.Inserir(CmsSupabase.Banners, new JsonObject
        {
            ["title"] = "Banner Fictício",
            ["image_url"] = imagem == null ? null : Publico + imagem,
            ["tipo_destino"] = "link",
            ["destino"] = "https://exemplo.com.br",
            ["html_path"] = null,
            ["display_order"] = 1,
            ["is_active"] = ativo,
        });
        return Guid.Parse(linha["id"]!.ToString());
    }

    private Guid Pendente(string caminho, int minutosAtras)
    {
        _supabase.Objetos.Add($"cms-assets/{caminho}");
        var linha = _supabase.Inserir(CmsSupabase.Auditoria, new JsonObject
        {
            ["acao"] = CmsAcao.TrocarArquivo,
            ["recurso"] = CmsSupabase.Banners,
            ["antes"] = new JsonObject { ["image_url"] = Publico + caminho },
            ["arquivo_limpo_em"] = null,
            ["created_at"] = _supabase.Agora.AddMinutes(-minutosAtras).ToString("O"),
        });
        return Guid.Parse(linha["id"]!.ToString());
    }

    private void Atualizar(Guid id, string coluna, string valor) =>
        _supabase.Tabelas[CmsSupabase.Banners].Single(l => l["id"]!.ToString() == id.ToString())[coluna] = valor;

    private T Linha<T>(Guid id) =>
        LinhaAtual(id).Deserialize<T>(SupabaseCmsClient.Json)!;

    private JsonObject LinhaAtual(Guid id) =>
        _supabase.Linhas(CmsSupabase.Banners).Single(l => l["id"]!.ToString() == id.ToString());

    private JsonObject LinhaAuditoria(Guid id) =>
        _supabase.Linhas(CmsSupabase.Auditoria).Single(l => l["id"]!.ToString() == id.ToString());

    private IReadOnlyList<JsonObject> Auditoria() => _supabase.Linhas(CmsSupabase.Auditoria);

    private static string Caminho(string url) => url[Publico.Length..];

    private CmsTrocaArquivo Troca()
    {
        var client = Client();
        return new CmsTrocaArquivo(client, new CmsObjetos(client, Config()),
            new CmsAuditoria(client, _logAuditoria), _relogio, _logTroca);
    }

    private CmsVarreduraArquivos Varredura()
    {
        var client = Client();
        return new CmsVarreduraArquivos(client, new CmsObjetos(client, Config()), _relogio, _logVarredura);
    }

    private SupabaseCmsClient Client() =>
        new(new HttpClient(_supabase.Handler, disposeHandler: false), Config(), NullLogger<SupabaseCmsClient>.Instance);

    private static CmsConfiguracao Config() => new(Options.Create(new CmsOptions
    {
        SupabaseUrl = Url,
        ServiceRoleKey = "sb_" + "secret_" + new string('t', 24),
    }), NullLogger<CmsConfiguracao>.Instance);

    private static CmsArquivo Imagem() => new() { Conteudo = Png, ContentType = "image/png", Extensao = "png" };

    private static CmsArquivo Html() =>
        new() { Conteudo = "<html></html>"u8.ToArray(), ContentType = "text/html; charset=utf-8", Extensao = "html" };

    private static ITenantContext Tenant(int afiliadaId)
    {
        var tenant = new TenantContext();
        tenant.Definir(new WlTenantInfo { AfiliadaId = afiliadaId, Host = "cms.teste", Tipo = WlDominioTipoEnum.Painel });
        return tenant;
    }

    private static ClaimsPrincipal Usuario(params (string Tipo, string Valor)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Tipo, c.Valor)), "Bearer"));

    /// <summary>O relógio do fake: "agora" é o mesmo para o BFF e para o Supabase simulado.</summary>
    private sealed class RelogioFixo : TimeProvider
    {
        private readonly SupabaseEmMemoria _supabase;
        public RelogioFixo(SupabaseEmMemoria supabase) => _supabase = supabase;
        public override DateTimeOffset GetUtcNow() => _supabase.Agora;
    }
}
