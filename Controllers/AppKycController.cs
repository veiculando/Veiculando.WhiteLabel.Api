using System;
using System.ComponentModel.DataAnnotations;
using System.Data.Entity;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities.WhiteLabel;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;

namespace Veiculando.WhiteLabel.Api.Controllers;

[ApiController]
[Route("api/wl/app/kyc")]
[Authorize]
public sealed class AppKycController : ControllerBase
{
    private readonly VeiculandoDataContext _db;
    private readonly ITenantQueries _tenant;
    private readonly IWlCompanyRegistry _companies;

    public AppKycController(VeiculandoDataContext db, ITenantQueries tenant, IWlCompanyRegistry companies)
    { _db = db; _tenant = tenant; _companies = companies; }

    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        if (!TryUser(out var userId)) return Unauthorized();
        var onboarding = await _db.WlAppOnboardings.AsNoTracking().SingleOrDefaultAsync(o => o.UsuarioId == userId && o.AfiliadaId == _tenant.AfiliadaId, ct);
        return Ok(new { status = AppAuthController.KycStatusName(onboarding?.Status ?? WlAppKycStatus.Rascunho), updatedAt = onboarding?.AtualizadoEm ?? DateTime.UtcNow, step = onboarding?.Etapa ?? 0, reason = onboarding?.Motivo });
    }

    [HttpGet("company/{document}")]
    [EnableRateLimiting(Startup.RateLimitEscrita)]
    public async Task<IActionResult> Company(string document, CancellationToken ct)
    {
        if (!TryUser(out _)) return Unauthorized();
        var cnpj = WlAppDocuments.Normalize(document);
        if (!WlAppDocuments.Valid(cnpj, "pj")) return BadRequest(new { message = "CNPJ inválido." });
        var company = await _companies.LookupAsync(cnpj, ct);
        if (company == null) return NotFound(new { message = "CNPJ não encontrado." });
        return Ok(new { document = company.Document, legalName = company.LegalName, city = company.City, state = company.State, active = company.Active });
    }

    [HttpPost]
    [EnableRateLimiting(Startup.RateLimitEscrita)]
    public async Task<IActionResult> Submit([FromBody] KycRequest request, CancellationToken ct)
    {
        if (!TryUser(out var userId)) return Unauthorized();
        if (request == null || (request.AccountType != "ad" && request.AccountType != "ag") ||
            string.IsNullOrWhiteSpace(request.TradeName) || string.IsNullOrWhiteSpace(request.LegalName) ||
            string.IsNullOrWhiteSpace(request.Phone) || string.IsNullOrWhiteSpace(request.City) || string.IsNullOrWhiteSpace(request.State) ||
            string.IsNullOrWhiteSpace(request.Street) || string.IsNullOrWhiteSpace(request.Number) ||
            string.IsNullOrWhiteSpace(request.District) || string.IsNullOrWhiteSpace(request.ZipCode) ||
            string.IsNullOrWhiteSpace(request.RepresentativeName) || string.IsNullOrWhiteSpace(request.RepresentativeCpf) ||
            string.IsNullOrWhiteSpace(request.RepresentativeEmail) || string.IsNullOrWhiteSpace(request.RepresentativePhone))
            return BadRequest(new { message = "Preencha os dados obrigatórios." });

        var document = WlAppDocuments.Normalize(request.Document);
        if (!WlAppDocuments.Valid(document, "pj")) return BadRequest(new { message = "CNPJ inválido." });
        if (!WlAppDocuments.Valid(WlAppDocuments.Normalize(request.RepresentativeCpf), "pf"))
            return BadRequest(new { message = "CPF do responsável inválido." });
        var user = await _tenant.UsuariosAnunciante.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user == null) return Unauthorized();
        var identity = await _db.WlAppIdentidades.AsNoTracking().SingleOrDefaultAsync(i => i.Id == userId, ct);
        if (identity?.EmailConfirmado != true)
            return StatusCode(403, new { message = "Confirme seu e-mail antes de enviar o cadastro." });
        var onboarding = await _db.WlAppOnboardings.SingleOrDefaultAsync(o => o.UsuarioId == userId && o.AfiliadaId == _tenant.AfiliadaId, ct);
        if (onboarding == null) { onboarding = new WlAppOnboarding(user); _db.WlAppOnboardings.Add(onboarding); }
        try
        {
            var data = JsonSerializer.Serialize(new AppKycBusinessData
            {
                TradeName = request.TradeName.Trim(), LegalName = request.LegalName.Trim(),
                Phone = WlAppDocuments.Normalize(request.Phone), Email = request.Email?.Trim(), Website = request.Website?.Trim(),
                Street = request.Street.Trim(), Number = request.Number.Trim(), District = request.District.Trim(),
                Complement = request.Complement?.Trim(), ZipCode = WlAppDocuments.Normalize(request.ZipCode),
                City = request.City.Trim(), State = request.State.Trim().ToUpperInvariant(),
                StateTaxId = request.StateTaxId?.Trim(), MunicipalTaxId = request.MunicipalTaxId?.Trim(),
                RepresentativeName = request.RepresentativeName.Trim(), RepresentativeCpf = WlAppDocuments.Normalize(request.RepresentativeCpf),
                RepresentativeEmail = request.RepresentativeEmail.Trim().ToLowerInvariant(),
                RepresentativePhone = WlAppDocuments.Normalize(request.RepresentativePhone)
            });
            onboarding.SalvarRascunho(request.AccountType, document, data, 4);
            onboarding.ReivindicarDocumento(document);
            onboarding.Enviar();
            await _db.SaveChangesAsync(ct);
        }
        catch (InvalidOperationException) { return Conflict(new { message = "Este cadastro não pode ser alterado neste estado." }); }
        return Ok(new { status = AppAuthController.KycStatusName(onboarding.Status), updatedAt = onboarding.AtualizadoEm, step = onboarding.Etapa });
    }

    private bool TryUser(out int id)
    {
        id = 0;
        return User.FindFirstValue("WlPerfil") == "Anunciante" && int.TryParse(User.FindFirstValue("WlAnuncianteId"), out id);
    }

    public sealed class KycRequest
    {
        [Required, RegularExpression("^(ad|ag)$")] public string AccountType { get; set; }
        [Required, StringLength(200)] public string TradeName { get; set; }
        [Required, StringLength(200, MinimumLength = 3)] public string LegalName { get; set; }
        [Required, StringLength(30)] public string Document { get; set; }
        [Required, StringLength(30)] public string Phone { get; set; }
        [Required, StringLength(120)] public string City { get; set; }
        [Required, StringLength(2, MinimumLength = 2)] public string State { get; set; }
        [EmailAddress, StringLength(254)] public string Email { get; set; }
        [StringLength(250)] public string Website { get; set; }
        [Required, StringLength(150)] public string Street { get; set; }
        [Required, StringLength(50)] public string Number { get; set; }
        [Required, StringLength(150)] public string District { get; set; }
        [StringLength(500)] public string Complement { get; set; }
        [Required, StringLength(10)] public string ZipCode { get; set; }
        [StringLength(40)] public string StateTaxId { get; set; }
        [StringLength(40)] public string MunicipalTaxId { get; set; }
        [Required, StringLength(200)] public string RepresentativeName { get; set; }
        [Required, StringLength(20)] public string RepresentativeCpf { get; set; }
        [Required, EmailAddress] public string RepresentativeEmail { get; set; }
        [Required, StringLength(30)] public string RepresentativePhone { get; set; }
    }
}
