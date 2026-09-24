using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Entity;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities;
using Veiculando.Domain.Entities.Pedidos;
using Veiculando.Domain.Entities.WhiteLabel;
using Veiculando.Domain.Enums;
using Veiculando.Infra.Security;
using Veiculando.WhiteLabel.Api.Middleware;

namespace Veiculando.WhiteLabel.Api.Controllers;

/// <summary>
/// Cotação server-side do carrinho. O preço, a campanha e o período são
/// revalidados antes de qualquer gravação de Pedido.
/// </summary>
[ApiController]
[Route("api/wl/app/checkout")]
[Authorize]
public sealed class AppCheckoutController : ControllerBase
{
    private readonly VeiculandoDataContext _db;
    private readonly ITenantQueries _tenant;
    private readonly JwtSettings _jwt;

    public AppCheckoutController(VeiculandoDataContext db, ITenantQueries tenant, IOptions<JwtSettings> jwt)
        => (_db, _tenant, _jwt) = (db, tenant, jwt.Value);

    [HttpPost("quote")]
    [EnableRateLimiting(Startup.RateLimitEscrita)]
    public async Task<IActionResult> Quote([FromBody] QuoteRequest request, CancellationToken ct)
    {
        if (!TryUser(out var userId)) return Unauthorized();
        if (request?.PieceIds == null || request.PieceIds.Length == 0 || request.PieceIds.Length > 100 ||
            request.PieceIds.Any(id => id <= 0) || request.PieceIds.Distinct().Count() != request.PieceIds.Length ||
            request.CampaignId <= 0 || string.IsNullOrWhiteSpace(request.PeriodCode))
            return BadRequest(new { message = "Informe peças, campanha e período válidos." });

        var buyer = await _tenant.UsuariosAnunciante.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (buyer == null) return Unauthorized();
        var onboarding = await _db.WlAppOnboardings.AsNoTracking().SingleOrDefaultAsync(o =>
            o.UsuarioId == userId && o.AfiliadaId == _tenant.AfiliadaId, ct);
        if (onboarding?.Status != WlAppKycStatus.Aprovado || string.IsNullOrWhiteSpace(onboarding.Documento))
            return StatusCode(403, new { message = "O cadastro deve estar aprovado para cotar." });

        var campaign = await EligibleCampaign(onboarding.Documento, request.CampaignId).SingleOrDefaultAsync(ct);
        if (campaign == null) return NotFound(new { message = "Campanha não encontrada para este anunciante e exibidora." });
        if (campaign.AnuncianteResponsavel?.Agencia == null)
            return Conflict(new { message = "A campanha não possui responsável comercial habilitado para pedidos." });

        var period = await _db.Periodos.SingleOrDefaultAsync(p =>
            p.Codigo == request.PeriodCode && p.StatusExibicao == StatusExibicaoEnum.Ativo &&
            p.DataFim >= DateTime.UtcNow && p.DataInicio <= campaign.DataFimPrevisto &&
            p.DataFim >= campaign.DataInicioPrevisto, ct);
        if (period == null) return BadRequest(new { message = "Período indisponível para esta campanha." });

        var pieces = await ActivePieces(request.PieceIds).ToListAsync(ct);
        if (pieces.Count != request.PieceIds.Length)
            return BadRequest(new { message = "Uma ou mais peças não pertencem ao inventário publicado desta exibidora." });
        if (pieces.Any(p => p.PeriodicidadePadrao?.Tipo != period.Periodicidade.Tipo))
            return BadRequest(new { message = "As peças selecionadas não usam a periodicidade do período escolhido." });

        var unavailable = await _tenant.PecaPeriodoStatus.AsNoTracking().AnyAsync(s =>
            request.PieceIds.Contains(s.IdPeca) && s.IdPeriodo == period.Id &&
            s.Status != StatusPecaPeriodoEnum.Disponivel, ct);
        if (unavailable) return Conflict(new { message = "Uma ou mais peças já estão indisponíveis neste período." });

        var orders = BuildOrders(campaign, period, pieces, simulation: true);
        if (orders == null) return Conflict(new { message = "Não foi possível calcular a cotação com os dados comerciais atuais." });
        var total = Money(orders.Sum(order => order.ValorTotalBruto));
        if (total <= 0) return Conflict(new { message = "A cotação não possui valor válido." });

        var items = pieces.OrderBy(p => p.Id).Select(p => new QuoteItem(p.Id, p.Codigo,
            Money(orders.SelectMany(o => o.Itens).Single(i => i.IdPeca == p.Id).ValorBruto))).ToArray();
        var json = JsonSerializer.Serialize(items);
        // SQL Server datetime arredonda frações de segundo; persistir um instante
        // exato evita que a assinatura mude após ler a cotação do banco.
        var nowTicks = DateTime.UtcNow.Ticks;
        var expiresAt = new DateTime(nowTicks - nowTicks % TimeSpan.TicksPerSecond, DateTimeKind.Utc).AddMinutes(15);
        var integrity = Sign(userId, _tenant.AfiliadaId, campaign.Id, period.Codigo, json, total, expiresAt);
        var quote = new WlAppCheckoutQuote(buyer, campaign.Id, period.Codigo, json, total, integrity, expiresAt);
        _db.WlAppCheckoutQuotes.Add(quote);
        await _db.SaveChangesAsync(ct);

        return Ok(new { quoteId = quote.Id, items = items.Select(i => new { id = i.Id, code = i.Code, serverPrice = i.ServerPrice }), total, expiresAt });
    }

