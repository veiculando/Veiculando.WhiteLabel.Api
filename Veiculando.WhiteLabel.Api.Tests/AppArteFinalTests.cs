using System;
using System.Data.Entity;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities;
using Veiculando.Domain.Enums;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;
using static Veiculando.WhiteLabel.Api.Tests.AppMinhasCampanhasEscopoTests;

namespace Veiculando.WhiteLabel.Api.Tests;

/// <summary>
/// Aba Arte Final de Minhas Campanhas (VEI-RD-166a): motivos, layouts por motivo × formato desta
/// exibidora, arquivo no FileServer pelo mesmo caminho do Core e só quem criou a campanha altera.
/// </summary>
[Collection(DatabaseCollection.Nome)]
public sealed class AppArteFinalTests
{
    private readonly SqlServerFixture _db;
    public AppArteFinalTests(SqlServerFixture db) => _db = db;

    [Fact]
    public async Task Agencia_cria_motivos_envia_substitui_baixa_e_exclui_layouts()
    {
        const int tenant = 8943;
        const int outra = 8944;
        const string email = "arte-ag-8943@exemplo.com";
        var local = await Seed.LocalAsync(tenant, $"AF{tenant}");
        var lona = await Seed.PecaAsync(local, $"P-AF-{tenant}-L");
        var tela = await Seed.PecaAsync(local, $"P-AF-{tenant}-T");
        var dali = await Seed.PecaAsync(await Seed.LocalAsync(outra, $"AF{outra}"), $"P-AF-{outra}");
        await Seed.ReservaAsync(tenant, $"AF-{tenant}", lona);
        var formatoLona = await FormatoAsync(MidiaTipoEnum.Fisica, 9m, 3m, 0, 0, new[] { "pdf", "jpg" });
        var formatoTela = await FormatoAsync(MidiaTipoEnum.Digital, 0m, 0m, 1920, 1080, new[] { "mp4" });
        var formatoDali = await FormatoAsync(MidiaTipoEnum.Fisica, 4m, 2m, 0, 0, new[] { "pdf" });
        await FormatoDaPecaAsync(formatoLona, lona);
        await FormatoDaPecaAsync(formatoTela, tela);
        await FormatoDaPecaAsync(formatoDali, dali);

        var cnpjAgencia = ProspeccaoCarteiraTests.Cnpj(tenant, 9);
        var (agencia, responsavel) = await AgenciaAsync(tenant, cnpjAgencia, email);
        var cliente = await ClienteAsync(tenant, "AF8943", "Cliente Arte", ProspeccaoCarteiraTests.Cnpj(tenant, 1));
        await ContratoAsync(agencia, cliente, DateTime.UtcNow.AddYears(1));
        var campanha = await CampanhaAsync("AF8943A", agencia, responsavel, cliente, StatusCampanhaEnum.Confirmada, 50_000m);
        var pedido = await PedidoAsync(campanha, "PAF8943A", StatusPedidoEnum.Aprovado);
        await ItemAsync(pedido, lona, StatusPedidoItemEnum.Confirmado, 1_000m);
        await ItemAsync(pedido, tela, StatusPedidoItemEnum.Confirmado, 1_000m);
        await ItemAsync(pedido, dali, StatusPedidoItemEnum.Confirmado, 1_000m);

        await Seed.AnuncianteAsync(tenant, email, cnpjAgencia);
        await AprovarAsync(tenant, email, "ag", cnpjAgencia, agencia);
        using var factory = new WlApiFactory(_db, tenant);
        using var app = await LoginAsync(factory, email);
        const string url = "/api/wl/app/campaigns/AF8943A/arte-final";

        var vazio = await JsonAsync(app, url);
        vazio.GetProperty("podeEditar").GetBoolean().Should().BeTrue();
        vazio.GetProperty("motivos").GetArrayLength().Should().Be(0);
        vazio.GetProperty("formatos").EnumerateArray().Select(f => f.GetProperty("id").GetInt32())
            .Should().Equal(new[] { formatoTela, formatoLona }, "só formatos desta exibidora, digital primeiro");
        vazio.GetProperty("formatos")[1].GetProperty("extensoes").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("pdf", "jpg");

        (await app.PostAsJsonAsync($"{url}/motivos", new { nome = " " })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var a = await CriarMotivoAsync(app, url, "Verão");
        var b = await CriarMotivoAsync(app, url, "Inverno");
        a.GetProperty("codigo").GetString().Should().Be("A");
        b.GetProperty("codigo").GetString().Should().Be("B");
        (await app.DeleteAsync($"{url}/motivos/{a.GetProperty("id").GetInt32()}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await CriarMotivoAsync(app, url, "Outono")).GetProperty("codigo").GetString()
            .Should().Be("A", "a letra livre volta; o Core repetiria o B");
        var idB = b.GetProperty("id").GetInt32();

        // Formato de outra exibidora e extensão fora do formato: 400 antes de chegar ao FileServer.
        (await EnviarAsync(app, url, idB, formatoDali, "arte.pdf")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var exe = await EnviarAsync(app, url, idB, formatoLona, "arte.exe");
        exe.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await exe.Content.ReadAsStringAsync()).Should().Contain("PDF, JPG");
        factory.FileServer.Conteudos.Should().BeEmpty();

        factory.FileServer.FalharUploadConteudo = true;
        (await EnviarAsync(app, url, idB, formatoLona, "arte.pdf")).StatusCode.Should().Be(HttpStatusCode.BadGateway);
        factory.FileServer.FalharUploadConteudo = false;

        var primeiro = await EnviarAsync(app, url, idB, formatoLona, "arte.pdf", new byte[] { 1, 2, 3 });
        primeiro.StatusCode.Should().Be(HttpStatusCode.Created, await primeiro.Content.ReadAsStringAsync());
        var segundo = await EnviarAsync(app, url, idB, formatoLona, "nova.PDF", new byte[] { 4, 5 });
        segundo.StatusCode.Should().Be(HttpStatusCode.Created);
        var idLayout = (await segundo.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var detalhe = await JsonAsync(app, url);
        var layouts = detalhe.GetProperty("layouts");
        layouts.GetArrayLength().Should().Be(1, "enviar de novo substitui o layout da mesma célula");
        layouts[0].GetProperty("arquivo").GetString().Should().StartWith("motivo-b_").And.EndWith(".pdf");
        using (var ctx = new VeiculandoDataContext())
        {
            var salvo = await ctx.ArtesFinais.AsNoTracking().SingleAsync(x => x.Id == idLayout);
            salvo.ArquivoAlta.Nome.Should().StartWith("arte-final/af8943/af8943a/motivo-b_", "mesmo caminho do ArteFinalHandler");
            salvo.FonteOrigem.Should().Be(FonteOrigemEnum.WhiteLabel);
        }

        var baixado = await app.GetAsync($"{url}/layouts/{idLayout}/arquivo");
        baixado.StatusCode.Should().Be(HttpStatusCode.OK);
        (await baixado.Content.ReadAsByteArrayAsync()).Should().Equal(4, 5);

        (await app.DeleteAsync($"{url}/motivos/{idB}")).StatusCode.Should().Be(HttpStatusCode.Conflict, "motivo com layout");
        (await app.DeleteAsync($"{url}/layouts/{idLayout}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await app.DeleteAsync($"{url}/layouts/{idLayout}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await app.DeleteAsync($"{url}/motivos/{idB}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Pj_ve_a_arte_da_campanha_da_agencia_mas_nao_altera_e_fora_do_escopo_da_404()
    {
        const int tenant = 8945;
        const string email = "arte-pj-8945@exemplo.com";
        var cnpj = ProspeccaoCarteiraTests.Cnpj(tenant, 1);
        var peca = await Seed.PecaAsync(await Seed.LocalAsync(tenant, $"AF{tenant}"), $"P-AF-{tenant}");
        await Seed.ReservaAsync(tenant, $"AF-{tenant}", peca);
        var cliente = await ClienteAsync(tenant, "AF8945", "Cliente PJ", cnpj);
        var outro = await ClienteAsync(tenant, "AO8945", "Outro", ProspeccaoCarteiraTests.Cnpj(tenant, 2));
        await CampanhaAsync("AF8945A", 1, 1, cliente, StatusCampanhaEnum.Confirmada, 10_000m);
        await CampanhaAsync("AF8945X", 1, 1, outro, StatusCampanhaEnum.Confirmada, 10_000m);

        await Seed.AnuncianteAsync(tenant, email, cnpj);
        await AprovarAsync(tenant, email, "pj", cnpj);
        using var factory = new WlApiFactory(_db, tenant);
        (await factory.ClienteAnonimo().GetAsync("/api/wl/app/campaigns/AF8945A/arte-final"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var app = await LoginAsync(factory, email);

        (await JsonAsync(app, "/api/wl/app/campaigns/AF8945A/arte-final")).GetProperty("podeEditar").GetBoolean().Should().BeFalse();
        (await app.PostAsJsonAsync("/api/wl/app/campaigns/AF8945A/arte-final/motivos", new { nome = "Motivo" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await app.GetAsync("/api/wl/app/campaigns/AF8945X/arte-final")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await app.PostAsJsonAsync("/api/wl/app/campaigns/AF8945X/arte-final/motivos", new { nome = "Motivo" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static async Task<JsonElement> CriarMotivoAsync(HttpClient app, string url, string nome)
    {
        var r = await app.PostAsJsonAsync($"{url}/motivos", new { nome, descricao = "Descrição" });
        r.StatusCode.Should().Be(HttpStatusCode.Created, await r.Content.ReadAsStringAsync());
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> EnviarAsync(HttpClient app, string url, int motivo, int formato, string arquivo,
        byte[] conteudo = null)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(motivo.ToString()), "idMotivo" },
            { new StringContent(formato.ToString()), "idFormato" },
            { new ByteArrayContent(conteudo ?? new byte[] { 1 }), "file", arquivo }
        };
        return app.PostAsync($"{url}/layouts", form);
    }

    private static async Task<int> FormatoAsync(MidiaTipoEnum midia, decimal largura, decimal altura,
        short resLargura, short resAltura, string[] extensoes)
    {
        using var ctx = new VeiculandoDataContext();
        var formato = new Formato(midia, largura, altura, resLargura, resAltura, 0, "Criação", "Arquivo", extensoes,
            await ctx.Usuarios.SingleAsync(u => u.Id == 1));
        ctx.Formatos.Add(formato);
        await ctx.SaveChangesAsync();
        return formato.Id;
    }

    private static async Task FormatoDaPecaAsync(int formato, int peca)
    {
        using var ctx = new VeiculandoDataContext();
        await ctx.Database.ExecuteSqlCommandAsync("UPDATE Peca SET IdFormatoArteFinal = @p0 WHERE Id = @p1", formato, peca);
    }
}
