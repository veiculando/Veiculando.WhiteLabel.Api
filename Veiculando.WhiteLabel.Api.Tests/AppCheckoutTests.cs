using System;
using System.Data.Entity;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities.WhiteLabel;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests;

[Collection(DatabaseCollection.Nome)]
public sealed class AppCheckoutTests
{
    private readonly SqlServerFixture _db;
    public AppCheckoutTests(SqlServerFixture db) => _db = db;

    [Fact]
    public async Task Checkout_exige_KYC_aprovado_e_contexto_comercial_do_tenant()
    {
        const int tenant = 891;
        const string email = "checkout-891@exemplo.com";
        var piece = await PrepareAsync(tenant, email, "P-CHK-891", 891);
        using var factory = new WlApiFactory(_db, tenant);
        using var client = await AppClientAsync(factory, email);

        var forbidden = await client.PostAsJsonAsync("/api/wl/app/checkout/quote",
            new { pieceIds = new[] { piece }, campaignId = 1, periodCode = "APP891" });
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await ApproveAsync(tenant, email);
        var context = await client.GetAsync("/api/wl/app/checkout/context");
        context.StatusCode.Should().Be(HttpStatusCode.OK);
        (await context.Content.ReadAsStringAsync()).Should().Contain("CAMP1");

        var crossTenant = await client.PostAsJsonAsync("/api/wl/app/checkout/quote",
            new { pieceIds = new[] { piece }, campaignId = 1, periodCode = "APP892" });
        crossTenant.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Checkout_confirma_pedido_core_uma_vez_e_retorna_mesmo_recibo_no_retry()
    {
        const int tenant = 892;
        const string email = "checkout-892@exemplo.com";
        var piece = await PrepareAsync(tenant, email, "P-CHK-892", 892);
        await ApproveAsync(tenant, email);
        using var factory = new WlApiFactory(_db, tenant);
        using var client = await AppClientAsync(factory, email);

        var quoteResponse = await client.PostAsJsonAsync("/api/wl/app/checkout/quote",
            new { pieceIds = new[] { piece }, campaignId = 1, periodCode = "APP892" });
        quoteResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            await quoteResponse.Content.ReadAsStringAsync());
        var quote = await quoteResponse.Content.ReadFromJsonAsync<QuoteResponse>();
        quote.Total.Should().BeGreaterThan(0);

        var withoutTerms = await client.PostAsJsonAsync("/api/wl/app/checkout/orders",
            new { quoteId = quote.QuoteId, termsAccepted = false, termsVersion = "aurum-v1" });
        withoutTerms.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        client.DefaultRequestHeaders.Add("Idempotency-Key", "checkout-892-unique");
        var payload = new { quoteId = quote.QuoteId, termsAccepted = true, termsVersion = "aurum-v1" };
        var first = await client.PostAsJsonAsync("/api/wl/app/checkout/orders", payload);
        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        var receipt = await first.Content.ReadAsStringAsync();
        receipt.Should().Contain("aurum-v1");
        var retry = await client.PostAsJsonAsync("/api/wl/app/checkout/orders", payload);
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        (await retry.Content.ReadAsStringAsync()).Should().Be(receipt);
        var reuseOnOtherQuote = await client.PostAsJsonAsync("/api/wl/app/checkout/orders",
            new { quoteId = Guid.NewGuid(), termsAccepted = true, termsVersion = "aurum-v1" });
        reuseOnOtherQuote.StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var ctx = new VeiculandoDataContext();
        (await ctx.Pedidos.CountAsync(p => p.IdCampanha == 1 && p.IdPeriodo == 892)).Should().Be(1);
        (await ctx.PedidosReserva.CountAsync(r => r.Pedido.IdCampanha == 1 && r.Pedido.IdPeriodo == 892)).Should().Be(1);
        (await ctx.WlAppCheckoutSubmissions.CountAsync(s => s.QuoteId == quote.QuoteId)).Should().Be(1);
    }

    private static async Task<int> PrepareAsync(int tenant, string email, string pieceCode, int periodId)
    {
        var local = await Seed.LocalAsync(tenant, $"CHK{tenant}");
        var piece = await Seed.PecaAsync(local, pieceCode);
        await Seed.ReservaAsync(tenant, $"CHK-{tenant}", piece);
        await Seed.AnuncianteAsync(tenant, email, "00000000000191");
        using var ctx = new VeiculandoDataContext();
        await ctx.Database.ExecuteSqlCommandAsync(@"
UPDATE dbo.UsuarioAnunciante SET IdAgencia = 1 WHERE Id = 1;
IF NOT EXISTS (SELECT 1 FROM dbo.AfiliadaCliente WHERE IdAfiliada = @p0 AND IdCliente = 1)
    INSERT dbo.AfiliadaCliente (IdAfiliada, IdCliente, FonteOrigem, Status, DataVinculo,
        DataCadastro, DataAtualizacao, StatusExibicao)
    VALUES (@p0, 1, 1, 0, GETUTCDATE(), GETUTCDATE(), GETUTCDATE(), 1);
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

    private static async Task<System.Net.Http.HttpClient> AppClientAsync(WlApiFactory factory, string email)
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
