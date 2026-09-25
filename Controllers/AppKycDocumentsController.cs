using System;
using System.Data.Entity;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities.WhiteLabel;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;

namespace Veiculando.WhiteLabel.Api.Controllers;

[ApiController]
[Route("api/wl/app/kyc/documents")]
[Authorize]
public sealed class AppKycDocumentsController : ControllerBase
{
    private readonly VeiculandoDataContext _db;
    private readonly ITenantQueries _tenant;
    private readonly IFileValidationService _validation;
    private readonly IWlUploadStorage _storage;
    private readonly WlUploadPipeline _pipeline;
    private readonly WlUploadReferences _references;

    public AppKycDocumentsController(VeiculandoDataContext db, ITenantQueries tenant,
        IFileValidationService validation, IWlUploadStorage storage,
        WlUploadPipeline pipeline, WlUploadReferences references)
        => (_db, _tenant, _validation, _storage, _pipeline, _references) =
            (db, tenant, validation, storage, pipeline, references);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!TryUser(out var userId)) return Unauthorized();
        var onboarding = await _db.WlAppOnboardings.AsNoTracking().Include(o => o.Documentos)
            .SingleOrDefaultAsync(o => o.UsuarioId == userId && o.AfiliadaId == _tenant.AfiliadaId, ct);
        if (onboarding == null) return Ok(Array.Empty<object>());
        return Ok(onboarding.Documentos.Where(d => d.Ativo)
            .Select(d => new { d.Id, d.Nome, d.Tipo, d.ContentType, d.Tamanho }));
    }

    [HttpPost]
    [EnableRateLimiting(Startup.RateLimitEscrita)]
    [RequestSizeLimit(11 * 1024 * 1024)]
    public async Task<IActionResult> Upload([FromForm] string type, [FromForm] IFormFile file, CancellationToken ct)
    {
        if (!TryUser(out var userId)) return Unauthorized();
        if (type != "corporate" && type != "representative" && type != "address")
            return BadRequest(new { message = "Tipo de documento inválido." });
        if (!_validation.IsValidFile(file, 10 * 1024 * 1024, out var error))
            return BadRequest(new { message = error });
        var onboarding = await _db.WlAppOnboardings.Include(o => o.Documentos)
            .SingleOrDefaultAsync(o => o.UsuarioId == userId && o.AfiliadaId == _tenant.AfiliadaId, ct);
        if (onboarding == null || (onboarding.Status != WlAppKycStatus.PendenteVerificacao &&
            onboarding.Status != WlAppKycStatus.AjustesSolicitados))
            return Conflict(new { message = "Envie os dados cadastrais antes do documento ou solicite ajuste da análise." });

        using var body = new MemoryStream();
        using (var source = file.OpenReadStream()) await source.CopyToAsync(body, ct);
        if (body.Length != file.Length || body.Length == 0)
            return BadRequest(new { message = "Upload incompleto." });
        body.Position = 0;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(body, ct)).ToLowerInvariant();
        body.Position = 0;
        var extension = file.ContentType switch
        {
            "application/pdf" => ".pdf", "image/png" => ".png", _ => ".jpg"
        };
        var name = "wl-" + Guid.NewGuid().ToString("N") + extension;
        var key = $"tenant-{_tenant.AfiliadaId}/kyc/{userId}/{name}";
        var stored = new WlStoredFile(key, name, file.ContentType, body.Length, hash, DateTimeOffset.UtcNow);
        await _pipeline.SaveAsync(stored, body, userId, async () =>
        {
            foreach (var previous in onboarding.Documentos.Where(d => d.Ativo && d.Tipo == type)) previous.Desativar();
            _db.WlAppDocumentos.Add(new WlAppDocumento(onboarding, Path.GetFileName(file.FileName).Substring(0, Math.Min(Path.GetFileName(file.FileName).Length, 255)), key,
                file.ContentType, body.Length, type));
            await _db.SaveChangesAsync(ct);
        }, () => _references.ExistsAsync(key, CancellationToken.None), ct);
        return Ok(new { message = "Documento recebido para análise.", type });
    }

    private bool TryUser(out int id)
    {
        id = 0;
        return User.FindFirstValue("WlPerfil") == "Anunciante" &&
            int.TryParse(User.FindFirstValue("WlAnuncianteId"), out id);
    }
}
