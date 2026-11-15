using System.Linq;
using System.Net;
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
/// Aba Produção de Minhas Campanhas (VEI-RD-165a): só peças desta exibidora, filtro de status dos
/// pedidos igual ao Veiculando.Web e PDF gerado no BFF.
/// </summary>
[Collection(DatabaseCollection.Nome)]
public sealed class AppProducaoTests
{
    private readonly SqlServerFixture _db;
    public AppProducaoTests(SqlServerFixture db) => _db = db;

    [Fact]
    public async Task Producao_agrupa_por_suporte_e_formato_so_desta_exibidora_e_respeita_o_filtro()
    {
        const int tenant = 8940;
        const int outra = 8941;
        const string email = "producao-ag-8940@exemplo.com";
        var local = await Seed.LocalAsync(tenant, $"PR{tenant}");
        var impressaA = await Seed.PecaAsync(local, $"P-PR-{tenant}-A");
        var impressaB = await Seed.PecaAsync(local, $"P-PR-{tenant}-B");
        var digital = await Seed.PecaAsync(local, $"P-PR-{tenant}-D");
        var semFormato = await Seed.PecaAsync(local, $"P-PR-{tenant}-S");
        var dali = await Seed.PecaAsync(await Seed.LocalAsync(outra, $"PR{outra}"), $"P-PR-{outra}");
        await Seed.ReservaAsync(tenant, $"PR-{tenant}", impressaA);

        var lona = await FormatoAsync(MidiaTipoEnum.Fisica, 9m, 3m, 0, 0, 0, new[] { "pdf", "ai" });
        var tela = await FormatoAsync(MidiaTipoEnum.Digital, 0m, 0m, 1920, 1080, 15, new[] { "mp4" });
        await FormatoDaPecaAsync(lona, impressaA, impressaB, dali);
        await FormatoDaPecaAsync(tela, digital);

        var cnpjAgencia = ProspeccaoCarteiraTests.Cnpj(tenant, 9);
        var (agencia, responsavel) = await AgenciaAsync(tenant, cnpjAgencia, email);
        var cliente = await ClienteAsync(tenant, "PR8940", "Cliente Produção", ProspeccaoCarteiraTests.Cnpj(tenant, 1));
        await ContratoAsync(agencia, cliente, System.DateTime.UtcNow.AddYears(1));
        var campanha = await CampanhaAsync("PR8940A", agencia, responsavel, cliente, StatusCampanhaEnum.Confirmada, 50_000m);
        await CampanhaAsync("PR8940V", agencia, responsavel, cliente, StatusCampanhaEnum.Criada, 50_000m);

        // Aprovado: duas lonas daqui, uma da outra exibidora, uma deletada e uma indisponível.
        var aprovado = await PedidoAsync(campanha, "PPR8940A", StatusPedidoEnum.Aprovado);
        await ItemAsync(aprovado, impressaA, StatusPedidoItemEnum.Confirmado, 1_000m);
        await ItemAsync(aprovado, impressaB, StatusPedidoItemEnum.Confirmado, 1_000m);
        await ItemAsync(aprovado, dali, StatusPedidoItemEnum.Confirmado, 1_000m);
        await ItemAsync(aprovado, impressaA, StatusPedidoItemEnum.Deletado, 1_000m);
        await ItemAsync(aprovado, impressaB, StatusPedidoItemEnum.Indisponivel, 1_000m);
        // Em andamento: a tela e uma peça sem formato. Simulação e cancelado: nunca entram juntos.
        var solicitado = await PedidoAsync(campanha, "PPR8940B", StatusPedidoEnum.Solicitado);
        await ItemAsync(solicitado, digital, StatusPedidoItemEnum.Solicitado, 1_000m);
        await ItemAsync(solicitado, semFormato, StatusPedidoItemEnum.Solicitado, 1_000m);
        var simulacao = await PedidoAsync(campanha, "PPR8940C", StatusPedidoEnum.Simulacao);
        await ItemAsync(simulacao, digital, StatusPedidoItemEnum.Simulacao, 1_000m);
        var cancelado = await PedidoAsync(campanha, "PPR8940D", StatusPedidoEnum.Cancelado);
        await ItemAsync(cancelado, impressaA, StatusPedidoItemEnum.Confirmado, 1_000m);

        await Seed.AnuncianteAsync(tenant, email, cnpjAgencia);
        await AprovarAsync(tenant, email, "ag", cnpjAgencia, agencia);
        using var factory = new WlApiFactory(_db, tenant);
        using var app = await LoginAsync(factory, email);

        var aprovados = await JsonAsync(app, "/api/wl/app/campaigns/PR8940A/producao");
        aprovados.GetArrayLength().Should().Be(1, "só a lona aprovada desta exibidora");
        var linha = aprovados[0];
        linha.GetProperty("suporte").GetString().Should().Be("Outdoor");
        linha.GetProperty("midia").GetString().Should().Be("impressa");
        linha.GetProperty("dimensao").GetString().Should().Be("9 x 3");
        linha.GetProperty("extensoesAceitas").GetString().Should().Contain("pdf");
        linha.GetProperty("quantidade").GetInt32().Should().Be(2, "nem a outra exibidora nem deletado ou indisponível contam");

        var andamento = await JsonAsync(app, "/api/wl/app/campaigns/PR8940A/producao?filtro=2");
        andamento.EnumerateArray().Select(l => l.GetProperty("midia").GetString())
            .Should().Equal(new[] { "digital", "impressa", null }, "digital primeiro e sem formato por último");
        var tela15 = andamento[0];
        tela15.GetProperty("resolucao").GetString().Should().NotBeNullOrEmpty();
        tela15.GetProperty("duracao").GetInt32().Should().Be(15);
        tela15.GetProperty("quantidade").GetInt32().Should().Be(1);

        var comSimulacao = await JsonAsync(app, "/api/wl/app/campaigns/PR8940A/producao?filtro=3");
        comSimulacao.EnumerateArray().Single(l => l.GetProperty("midia").GetString() == "digital")
            .GetProperty("quantidade").GetInt32().Should().Be(2, "a simulação entra só no filtro 3");
        comSimulacao.EnumerateArray().Sum(l => l.GetProperty("quantidade").GetInt32()).Should().Be(5, "o cancelado nunca entra");

        (await JsonAsync(app, "/api/wl/app/campaigns/PR8940V/producao")).GetArrayLength().Should().Be(0);
        (await app.GetAsync("/api/wl/app/campaigns/PR8940A/producao?filtro=9")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await app.GetAsync("/api/wl/app/campaigns/NAOEXISTE/producao")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var pdf = await app.GetAsync("/api/wl/app/campaigns/PR8940A/producao/pdf?filtro=2");
        pdf.StatusCode.Should().Be(HttpStatusCode.OK);
        pdf.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        var bytes = await pdf.Content.ReadAsByteArrayAsync();
        System.Text.Encoding.ASCII.GetString(bytes, 0, 5).Should().Be("%PDF-");
        (await app.GetAsync("/api/wl/app/campaigns/NAOEXISTE/producao/pdf")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Producao_de_campanha_fora_do_escopo_da_404_e_sem_login_401()
    {
        const int tenant = 8942;
        const string email = "producao-pj-8942@exemplo.com";
        var cnpj = ProspeccaoCarteiraTests.Cnpj(tenant, 1);
        var peca = await Seed.PecaAsync(await Seed.LocalAsync(tenant, $"PR{tenant}"), $"P-PR-{tenant}");
        await Seed.ReservaAsync(tenant, $"PR-{tenant}", peca);
        var outroCliente = await ClienteAsync(tenant, "O8942", "Outro Cliente", ProspeccaoCarteiraTests.Cnpj(tenant, 2));
        await CampanhaAsync("PR8942X", 1, 1, outroCliente, StatusCampanhaEnum.Confirmada, 10_000m);

        await Seed.AnuncianteAsync(tenant, email, cnpj);
        await AprovarAsync(tenant, email, "pj", cnpj);
        using var factory = new WlApiFactory(_db, tenant);
        (await factory.ClienteAnonimo().GetAsync("/api/wl/app/campaigns/PR8942X/producao"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var app = await LoginAsync(factory, email);
        (await app.GetAsync("/api/wl/app/campaigns/PR8942X/producao")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await app.GetAsync("/api/wl/app/campaigns/PR8942X/producao/pdf")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static async Task<int> FormatoAsync(MidiaTipoEnum midia, decimal largura, decimal altura,
        short resLargura, short resAltura, short duracao, string[] extensoes)
    {
        using var ctx = new VeiculandoDataContext();
        var formato = new Formato(midia, largura, altura, resLargura, resAltura, duracao, "Criação", "Arquivo", extensoes,
            await System.Data.Entity.QueryableExtensions.SingleAsync(ctx.Usuarios, u => u.Id == 1));
        ctx.Formatos.Add(formato);
        await ctx.SaveChangesAsync();
        return formato.Id;
    }

    private static async Task FormatoDaPecaAsync(int formato, params int[] pecas)
    {
        using var ctx = new VeiculandoDataContext();
        foreach (var peca in pecas)
            await ctx.Database.ExecuteSqlCommandAsync("UPDATE Peca SET IdFormatoArteFinal = @p0 WHERE Id = @p1", formato, peca);
    }
}
