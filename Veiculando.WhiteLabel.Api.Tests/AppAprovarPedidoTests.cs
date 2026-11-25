using System;
using System.Data.Entity;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Enums;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;
using static Veiculando.WhiteLabel.Api.Tests.AppMinhasCampanhasEscopoTests;

namespace Veiculando.WhiteLabel.Api.Tests;

/// <summary>
/// VEI-SUP-60: o pedido que a exibidora confirmou é aprovado no App e vira PI. Em produção
/// (Aurum, pedido UMC-5DFE-F39C) a reserva estava confirmada, mas o App não tinha o passo de
/// aprovar: a campanha ficava em rascunho e a exibidora nunca via o pedido de inserção.
/// </summary>
[Collection(DatabaseCollection.Nome)]
public sealed class AppAprovarPedidoTests
{
    private const int Tenant = 8981;
    private readonly SqlServerFixture _db;
    public AppAprovarPedidoTests(SqlServerFixture db) => _db = db;

    [Fact]
    public async Task Pedido_confirmado_aprovado_no_App_gera_PI_e_autoriza_a_campanha()
    {
        const string email = "sup60-ag-8981@exemplo.com";
        var p1 = await Seed.PecaAsync(await Seed.LocalAsync(Tenant, $"AP{Tenant}"), $"P-AP-{Tenant}-1");
        var p2 = await Seed.PecaAsync(await Seed.LocalAsync(Tenant, $"AQ{Tenant}"), $"P-AP-{Tenant}-2");
        await Seed.ReservaAsync(Tenant, $"AP-{Tenant}", p1); // grafo de apoio (período 1, cidade 1)
        var cnpjAgencia = ProspeccaoCarteiraTests.Cnpj(Tenant, 9);
        var (agencia, responsavel) = await AgenciaAsync(Tenant, cnpjAgencia, email);
        var cliente = await ClienteAsync(Tenant, "AP8981", "Universidade Teste", ProspeccaoCarteiraTests.Cnpj(Tenant, 1));
        await ContratoAsync(agencia, cliente, DateTime.UtcNow.AddYears(1));
        var campanha = await CampanhaAsync("AP8981C", agencia, responsavel, cliente, StatusCampanhaEnum.PedidoEmAberto, null);
        var pedido = await PedidoAsync(campanha, "AP8981C-0001", StatusPedidoEnum.Confirmado, responsavel);
        await ItemAsync(pedido, p1, StatusPedidoItemEnum.Reservado, 4_000m);
        await ItemAsync(pedido, p2, StatusPedidoItemEnum.Reservado, 4_575.06m);
        await GradeAsync(p1, pedido);
        await GradeAsync(p2, pedido);

        await Seed.AnuncianteAsync(Tenant, email, cnpjAgencia);
        await AprovarAsync(Tenant, email, "ag", cnpjAgencia, agencia);
        using var factory = new WlApiFactory(_db, Tenant);
        using var app = await LoginAsync(factory, email);

        var linha = (await JsonAsync(app, "/api/wl/app/campaigns/AP8981C")).GetProperty("pedidos").EnumerateArray().Single();
        linha.GetProperty("podeAprovar").GetBoolean().Should().BeTrue("a exibidora já confirmou");

        var r = await app.PostAsync("/api/wl/app/campaigns/AP8981C/pedidos/AP8981C-0001/aprovar", null);
        var corpo = await r.Content.ReadAsStringAsync();
        r.StatusCode.Should().Be(HttpStatusCode.OK, corpo);
        JsonDocument.Parse(corpo).RootElement.GetProperty("pedidosInsercao").GetArrayLength().Should().Be(1);

        using (var ctx = new VeiculandoDataContext())
        {
            var gravado = await ctx.Pedidos.Include(p => p.Itens).SingleAsync(p => p.Id == pedido);
            gravado.Status.Should().Be(StatusPedidoEnum.Aprovado);
            gravado.IdUsuarioAprovador.Should().Be(responsavel);
            gravado.Itens.Should().OnlyContain(i => i.Status == StatusPedidoItemEnum.Confirmado);
            (await ctx.Campanhas.SingleAsync(c => c.Id == campanha)).Status.Should().Be(StatusCampanhaEnum.Autorizada);
            var pi = await ctx.PedidosInsercao.Include(x => x.Itens).SingleAsync(x => x.IdPedido == pedido);
            pi.IdAfiliada.Should().Be(Tenant);
            pi.Itens.Should().HaveCount(2);
            (await ctx.PecaPeriodoStatus.Where(s => s.IdPedido == pedido).ToListAsync())
                .Should().OnlyContain(s => s.Status == StatusPecaPeriodoEnum.Autorizada && s.IdPedidoInsercao == pi.Id);
        }

        var segunda = await app.PostAsync("/api/wl/app/campaigns/AP8981C/pedidos/AP8981C-0001/aprovar", null);
        segunda.StatusCode.Should().Be(HttpStatusCode.Conflict, "aprovar duas vezes não gera outro PI");
        (await app.PostAsync("/api/wl/app/campaigns/AP8981C/pedidos/NAO-EXISTE/aprovar", null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// O pj enxerga a campanha que a agência criou para o CNPJ dele, mas quem aprova é a
    /// agência: o botão não aparece e o POST responde 403 sem mexer no pedido.
    /// </summary>
    [Fact]
    public async Task Pj_nao_aprova_pedido_da_campanha_que_a_agencia_criou()
    {
        const int tenant = 8982;
        const string emailAgencia = "sup60-ag-8982@exemplo.com";
        const string emailPj = "sup60-pj-8982@exemplo.com";
        var peca = await Seed.PecaAsync(await Seed.LocalAsync(tenant, $"AP{tenant}"), $"P-AP-{tenant}");
        await Seed.ReservaAsync(tenant, $"AP-{tenant}", peca);
        var cnpjAgencia = ProspeccaoCarteiraTests.Cnpj(tenant, 9);
        var cnpjPj = ProspeccaoCarteiraTests.Cnpj(tenant, 1);
        var (agencia, responsavel) = await AgenciaAsync(tenant, cnpjAgencia, emailAgencia);
        var cliente = await ClienteAsync(tenant, "AP8982", "Cliente Pj", cnpjPj);
        var campanha = await CampanhaAsync("AP8982C", agencia, responsavel, cliente, StatusCampanhaEnum.PedidoEmAberto, null);
        var pedido = await PedidoAsync(campanha, "AP8982C-0001", StatusPedidoEnum.Confirmado, responsavel);
        await ItemAsync(pedido, peca, StatusPedidoItemEnum.Reservado, 1_000m);

        await Seed.AnuncianteAsync(tenant, emailPj, cnpjPj);
        await AprovarAsync(tenant, emailPj, "pj", cnpjPj);
        using var factory = new WlApiFactory(_db, tenant);
        using var app = await LoginAsync(factory, emailPj);

        var linha = (await JsonAsync(app, "/api/wl/app/campaigns/AP8982C")).GetProperty("pedidos").EnumerateArray().Single();
        linha.GetProperty("podeAprovar").GetBoolean().Should().BeFalse("a campanha é da agência");

        var r = await app.PostAsync("/api/wl/app/campaigns/AP8982C/pedidos/AP8982C-0001/aprovar", null);
        r.StatusCode.Should().Be(HttpStatusCode.Forbidden, await r.Content.ReadAsStringAsync());
        using var ctx = new VeiculandoDataContext();
        (await ctx.Pedidos.SingleAsync(p => p.Id == pedido)).Status.Should().Be(StatusPedidoEnum.Confirmado);
        (await ctx.PedidosInsercao.AnyAsync(x => x.IdPedido == pedido)).Should().BeFalse();
    }

    private static async Task<int> PedidoAsync(int campanha, string codigo, StatusPedidoEnum status, int responsavel)
    {
        using var ctx = new VeiculandoDataContext();
        return await ctx.Database.SqlQuery<int>(@"
INSERT INTO Pedido (FonteOrigem, IdCampanha, IdCidade, IdPeriodo, IdUsuarioAnunciante, Codigo, Status, StatusPagamento, Revisao,
                    DescontoNegociado, DescontoFinanceiro, ComissaoAgencia, BonificacaoVolume, ComissaoVeiculando,
                    ValorTotalTabela, ValorTotalBruto, ValorDescontoNegociado, ValorDescontoFinanceiro, ValorComissaoAgencia,
                    ValorBonificacaoVolume, ValorComissaoVeiculando, ValorLiquidoVeiculacao, ValorLiquidoAnunciante,
                    DataCadastro, DataAtualizacao, StatusExibicao)
VALUES (1, @p0, 1, 1, @p3, @p1, @p2, 2, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, GETDATE(), GETDATE(), 1);
SELECT CAST(SCOPE_IDENTITY() AS int);", campanha, codigo, (int)status, responsavel).SingleAsync();
    }

    /// <summary>Linha da grade de disponibilidade como a reserva confirmada deixa (Reservada).</summary>
    private static async Task GradeAsync(int peca, int pedido)
    {
        using var ctx = new VeiculandoDataContext();
        await ctx.Database.ExecuteSqlCommandAsync(@"
INSERT INTO PecaPeriodoStatus (IdPeca, IdPeriodo, Status, IdPedido)
VALUES (@p0, 1, @p1, @p2);", peca, (int)StatusPecaPeriodoEnum.Reservada, pedido);
    }
}