    [HttpPost("orders")]
    [EnableRateLimiting(Startup.RateLimitEscrita)]
    public async Task<IActionResult> Orders([FromBody] OrderRequest request, CancellationToken ct)
    {
        if (!TryUser(out var userId)) return Unauthorized();
        if (request == null || request.QuoteId == Guid.Empty || !request.TermsAccepted ||
            string.IsNullOrWhiteSpace(request.TermsVersion) || request.TermsVersion.Length > 40 ||
            !Request.Headers.TryGetValue("Idempotency-Key", out var keyHeader) ||
            keyHeader.Count != 1 || string.IsNullOrWhiteSpace(keyHeader[0]) || keyHeader[0].Length > 128)
            return BadRequest(new { message = "Informe cotação, aceite dos termos e chave de idempotência." });

        var keyHash = HashKey(userId, _tenant.AfiliadaId, keyHeader[0]);
        using var transaction = _db.Database.BeginTransaction(IsolationLevel.Serializable);
        var previous = await _db.WlAppCheckoutSubmissions.AsNoTracking()
            .SingleOrDefaultAsync(s => s.IdempotencyHash == keyHash &&
                s.UsuarioId == userId && s.AfiliadaId == _tenant.AfiliadaId, ct);
        if (previous != null)
        {
            if (previous.QuoteId != request.QuoteId)
                return Conflict(new { message = "Esta chave já foi usada para outra cotação." });
            return Content(previous.CodigosPedidoJson, "application/json");
        }

        var quote = await _db.WlAppCheckoutQuotes.Include(q => q.Usuario)
            .SingleOrDefaultAsync(q => q.Id == request.QuoteId && q.UsuarioId == userId &&
                q.AfiliadaId == _tenant.AfiliadaId, ct);
        if (quote == null) return NotFound(new { message = "Cotação não encontrada." });
        if (quote.ConsumidaEm.HasValue || quote.ExpiraEm <= DateTime.UtcNow)
            return Conflict(new { message = "Cotação expirada ou já confirmada; solicite uma nova." });

        QuoteItem[] quotedItems;
        try { quotedItems = JsonSerializer.Deserialize<QuoteItem[]>(quote.PecasJson); }
        catch (JsonException) { return Conflict(new { message = "Cotação inválida; solicite uma nova." }); }
        if (quotedItems == null || quotedItems.Length == 0 || quotedItems.Length > 100 ||
            quotedItems.Any(i => i.Id <= 0) || quotedItems.Select(i => i.Id).Distinct().Count() != quotedItems.Length)
            return Conflict(new { message = "Cotação inválida; solicite uma nova.", code = "invalid_snapshot" });
        if (!FixedEquals(quote.Integridade, Sign(userId, _tenant.AfiliadaId, quote.CampanhaId,
                quote.CodigoPeriodo, quote.PecasJson, quote.ValorTotal, quote.ExpiraEm)))
            return Conflict(new { message = "Cotação inválida; solicite uma nova.", code = "invalid_integrity" });

        var onboarding = await _db.WlAppOnboardings.AsNoTracking().SingleOrDefaultAsync(o =>
            o.UsuarioId == userId && o.AfiliadaId == _tenant.AfiliadaId, ct);
        if (onboarding?.Status != WlAppKycStatus.Aprovado || string.IsNullOrWhiteSpace(onboarding.Documento))
            return StatusCode(403, new { message = "O cadastro deve estar aprovado para fechar o pedido." });
        var campaign = await EligibleCampaign(onboarding.Documento, quote.CampanhaId).SingleOrDefaultAsync(ct);
        if (campaign?.AnuncianteResponsavel?.Agencia == null)
            return Conflict(new { message = "Campanha ou responsável comercial indisponível." });
        var period = await _db.Periodos.SingleOrDefaultAsync(p =>
            p.Codigo == quote.CodigoPeriodo && p.StatusExibicao == StatusExibicaoEnum.Ativo &&
            p.DataFim >= DateTime.UtcNow && p.DataInicio <= campaign.DataFimPrevisto &&
            p.DataFim >= campaign.DataInicioPrevisto, ct);
        if (period == null) return Conflict(new { message = "Período indisponível; solicite nova cotação." });
        var ids = quotedItems.Select(i => i.Id).ToArray();
        var pieces = await ActivePieces(ids).ToListAsync(ct);
        if (pieces.Count != ids.Length || pieces.Any(p => p.PeriodicidadePadrao?.Tipo != period.Periodicidade.Tipo))
            return Conflict(new { message = "Inventário alterado; solicite nova cotação." });
        var status = await _tenant.PecaPeriodoStatus.Where(s => ids.Contains(s.IdPeca) && s.IdPeriodo == period.Id)
            .ToListAsync(ct);
        if (status.Any(s => s.Status != StatusPecaPeriodoEnum.Disponivel))
            return Conflict(new { message = "Uma ou mais peças ficaram indisponíveis; solicite nova cotação." });

        var orders = BuildOrders(campaign, period, pieces, simulation: false);
        if (orders == null || Money(orders.Sum(o => o.ValorTotalBruto)) != quote.ValorTotal ||
            quotedItems.Any(i => !pieces.Any(p => p.Id == i.Id && p.Codigo == i.Code) ||
                Money(orders.SelectMany(o => o.Itens).Single(item => item.IdPeca == i.Id).ValorBruto) != i.ServerPrice))
            return Conflict(new { message = "Preço ou disponibilidade alterados; solicite nova cotação." });

        var cityIds = pieces.Select(p => p.Local.IdCidade).Distinct().ToArray();
        if (await _db.Pedidos.AnyAsync(p => p.IdCampanha == campaign.Id && p.IdPeriodo == period.Id &&
            cityIds.Contains(p.IdCidade) && p.Status != StatusPedidoEnum.Cancelado &&
            p.Status != StatusPedidoEnum.Revisado, ct))
            return Conflict(new { message = "Já existe pedido nesta campanha, cidade e período." });

        foreach (var order in orders)
        {
            order.RegistrarOrigem(FonteOrigemEnum.WhiteLabel, campaign.IdAgencia, campaign.IdUsuarioAnunciante);
            campaign.AdicionarPedido(order);
            _db.Pedidos.Add(order);
            foreach (var item in order.Itens)
            {
                var existing = status.SingleOrDefault(s => s.IdPeca == item.IdPeca);
                if (existing == null)
                    _db.PecaPeriodoStatus.Add(new PecaPeriodoStatus(item.Peca, period, order));
            }
        }
        foreach (var reserve in PedidoReserva.GerarPedidosDeReserva(orders))
            _db.PedidosReserva.Add(reserve);

        quote.Consumir(DateTime.UtcNow);
        var receipt = JsonSerializer.Serialize(new
        {
            quoteId = quote.Id,
            orders = orders.Select(o => new { code = o.Codigo, city = o.Cidade.Nome, period = period.Codigo }).ToArray(),
            total = quote.ValorTotal,
            termsVersion = request.TermsVersion,
            acceptedAt = DateTime.UtcNow
        });
        _db.WlAppCheckoutSubmissions.Add(new WlAppCheckoutSubmission(quote, keyHash, receipt));
        await _db.SaveChangesAsync(ct);
        foreach (var available in status)
        {
            var updated = await _db.Database.ExecuteSqlCommandAsync(
                "UPDATE dbo.PecaPeriodoStatus SET Status = 1, IdPedido = @p0 WHERE Id = @p1 AND Status = 0",
                orders.Single(o => o.Itens.Any(i => i.IdPeca == available.IdPeca)).Id, available.Id);
            if (updated != 1)
                return Conflict(new { message = "Grade de disponibilidade alterada; solicite nova cotação." });
        }
        transaction.Commit();
        return Content(receipt, "application/json");
    }

