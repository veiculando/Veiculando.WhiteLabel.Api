using System;
using System.Data.Entity;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities.WhiteLabel;
using Veiculando.Infra.Security;
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

        var otherLocal = await Seed.LocalAsync(893, "CHK893");
        var otherPiece = await Seed.PecaAsync(otherLocal, "P-CHK-893");
        var crossTenant = await client.PostAsJsonAsync("/api/wl/app/checkout/quote",
            new { pieceIds = new[] { otherPiece }, campaignId = 1, periodCode = "APP891" });
        crossTenant.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using (var ctx = new VeiculandoDataContext())
            await ctx.Database.ExecuteSqlCommandAsync("UPDATE dbo.AfiliadaCliente SET Status = 0 WHERE IdAfiliada = @p0 AND IdCliente = 1", tenant);
        var afterUnlink = await client.GetAsync("/api/wl/app/checkout/context");
        (await afterUnlink.Content.ReadAsStringAsync()).Should().NotContain("CAMP1");
        var denied = await client.PostAsJsonAsync("/api/wl/app/checkout/quote",
            new { pieceIds = new[] { piece }, campaignId = 1, periodCode = "APP891" });
        denied.StatusCode.Should().Be(HttpStatusCode.NotFound);
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
        using (var inspection = new VeiculandoDataContext())
        {
            var stored = await inspection.WlAppCheckoutQuotes.SingleAsync(q => q.Id == quote.QuoteId);
            stored.ExpiraEm.Ticks.Should().Be(quote.ExpiresAt.Ticks, "a expiração assinada deve sobreviver ao SQL datetime");
            stored.ValorTotal.Should().Be(quote.Total, "o valor assinado deve sobreviver ao decimal(18,2)");
            var value = string.Join("|", stored.UsuarioId, stored.AfiliadaId, stored.CampanhaId,
                stored.CodigoPeriodo, stored.PecasJson, stored.ValorTotal.ToString("0.00", CultureInfo.InvariantCulture),
                stored.ExpiraEm.Ticks);
            var jwtSecret = factory.Services.GetRequiredService<IOptions<JwtSettings>>().Value.Secret;
            jwtSecret.Should().Be("test-jwt-" + new string('x', 40));
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(jwtSecret));
            var expected = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
            stored.Integridade.Should().Be(expected, "a assinatura precisa ser estável após persistência");
        }

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
        var pedido = await ctx.Pedidos.Include(p => p.Campanha).SingleAsync(p => p.IdCampanha == 1 && p.IdPeriodo == 892);
        pedido.FonteAgenciaId.Should().Be(pedido.Campanha.IdAgencia, "fora da prospecção a origem continua sendo a campanha");
        pedido.FonteUsuarioId.Should().Be(pedido.Campanha.IdUsuarioAnunciante);
        (await ctx.PedidosReserva.CountAsync(r => r.Pedido.IdCampanha == 1 && r.Pedido.IdPeriodo == 892)).Should().Be(1);
        (await ctx.WlAppCheckoutSubmissions.CountAsync(s => s.QuoteId == quote.QuoteId)).Should().Be(1);
    }

    [Fact]
    public async Task Campanha_da_compra_exige_KYC_e_identidade_comercial_do_mesmo_anunciante()
    {
        const int tenant = 894;
        const string email = "checkout-894@exemplo.com";
        await PrepareAsync(tenant, email, "P-CHK-894", 894);
        using var factory = new WlApiFactory(_db, tenant);
        using var client = await AppClientAsync(factory, email);
        var payload = new { name = "Campanha de setembro", product = "Produto A", job = "Lançamento",
            startDate = DateTime.UtcNow.Date.AddDays(1), endDate = DateTime.UtcNow.Date.AddDays(8), budget = 5000m };

        (await client.PostAsJsonAsync("/api/wl/app/checkout/context/campaigns", payload)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
        await ApproveAsync(tenant, email);
        (await client.PostAsJsonAsync("/api/wl/app/checkout/context/campaigns", payload)).StatusCode
            .Should().Be(HttpStatusCode.Conflict, "a conta WhiteLabel não pode usar outro usuário Core por semelhança de CNPJ");

        using var ctx = new VeiculandoDataContext();
        var originalEmail = await ctx.UsuariosAnunciantes.Where(u => u.Id == 1)
            .Select(u => u.Email.Endereco).SingleAsync();
        try
        {
            await ctx.Database.ExecuteSqlCommandAsync(@"
UPDATE dbo.Usuario SET Email = @p0 WHERE Id = 1;
IF NOT EXISTS (SELECT 1 FROM dbo.AgenciaCliente WHERE IdAgencia = 1 AND IdCliente = 1)
    INSERT dbo.AgenciaCliente (IdAgencia, IdCliente, ComissaoAgencia, DataInicioContrato,
        DataExpiracaoContrato, DataCadastro, DataAtualizacao, StatusExibicao)
    VALUES (1, 1, 0, DATEADD(day,-1,GETUTCDATE()), DATEADD(year,1,GETUTCDATE()),
        GETUTCDATE(), GETUTCDATE(), 1);", email);

            var created = await client.PostAsJsonAsync("/api/wl/app/checkout/context/campaigns", payload);
            created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
            var context = await client.GetStringAsync("/api/wl/app/checkout/context");
            context.Should().Contain("Campanha de setembro");
            var stored = await ctx.Campanhas.SingleAsync(c => c.Nome == "Campanha de setembro");
            stored.IdCliente.Should().Be(1);
            stored.IdUsuarioAnunciante.Should().Be(1);
            stored.FonteOrigem.Should().Be(Veiculando.Domain.Enums.FonteOrigemEnum.WhiteLabel);
        }
        finally
        {
            await ctx.Database.ExecuteSqlCommandAsync("UPDATE dbo.Usuario SET Email = @p0 WHERE Id = 1", originalEmail);
        }
    }

    [Fact]
    public async Task Pedido_fechado_em_sessao_de_prospeccao_entra_na_trilha_da_sessao()
    {
        // VEI-RD-83 cenário 7: "o que foi criado na sessão" consta na auditoria.
        const int tenant = 893;
        const string email = "checkout-prosp-893@exemplo.com";
        var piece = await PrepareAsync(tenant, email, "P-CHK-893", 893);
        await ApproveAsync(tenant, email);
        const string admin = "prosp-admin-893@exemplo.com";
        await Seed.OperadorAsync(tenant, admin, new[] { "PedidoCriar", "UsuarioAfiliadaGerenciar" });
        int anuncianteId;
        using (var lookup = new VeiculandoDataContext())
            anuncianteId = (await lookup.WlUsuariosAnunciante
                .SingleAsync(u => u.AfiliadaId == tenant && u.Email.Endereco == email)).Id;

        using var factory = new WlApiFactory(_db, tenant);
        using var operador = await factory.ClienteAutenticadoAsync(admin, Seed.SenhaPadrao);
        var emissao = await operador.PostAsJsonAsync("/api/wl/prospeccao/sessao", new { anuncianteId });
        emissao.StatusCode.Should().Be(HttpStatusCode.OK, await emissao.Content.ReadAsStringAsync());
        var sessao = await emissao.Content.ReadFromJsonAsync<ProspeccaoResponse>();

        using var client = factory.ClienteAnonimo();
        var resgate = await client.PostAsJsonAsync("/api/wl/app/prospeccao/sessao",
            new { Token = sessao.Token, OperadorId = sessao.FonteUsuarioId, AnuncianteId = anuncianteId });
        resgate.StatusCode.Should().Be(HttpStatusCode.OK, await resgate.Content.ReadAsStringAsync());
        var login = await resgate.Content.ReadFromJsonAsync<LoginResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);

        var quote = await (await client.PostAsJsonAsync("/api/wl/app/checkout/quote",
            new { pieceIds = new[] { piece }, campaignId = 1, periodCode = "APP893" }))
            .Content.ReadFromJsonAsync<QuoteResponse>();
        client.DefaultRequestHeaders.Add("Idempotency-Key", "checkout-893-prospeccao");
        var order = await client.PostAsJsonAsync("/api/wl/app/checkout/orders",
            new { quoteId = quote.QuoteId, termsAccepted = true, termsVersion = "aurum-v1" });
        order.StatusCode.Should().Be(HttpStatusCode.OK, await order.Content.ReadAsStringAsync());

        using var ctx = new VeiculandoDataContext();
        var pedido = await ctx.Pedidos.SingleAsync(p => p.IdCampanha == 1 && p.IdPeriodo == 893);
        var codigo = pedido.Codigo;
        // Quem cria o pedido na prospecção é o usuário da exibidora que emitiu a
        // sessão, em nome da afiliada; não o anunciante da campanha.
        pedido.FonteOrigem.Should().Be(Veiculando.Domain.Enums.FonteOrigemEnum.WhiteLabel);
        pedido.FonteAgenciaId.Should().Be(tenant);
        pedido.FonteUsuarioId.Should().Be(sessao.FonteUsuarioId);
        var trilha = await ctx.WlProspeccaoSessaoEventos
            .Where(e => e.AfiliadaId == tenant && e.Evento == WlProspeccaoEventoTipo.PedidoCriado).ToListAsync();
        var evento = trilha.Should().ContainSingle().Which;
        evento.CodigosPedido.Should().Contain(codigo);
        evento.OperadorId.Should().Be(sessao.FonteUsuarioId, "a trilha registra o emissor como criador do pedido");

        var auditoria = await operador.GetStringAsync("/api/wl/prospeccao/auditoria");
        auditoria.Should().Contain(codigo);
        auditoria.Should().NotContain(sessao.Token);
    }

    private sealed record ProspeccaoResponse(string Token, int FonteUsuarioId);

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
    private sealed record QuoteResponse(Guid QuoteId, decimal Total, DateTime ExpiresAt);
}
