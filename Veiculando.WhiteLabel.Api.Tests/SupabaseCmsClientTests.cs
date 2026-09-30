using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Veiculando.WhiteLabel.Api.Controllers.Cms;
using Veiculando.WhiteLabel.Api.Services.Cms;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests;

/// <summary>
/// Client do Supabase contra o <see cref="SupabaseFake"/> (VEI-RD-19c).
/// </summary>
public class SupabaseCmsClientTests
{
    private const string Url = "https://cms-ficticio.supabase.co";

    // Montadas em runtime: nenhum literal com cara de credencial no repositório.
    private static readonly string ChaveNova = "sb_" + "secret_" + new string('n', 32);
    private static readonly string ChaveLegada = "ey" + "J" + new string('l', 40) + ".x.y";

    private readonly SupabaseFake _fake = new();
    private readonly ListaLogger<SupabaseCmsClient> _log = new();

    [Fact]
    public async Task Chave_nova_vai_so_no_apikey()
    {
        await Client(ChaveNova).SelecionarAsync<CmsMarcaLinha>(CmsSupabase.Marcas, "select=*");

        var chamada = _fake.Chamadas.Single();
        chamada.Headers["apikey"].Should().Be(ChaveNova);
        chamada.Headers.ContainsKey("Authorization").Should().BeFalse(
            "a chave sb_secret_ não é JWT; no Bearer o PostgREST a recusaria");
    }

    [Fact]
    public async Task Chave_legada_vai_no_apikey_e_no_bearer()
    {
        await Client(ChaveLegada).SelecionarAsync<CmsMarcaLinha>(CmsSupabase.Marcas, "select=*");

        var chamada = _fake.Chamadas.Single();
        chamada.Headers["apikey"].Should().Be(ChaveLegada);
        chamada.Headers["Authorization"].Should().Be($"Bearer {ChaveLegada}");
    }