    internal IQueryable<Campanha> EligibleCampaign(string document, int campaignId) => _db.Campanhas
        .Include(c => c.Cliente)
        .Include(c => c.Agencia.ContratosCliente)
        .Include(c => c.AnuncianteResponsavel.Agencia)
        .Where(c => c.Id == campaignId && c.StatusExibicao == StatusExibicaoEnum.Ativo &&
            c.Status != StatusCampanhaEnum.Cancelada && c.Status != StatusCampanhaEnum.Aprovada &&
            c.Cliente.Cnpj.Numero == document &&
            c.Cliente.AfiliadasVinculadas.Any(v => v.IdAfiliada == _tenant.AfiliadaId &&
                v.Status == StatusVinculoEnum.Ativo));

    internal IQueryable<Peca> ActivePieces(int[] ids) => _tenant.Pecas
        .Include(p => p.Local.Cidade)
        .Include(p => p.Local.Afiliada)
        .Include(p => p.ValoresSazonais.Select(v => v.Periodo))
        .Where(p => ids.Contains(p.Id) && p.StatusExibicao == StatusExibicaoEnum.Ativo &&
            p.Local.StatusExibicao == StatusExibicaoEnum.Ativo);

    internal static List<Pedido> BuildOrders(Campanha campaign, Periodo period, List<Peca> pieces, bool simulation)
    {
        var orders = new List<Pedido>();
        foreach (var group in pieces.GroupBy(p => p.Local.IdCidade))
        {
            var city = group.First().Local.Cidade;
            var order = new Pedido(campaign, campaign.AnuncianteResponsavel, city, period,
                null, null, null, null, null, simulation);
            foreach (var piece in group) order.AddItem(piece);
            if (!order.IsValid() || order.Itens.Count != group.Count()) return null;
            orders.Add(order);
        }
        return orders;
    }

