using System;
using System.Data.Entity;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities.WhiteLabel;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests;

/// <summary>
/// Minhas Campanhas do App (VEI-RD-45i/j). A CAMP1 do seed é compartilhada entre
/// exibidoras: o pedido de uma não pode aparecer, nem somar, na outra.
/// </summary>
[Collection(DatabaseCollection.Nome)]
public sealed class AppCampaignsTests
{
    private readonly SqlServerFixture _db;
    public AppCampaignsTests(SqlServerFixture db) => _db = db;

    [Fact]
    public async Task Minhas_campanhas_exige_KYC_aprovado()
    {
        const int tenant = 8931;
        const string email = "campanhas-8931@exemplo.com";
        await PrepareAsync(tenant, email, "P-CMP-8931", 8931);
        using var factory = new WlApiFactory(_db, tenant);
        using var client = await AppClientAsync(factory, email);

        (await client.GetAsync("/api/wl/app/campaigns")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync("/api/wl/app/campaigns/CAMP1")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Painel_e_detalhe_mostram_so_pedidos_da_propria_exibidora()
    {
        const int tenant = 8930;
        const int outro = 8932;
        const string email = "campanhas-8930@exemplo.com";
        const string emailOutro = "campanhas-8932@exemplo.com";

        // Outra exibidora faz pedido na mesma CAMP1 antes.
        var pecaOutro = await PrepareAsync(outro, emailOutro, "P-CMP-8932", 8932);
        await ApproveAsync(outro, emailOutro);
        using (var factoryOutro = new WlApiFactory(_db, outro))
        using (var clientOutro = await AppClientAsync(factoryOutro, emailOutro))
            await OrderAsync(clientOutro, pecaOutro, "APP8932", "campanhas-8932");

        var peca = await PrepareAsync(tenant, email, "P-CMP-8930", 8930);
        await ApproveAsync(tenant, email);
        using var factory = new WlApiFactory(_db, tenant);
        using var client = await AppClientAsync(factory, email);
        await OrderAsync(client, peca, "APP8930", "campanhas-8930");

        // O seed da reserva também gera pedido Solicitado na CAMP1 com a peça da exibidora:
        // os dois são dela. Os da 8932 (seed + compra) ficam de fora.
        decimal total, global;
        using (var ctx = new VeiculandoDataContext())
        {
            var itens = ctx.Pedidos.Where(p => p.IdCampanha == 1 && p.Status == Veiculando.Domain.Enums.StatusPedidoEnum.Solicitado)
                .SelectMany(p => p.Itens);
            total = await itens.Where(i => i.Peca.Local.IdAfiliada == tenant).SumAsync(i => i.ValorBruto);
            global = await itens.SumAsync(i => i.ValorBruto);
        }
        global.Should().BeGreaterThan(total, "o cenário precisa ter pedido de outra exibidora na mesma campanha");

        var list = await client.GetAsync("/api/wl/app/campaigns");
        list.StatusCode.Should().Be(HttpStatusCode.OK, await list.Content.ReadAsStringAsync());
        using var listJson = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var camp = listJson.RootElement.EnumerateArray().Single(c => c.GetProperty("codigo").GetString() == "CAMP1");
        camp.GetProperty("pedidosPendentes").GetDecimal().Should().Be(total, "o pedido da outra exibidora não soma aqui");
        camp.GetProperty("pedidosConfirmados").GetDecimal().Should().Be(0);
        camp.GetProperty("status").GetString().Should().NotBeNullOrEmpty();
        camp.GetProperty("anunciante").GetProperty("nome").GetString().Should().NotBeNullOrEmpty();

        var detail = await client.GetAsync("/api/wl/app/campaigns/CAMP1");
        detail.StatusCode.Should().Be(HttpStatusCode.OK, await detail.Content.ReadAsStringAsync());
        using var detailJson = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        var root = detailJson.RootElement;
        var pedidos = root.GetProperty("pedidos").EnumerateArray().ToList();
        pedidos.Should().HaveCount(2, "o pedido do seed e o da compra");
        pedidos.Should().OnlyContain(p => p.GetProperty("status").GetString() == "Solicitado");
        pedidos.Sum(p => p.GetProperty("valor").GetDecimal()).Should().Be(total);
        root.GetProperty("pedidosPendentes").GetDecimal().Should().Be(total);
        root.GetProperty("totalPecas").GetInt32().Should().Be(2);
        root.GetProperty("pecasPublicadas").GetInt32().Should().Be(0);

        // Campanha inexistente e campanha fora do escopo respondem igual: 404.
        (await client.GetAsync("/api/wl/app/campaigns/NAO-EXISTE")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        using (var ctx = new VeiculandoDataContext())
            await ctx.Database.ExecuteSqlCommandAsync(
                "UPDATE dbo.AfiliadaCliente SET Status = 0 WHERE IdAfiliada = @p0 AND IdCliente = 1", tenant);
        (await client.GetAsync("/api/wl/app/campaigns/CAMP1")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var afterUnlink = JsonDocument.Parse(await (await client.GetAsync("/api/wl/app/campaigns")).Content.ReadAsStringAsync());
        afterUnlink.RootElement.EnumerateArray().Should().NotContain(c => c.GetProperty("codigo").GetString() == "CAMP1");
    }

    private static async Task OrderAsync(HttpClient client, int piece, string period, string key)
    {
        var quoteResponse = await client.PostAsJsonAsync("/api/wl/app/checkout/quote",
            new { pieceIds = new[] { piece }, campaignId = 1, periodCode = period });
        quoteResponse.StatusCode.Should().Be(HttpStatusCode.OK, await quoteResponse.Content.ReadAsStringAsync());
        var quote = await quoteResponse.Content.ReadFromJsonAsync<QuoteResponse>();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/wl/app/checkout/orders")
        {
            Content = JsonContent.Create(new { quoteId = quote.QuoteId, termsAccepted = true, termsVersion = "aurum-v1" })
        };
        request.Headers.Add("Idempotency-Key", key);
        var order = await client.SendAsync(request);
        order.StatusCode.Should().Be(HttpStatusCode.OK, await order.Content.ReadAsStringAsync());
    }

    private static async Task<int> PrepareAsync(int tenant, string email, string pieceCode, int periodId)
    {
        var local = await Seed.LocalAsync(tenant, $"CMP{tenant}");
        var piece = await Seed.PecaAsync(local, pieceCode);
        await Seed.ReservaAsync(tenant, $"CMP-{tenant}", piece);
        await Seed.AnuncianteAsync(tenant, email, "00000000000191");
        using var ctx = new VeiculandoDataContext();
        await ctx.Database.ExecuteSqlCommandAsync(@"
UPDATE dbo.UsuarioAnunciante SET IdAgencia = 1 WHERE Id = 1;
IF NOT EXISTS (SELECT 1 FROM dbo.AfiliadaCliente WHERE IdAfiliada = @p0 AND IdCliente = 1)
    INSERT dbo.AfiliadaCliente (IdAfiliada, IdCliente, FonteOrigem, Status, DataVinculo,
        DataCadastro, DataAtualizacao, StatusExibicao)
    VALUES (@p0, 1, 1, 1, GETUTCDATE(), GETUTCDATE(), GETUTCDATE(), 1);
SET IDENTITY_INSERT dbo.Periodo ON;
INSERT dbo.Periodo (Id, Codigo, Periodicidade, DataInicio, DataFim, StatusExibicao)
VALUES (@p1, @p2, 0, DATEADD(day, 1, GETUTCDATE()), DATEADD(day, 8, GETUTCDATE()), 1);
SET IDENTITY_INSERT dbo.Periodo OFF;", tenant, periodId, $"APP{periodId}");
        return piece;
    }

    private static async Task ApproveAsync(int tenant, string email)
    {
        using var ctx = new VeiculandoDataContext();
        var user = await ctx.WlUsuariosAnunciante.SingleAsync(u => u.AfiliadaId == tenant && u.Email.Endereco == email);
        var onboarding = new WlAppOnboarding(user);
        onboarding.SalvarRascunho("pj", "00000000000191", "{}", 4);
        onboarding.ReivindicarDocumento("00000000000191");
        onboarding.Enviar();
        onboarding.Transicionar(WlAppKycStatus.EmAnalise, null);
        onboarding.Transicionar(WlAppKycStatus.Aprovado, null);
        ctx.WlAppOnboardings.Add(onboarding);
        await ctx.SaveChangesAsync();
    }

    private static async Task<HttpClient> AppClientAsync(WlApiFactory factory, string email)
    {
        var client = factory.ClienteAnonimo();
        var response = await client.PostAsJsonAsync("/api/wl/app/auth/login", new { email, password = Seed.SenhaPadrao });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var login = await response.Content.ReadFromJsonAsync<LoginResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        return client;
    }

    private sealed record LoginResponse(string Token);
    private sealed record QuoteResponse(Guid QuoteId, decimal Total);
}