    [Theory]
    [InlineData("0-9/57", 10, 57)]
    [InlineData("50-56/57", 7, 57)]
    [InlineData("*/0", 0, 0)]
    public async Task Lista_le_o_total_do_content_range(string contentRange, int itens, int total)
    {
        _fake.Responder = (_, _) => Task.FromResult(
            SupabaseFake.Json(HttpStatusCode.PartialContent, Linhas(itens), contentRange));

        var pagina = await Client().ListarAsync<CmsMarcaLinha>(CmsSupabase.Marcas, "select=*&order=display_order", 10, 50);

        pagina.Itens.Should().HaveCount(itens);
        pagina.Total.Should().Be(total);

        var chamada = _fake.Chamadas.Single();
        chamada.QueryCrua.Should().Be("select=*&order=display_order&limit=10&offset=50");
        chamada.Headers["Prefer"].Should().Be("count=exact");
        chamada.Headers.ContainsKey("Range").Should().BeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.RequestedRangeNotSatisfiable)]
    public async Task Pagina_alem_do_fim_e_vazia_com_o_total(HttpStatusCode status)
    {
        // Conforme a versão do PostgREST, offset além do total com count=exact
        // devolve 200 com [] ou 416 (PGRST103). Nos dois, a tela só vê uma página
        // vazia e o total certo para o paginador voltar.
        var corpo = status == HttpStatusCode.OK
            ? "[]"
            : "{\"code\":\"PGRST103\",\"message\":\"Requested range not satisfiable\"}";
        _fake.Responder = (_, _) => Task.FromResult(SupabaseFake.Json(status, corpo, "*/57"));

        var pagina = await Client().ListarAsync<CmsMarcaLinha>(CmsSupabase.Marcas, "select=*", 25, 100);

        pagina.Itens.Should().BeEmpty();
        pagina.Total.Should().Be(57);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task Status_inesperado_vira_indisponivel_sem_vazar_a_chave(HttpStatusCode status)
    {
        _fake.Responder = (_, _) => Task.FromResult(SupabaseFake.Json(status, "{\"message\":\"falhou\"}"));

        var acao = () => Client().SelecionarAsync<CmsMarcaLinha>(CmsSupabase.Marcas, "select=*");

        var ex = (await acao.Should().ThrowAsync<CmsIndisponivelException>()).Which;
        ex.Message.Should().NotContain(ChaveNova);
        _log.Mensagens.Should().NotContain(m => m.Contains(ChaveNova));
        _log.Mensagens.Should().Contain(m => m.Contains(((int)status).ToString()));
    }

    [Fact]
    public async Task Timeout_vira_indisponivel()
    {
        _fake.Responder = async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return SupabaseFake.Json(HttpStatusCode.OK, "[]");
        };

        var acao = () => Client(timeout: TimeSpan.FromMilliseconds(100))
            .SelecionarAsync<CmsMarcaLinha>(CmsSupabase.Marcas, "select=*");

        await acao.Should().ThrowAsync<CmsIndisponivelException>();
        _log.Mensagens.Should().NotContain(m => m.Contains(ChaveNova));
    }

    [Fact]
    public async Task Erro_de_rede_vira_indisponivel()
    {
        _fake.Responder = (_, _) => throw new HttpRequestException("conexão recusada");

        var acao = () => Client().RpcAsync<CmsDepoimentosResumoRpc>(CmsSupabase.RpcResumoDepoimentos);

        await acao.Should().ThrowAsync<CmsIndisponivelException>();
    }

    [Fact]
    public async Task Cancelamento_pelo_cliente_nao_vira_503()
    {
        // O navegador fechou a aba: não é o Supabase que está fora do ar.
        _fake.Responder = async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return SupabaseFake.Json(HttpStatusCode.OK, "[]");
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var acao = () => Client().SelecionarAsync<CmsMarcaLinha>(CmsSupabase.Marcas, "select=*", cts.Token);

        (await acao.Should().ThrowAsync<OperationCanceledException>())
            .Which.Should().NotBeOfType<CmsIndisponivelException>();
    }

    [Fact]
    public async Task Sem_chaves_nao_chama_a_rede()
    {
        var acao = () => Client(chave: null).SelecionarAsync<CmsMarcaLinha>(CmsSupabase.Marcas, "select=*");

        await acao.Should().ThrowAsync<CmsIndisponivelException>();
        _fake.Chamadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Insert_manda_snake_case_com_representation_e_devolve_a_linha()
    {
        _fake.Responder = (_, _) => Task.FromResult(SupabaseFake.Json(HttpStatusCode.Created,
            "[{\"id\":\"00000000-0000-4000-8000-000000000301\",\"author\":\"Pessoa Fictícia\",\"is_active\":false," +
            "\"display_order\":3,\"created_at\":\"2026-09-30T12:00:00.123456+00:00\"}]"));

        var linha = await Client().InserirAsync<CmsDepoimentoLinha>(CmsSupabase.Depoimentos,
            new CmsDepoimentoLinha { Author = "Pessoa Fictícia", Content = "Texto", IsActive = false, DisplayOrder = 3 });

        linha.Author.Should().Be("Pessoa Fictícia");
        linha.DisplayOrder.Should().Be(3);

        var chamada = _fake.Chamadas.Single();
        chamada.Metodo.Should().Be(HttpMethod.Post);
        chamada.Alvo.Should().Be("/rest/v1/depoimentos");
        chamada.Headers["Prefer"].Should().Be("return=representation");

        var corpo = JsonNode.Parse(chamada.Corpo)!.AsObject();
        corpo["is_active"]!.GetValue<bool>().Should().BeFalse("is_active vai sempre, mesmo false");
        corpo.ContainsKey("display_order").Should().BeTrue();
        corpo.ContainsKey("IsActive").Should().BeFalse();
    }

    [Fact]
    public async Task Patch_sem_filtro_e_recusado_antes_da_rede()
    {
        var acao = () => Client().AtualizarAsync<CmsMarcaLinha>(CmsSupabase.Marcas, " ", new { is_active = false });

        await acao.Should().ThrowAsync<ArgumentException>();
        _fake.Chamadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Patch_de_id_inexistente_devolve_lista_vazia()
    {
        // O PostgREST responde 200 com [] quando o filtro não casa. Quem decide
        // que isso é 404 é o controller.
        var linhas = await Client().AtualizarAsync<CmsMarcaLinha>(CmsSupabase.Marcas,
            "id=eq.00000000-0000-4000-8000-000000000999", new { is_active = false });

        linhas.Should().BeEmpty();
        _fake.Chamadas.Single().Metodo.Should().Be(HttpMethod.Patch);
    }

    [Fact]
    public async Task Upload_nunca_sobrescreve()
    {
        using var conteudo = new MemoryStream(new byte[] { 1, 2, 3 });

        await Client().EnviarObjetoAsync(CmsSupabase.BucketAssets, "banners/abc.png", conteudo, "image/png");

        var chamada = _fake.Chamadas.Single();
        chamada.Metodo.Should().Be(HttpMethod.Post);
        chamada.Alvo.Should().Be("/storage/v1/object/cms-assets/banners/abc.png");
        chamada.Headers["x-upsert"].Should().Be("false");
        chamada.Headers["Content-Type"].Should().Be("image/png");
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    public async Task Remover_objeto_inexistente_nao_e_erro(HttpStatusCode status, bool removido)
    {
        _fake.Responder = (_, _) => Task.FromResult(SupabaseFake.Json(status, "{}"));

        (await Client().RemoverObjetoAsync(CmsSupabase.BucketHtml, "banners/abc.html")).Should().Be(removido);

        var chamada = _fake.Chamadas.Single();
        chamada.Metodo.Should().Be(HttpMethod.Delete);
        chamada.Alvo.Should().Be("/storage/v1/object/cms-html/banners/abc.html");
    }

    [Fact]
    public async Task Resumo_vem_da_rpc_com_as_chaves_em_camel_case()
    {
        _fake.Responder = (_, _) => Task.FromResult(SupabaseFake.Json(HttpStatusCode.OK,
            "{\"total\":5,\"publicados\":3,\"ocultos\":2,\"novosNoMes\":1,\"empresas\":2}"));

        var resumo = await Client().RpcAsync<CmsDepoimentosResumoRpc>(CmsSupabase.RpcResumoDepoimentos);

        resumo.ParaDto().Should().BeEquivalentTo(new CmsDepoimentosResumoDto
        {
            Total = 5, Publicados = 3, Ocultos = 2, NovosNoMes = 1, Empresas = 2,
        }, "novosNoMes chegaria 0 se lido como novos_no_mes");

        var chamada = _fake.Chamadas.Single();
        chamada.Metodo.Should().Be(HttpMethod.Post);
        chamada.Alvo.Should().Be("/rest/v1/rpc/cms_depoimentos_resumo");
    }

    [Fact]
    public void Url_publica_e_a_que_a_lp_usa_no_img()
    {
        Client().UrlPublica(CmsSupabase.BucketAssets, "marcas/abc.webp")
            .Should().Be("https://cms-ficticio.supabase.co/storage/v1/object/public/cms-assets/marcas/abc.webp");
    }

    [Fact]
    public void Linha_anterior_a_migration_v2_vira_banner_link()
    {
        // Linhas do seed de setembro, antes das colunas novas do TP-2.
        var linha = JsonSerializer.Deserialize<CmsBannerLinha>(
            "{\"id\":\"00000000-0000-4000-8000-000000000101\",\"created_at\":\"2026-09-15T20:06:16.5+00:00\"," +
            "\"image_url\":\"https://cms-ficticio.supabase.co/x.png\",\"destino\":\"#contato\",\"is_active\":true}",
            SupabaseCmsClient.Json)!;

        var dto = linha.ParaDto();

        dto.TipoDestino.Should().Be(CmsTipoDestino.Link);
        dto.Title.Should().BeEmpty();
        dto.HtmlPath.Should().BeNull();
        dto.Ativo.Should().BeTrue();
        dto.UpdatedAt.Should().Be(dto.CreatedAt);
    }

    [Fact]
    public void Filtro_transforma_indisponivel_em_503_com_a_mensagem_da_fixture()
    {
        var contexto = new ExceptionContext(
            new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()),
            Array.Empty<IFilterMetadata>())
        {
            Exception = new CmsIndisponivelException("Supabase respondeu 503 em GET /rest/v1/marcas."),
        };

        new CmsIndisponivelFiltro(NullLogger<CmsIndisponivelFiltro>.Instance).OnException(contexto);

        contexto.ExceptionHandled.Should().BeTrue();
        var resultado = contexto.Result.Should().BeOfType<ObjectResult>().Subject;
        resultado.StatusCode.Should().Be(503);

        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Contratos", "cms-erro.json")))!;
        resultado.Value.Should().BeOfType<CmsErroDto>()
            .Which.Message.Should().Be(fixture["message"]!.GetValue<string>());
    }

    [Fact]
    public void Filtro_ignora_outras_excecoes()
    {
        var contexto = new ExceptionContext(
            new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()),
            Array.Empty<IFilterMetadata>())
        {
            Exception = new InvalidOperationException("bug"),
        };

        new CmsIndisponivelFiltro(NullLogger<CmsIndisponivelFiltro>.Instance).OnException(contexto);

        contexto.ExceptionHandled.Should().BeFalse("bug nosso é 500, não Supabase fora do ar");
    }

    private SupabaseCmsClient Client(string chave = "__nova__", TimeSpan? timeout = null)
    {
        var config = new CmsConfiguracao(Options.Create(new CmsOptions
        {
            SupabaseUrl = Url + "/",
            ServiceRoleKey = chave == "__nova__" ? ChaveNova : chave,
        }), NullLogger<CmsConfiguracao>.Instance);

        var http = new HttpClient(_fake) { Timeout = timeout ?? SupabaseCmsClient.Timeout };
        return new SupabaseCmsClient(http, config, _log);
    }

    private static string Linhas(int quantidade) =>
        "[" + string.Join(",", Enumerable.Range(1, quantidade).Select(i =>
            $"{{\"id\":\"{Guid.NewGuid()}\",\"name\":\"Marca Fictícia {i}\",\"is_active\":false}}")) + "]";
}

/// <summary>Logger que guarda as mensagens formatadas, para conferir o que iria ao log.</summary>
public sealed class ListaLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<string> _mensagens = new();

    public string[] Mensagens => _mensagens.ToArray();

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => null!;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
        Func<TState, Exception, string> formatter)
    {
        _mensagens.Enqueue($"{logLevel}: {formatter(state, exception)} {exception}");
    }
}