    internal string Sign(int userId, int tenantId, int campaignId, string periodCode,
        string piecesJson, decimal total, DateTime expiresAt)
    {
        var value = string.Join("|", userId, tenantId, campaignId, periodCode, piecesJson,
            total.ToString("0.00", CultureInfo.InvariantCulture), expiresAt.Ticks);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_jwt.Secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static string HashKey(int userId, int tenantId, string key)
    {
        var input = Encoding.UTF8.GetBytes($"{tenantId}|{userId}|{key}");
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }

    // Os valores do Pedido no Core são gravados em decimal(18,2) pelo EF6.
    // Alinhar a cotação à mesma escala evita assinar um total que muda ao persistir.
    private static decimal Money(decimal value) => decimal.Truncate(value * 100m) / 100m;

    private static bool FixedEquals(string actual, string expected)
    {
        if (actual == null || actual.Length != expected.Length) return false;
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(expected)); }
        catch (FormatException) { return false; }
    }

    internal bool TryUser(out int id)
    {
        id = 0;
        return User.FindFirstValue("WlPerfil") == "Anunciante" &&
            int.TryParse(User.FindFirstValue("WlAnuncianteId"), out id);
    }

    public sealed class QuoteRequest
    {
        public int[] PieceIds { get; set; }
        public int CampaignId { get; set; }
        public string PeriodCode { get; set; }
    }

    public sealed class OrderRequest
    {
        public Guid QuoteId { get; set; }
        public bool TermsAccepted { get; set; }
        public string TermsVersion { get; set; }
    }

    public sealed record QuoteItem(int Id, string Code, decimal ServerPrice);
}
