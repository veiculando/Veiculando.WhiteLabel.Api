using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Data.Entity.Infrastructure;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities;
using Veiculando.Domain.Enums;
using Veiculando.Domain.ValueObjects;
using Veiculando.WhiteLabel.Api.Configurations;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;

namespace Veiculando.WhiteLabel.Api.Controllers;

[ApiController]
[Route("api/wl")]
[Authorize]
public sealed class UploadsController : WlCoreProxyControllerBase
{
    private readonly VeiculandoDataContext _db;
    private readonly ITenantQueries _tenant;
    private readonly IFileValidationService _validation;
    private readonly IWlUploadStorage _storage;
    private readonly WlUploadPipeline _pipeline;
    private readonly WlUploadReferences _references;
    private readonly ISeedAccountResolver _seed;
    private readonly ILogger<UploadsController> _logger;

    public UploadsController(VeiculandoDataContext db, ITenantQueries tenant, IFileValidationService validation,
        IWlUploadStorage storage, WlUploadPipeline pipeline, WlUploadReferences references,
        ISeedAccountResolver seed, ILogger<UploadsController> logger)
        => (_db, _tenant, _validation, _storage, _pipeline, _references, _seed, _logger) =
            (db, tenant, validation, storage, pipeline, references, seed, logger);

    [HttpPost("pecas/locais/{idLocal}/pecas/{pecaId}/foto")]
    [Authorize(Policy = AuthorizationSetup.PecaGerenciar)]
    [EnableRateLimiting(Startup.RateLimitEscrita)]
    [RequestSizeLimit(11 * 1024 * 1024)]
    public async Task<IActionResult> FotoPeca(int idLocal, int pecaId, [FromForm] IFormFile foto, CancellationToken ct)
    {
        var peca = await _tenant.Pecas.Include(p => p.Local).SingleOrDefaultAsync(p => p.Id == pecaId && p.IdLocal == idLocal
            && p.StatusExibicao != StatusExibicaoEnum.Deletado && p.Local.StatusExibicao != StatusExibicaoEnum.Deletado, ct);
        if (peca == null) return NotFound(new { message = "Peça não encontrada neste local." });
        if (!FotoValida(foto, 10 * 1024 * 1024, out var error)) return BadRequest(new { message = error });
        return await ExecutarUpload(foto, "pecas", pecaId, 10 * 1024 * 1024, async file =>
        {
            peca.AlterarFoto(file.FileName);
            peca.RegistrarOrigem(FonteOrigemEnum.WhiteLabel, null, WlUsuarioId);
            await _db.SaveChangesAsync(ct);
        }, ct);
    }

    [HttpPost("checking/enviar-foto/{idItemPI}")]
    [Authorize(Policy = AuthorizationSetup.Checking)]
    [EnableRateLimiting(Startup.RateLimitEscrita)]
    [RequestSizeLimit(16 * 1024 * 1024)]
    public async Task<IActionResult> FotoChecking(int idItemPI, [FromForm] IFormFile foto, CancellationToken ct)
    {
        var item = await _tenant.PedidoInsercaoItens
            .Include(i => i.PedidoItem.Peca.Local)
            .Include(i => i.PedidoInsercao.Pedido)
            .Include(i => i.PedidoInsercao.Pedido.Campanha.Pedidos.Select(p => p.PedidosInsercao))
            .Include(i => i.PedidoInsercao.Itens.Select(x => x.PedidoItem))
            .SingleOrDefaultAsync(i => i.IdPedidoItem == idItemPI && i.PedidoInsercao.StatusExibicao == StatusExibicaoEnum.Ativo, ct);
        if (item == null) return NotFound(new { message = "Item de PI não encontrado." });
        if (!FotoValida(foto, 15 * 1024 * 1024, out var error)) return BadRequest(new { message = error });

        UsuarioAfiliada actor;
        try
        {
            var account = _seed.Resolve();
            actor = await _db.UsuariosAfiliada.SingleOrDefaultAsync(u => u.IdAfiliada == _tenant.AfiliadaId
                && u.Email.Endereco == account.Email && u.StatusExibicao == StatusExibicaoEnum.Ativo, ct);
            if (actor == null) throw new InvalidOperationException("Conta de serviço Core ausente no tenant.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "WL_UPLOAD_CORE_ACTOR_UNAVAILABLE tenant={Tenant}", _tenant.AfiliadaId);
            return StatusCode(503, new { message = "A conta de serviço da exibidora não está disponível. Nenhuma foto foi salva." });
        }

        return await ExecutarUpload(foto, "checking", idItemPI, 15 * 1024 * 1024, async file =>
        {
            using var transaction = _db.Database.BeginTransaction(System.Data.IsolationLevel.Serializable);
            var checking = await _db.Checkings
                .Include(c => c.Itens.Select(i => i.Fotos))
                .Include(c => c.Itens.Select(i => i.Peca.Local))
                .Include(c => c.Itens.Select(i => i.PedidoInsercaoItem.PedidoItem))
                .SingleOrDefaultAsync(c => c.IdPedidoInsercao == item.IdPedidoInsercao, ct);
            if (checking == null)
            {
                checking = new Checking(item.PedidoInsercao, actor);
                _db.Checkings.Add(checking);
            }
            var checkingItem = checking.Itens.SingleOrDefault(i => i.IdPedidoItem == idItemPI)
                ?? new CheckingItem(checking, item, actor);
            var metadata = new Dictionary<string, string>
            {
                ["FonteOrigem"] = "WhiteLabel", ["FonteUsuarioId"] = WlUsuarioId.Value.ToString(),
                ["Sha256"] = file.Sha256, ["ContentType"] = file.ContentType,
                ["CapturaGPS"] = "NaoInformada", ["RecebidoEm"] = file.CreatedAt.ToString("O")
            };
            // Não inventar EXIF/geolocalização: arquivo de disco não comprova posição de captura.
            var evidence = new CheckingFoto(checkingItem, WlUploadReferences.Prefix + file.Key, null,
                checked((int)file.Size), new Geolocalizacao(0, 0), metadata, null, actor);
            checkingItem.RegistrarFoto(evidence);
            await _db.SaveChangesAsync(ct);
            transaction.Commit();
        }, ct);
    }

    [HttpGet("pecas/locais/{idLocal}/pecas/{pecaId}/fotos")]
    [Authorize(Policy = AuthorizationSetup.PecaGerenciar)]
    public async Task<IActionResult> FotosPeca(int idLocal, int pecaId, CancellationToken ct)
    {
        var peca = await _tenant.Pecas.AsNoTracking().SingleOrDefaultAsync(p => p.Id == pecaId && p.IdLocal == idLocal
            && p.StatusExibicao != StatusExibicaoEnum.Deletado && p.Local.StatusExibicao != StatusExibicaoEnum.Deletado, ct);
        if (peca == null) return NotFound(new { message = "Peça não encontrada neste local." });
        var name = peca.Foto?.ArquivoNome;
        if (string.IsNullOrEmpty(name) || !System.Text.RegularExpressions.Regex.IsMatch(name, @"^wl-[a-f0-9]{32}\.(jpg|png)$"))
            return Ok(Array.Empty<object>()); // Foto legada não é um blob privado WL.
        var key = Key("pecas", pecaId, name);
        try { return Ok(new[] { Descriptor(await _storage.InfoAsync(key, ct), $"/api/wl/pecas/locais/{idLocal}/pecas/{pecaId}/foto") }); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return StorageError(ex); }
    }

    [HttpGet("checking/item/{idItemPI}/fotos")]
    [Authorize(Policy = AuthorizationSetup.Checking)]
    public async Task<IActionResult> FotosChecking(int idItemPI, CancellationToken ct)
    {
        if (!await _tenant.PedidoInsercaoItens.AnyAsync(i => i.IdPedidoItem == idItemPI && i.PedidoInsercao.StatusExibicao == StatusExibicaoEnum.Ativo, ct))
            return NotFound(new { message = "Item de PI não encontrado." });
        var photos = await _db.CheckingFotos.AsNoTracking().Where(f => f.IdPedidoItem == idItemPI
            && f.CheckingItem.PedidoInsercaoItem.PedidoInsercao.IdAfiliada == _tenant.AfiliadaId
            && f.UrlArquivo.StartsWith(WlUploadReferences.Prefix) && f.StatusExibicao != StatusExibicaoEnum.Deletado).ToListAsync(ct);
        var result = new List<object>();
        try
        {
            foreach (var photo in photos)
            {
                var key = photo.UrlArquivo.Substring(WlUploadReferences.Prefix.Length);
                if (!KeyBelongs(key, "checking", idItemPI)) throw new InvalidOperationException("Referência de evidência inconsistente.");
                result.Add(Descriptor(await _storage.InfoAsync(key, ct), $"/api/wl/checking/item/{idItemPI}/fotos/{photo.Id}/arquivo", photo.Status.ToString()));
            }
            return Ok(result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return StorageError(ex); }
    }

    [HttpGet("pecas/locais/{idLocal}/pecas/{pecaId}/foto")]
    [Authorize(Policy = AuthorizationSetup.PecaGerenciar)]
    public async Task<IActionResult> BaixarPeca(int idLocal, int pecaId, CancellationToken ct)
    {
        var peca = await _tenant.Pecas.AsNoTracking().SingleOrDefaultAsync(p => p.Id == pecaId && p.IdLocal == idLocal
            && p.StatusExibicao != StatusExibicaoEnum.Deletado && p.Local.StatusExibicao != StatusExibicaoEnum.Deletado, ct);
        if (peca == null || string.IsNullOrEmpty(peca.Foto?.ArquivoNome)) return NotFound();
        var key = Key("pecas", pecaId, peca.Foto.ArquivoNome);
        if (!KeyBelongs(key, "pecas", pecaId)) return NotFound();
        return await Download(key, ct);
    }

    [HttpGet("checking/item/{idItemPI}/fotos/{fotoId}/arquivo")]
    [Authorize(Policy = AuthorizationSetup.Checking)]
    public async Task<IActionResult> BaixarChecking(int idItemPI, int fotoId, CancellationToken ct)
    {
        var photo = await _db.CheckingFotos.AsNoTracking().SingleOrDefaultAsync(f => f.Id == fotoId && f.IdPedidoItem == idItemPI
            && f.CheckingItem.PedidoInsercaoItem.PedidoInsercao.IdAfiliada == _tenant.AfiliadaId
            && f.CheckingItem.PedidoInsercaoItem.PedidoInsercao.StatusExibicao == StatusExibicaoEnum.Ativo
            && f.StatusExibicao != StatusExibicaoEnum.Deletado, ct);
        if (photo == null || !photo.UrlArquivo.StartsWith(WlUploadReferences.Prefix)) return NotFound();
        var key = photo.UrlArquivo.Substring(WlUploadReferences.Prefix.Length);
        if (!KeyBelongs(key, "checking", idItemPI)) return NotFound();
        return await Download(key, ct);
    }

    private async Task<IActionResult> Download(string key, CancellationToken ct)
    {
        try
        {
            var file = await _storage.InfoAsync(key, ct);
            Response.Headers.CacheControl = "private, no-store";
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            return File(await _storage.ReadAsync(key, ct), file.ContentType, file.FileName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return StorageError(ex); }
    }

    private bool FotoValida(IFormFile file, long maxBytes, out string error)
    {
        if (!_validation.IsValidFile(file, maxBytes, out error)) return false;
        if (file.ContentType != "image/jpeg" && file.ContentType != "image/png") { error = "Envie uma foto JPG ou PNG."; return false; }
        return true;
    }

    private async Task<IActionResult> ExecutarUpload(IFormFile foto, string kind, int id, long limit, Func<WlStoredFile, Task> commit, CancellationToken ct)
    {
        if (WlUsuarioId is not > 0) return Forbid();
        try
        {
            using var body = new MemoryStream();
            using var source = foto.OpenReadStream();
            var buffer = new byte[81920];
            int count;
            while ((count = await source.ReadAsync(buffer, ct)) > 0)
            {
                if (body.Length + count > limit) return StatusCode(413, new { message = "Arquivo maior que o limite permitido." });
                await body.WriteAsync(buffer.AsMemory(0, count), ct);
            }
            if (body.Length != foto.Length || body.Length == 0) return BadRequest(new { message = "Upload incompleto. Selecione o arquivo e tente novamente." });
            body.Position = 0;
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(body, ct)).ToLowerInvariant();
            body.Position = 0;
            var name = "wl-" + Guid.NewGuid().ToString("N") + (foto.ContentType == "image/png" ? ".png" : ".jpg");
            var file = new WlStoredFile(Key(kind, id, name), name, foto.ContentType, body.Length, hash, DateTimeOffset.UtcNow);
            await _pipeline.SaveAsync(file, body, WlUsuarioId.Value, () => commit(file),
                () => _references.ExistsAsync(file.Key, CancellationToken.None), ct);
            return Ok(new { message = "Foto salva. Recarregue a lista para conferir.", fileName = name, sha256 = hash, size = file.Size });
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { message = "O registro foi alterado por outra operação. Recarregue antes de reenviar." });
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return StorageError(ex); }
    }

    private IActionResult StorageError(Exception error)
    {
        _logger.LogError(error, "WL_UPLOAD_FAILED tenant={Tenant} actor={Actor}", _tenant.AfiliadaId, WlUsuarioId);
        return StatusCode(503, new { message = "Não foi possível confirmar o arquivo. Recarregue a lista antes de tentar novamente." });
    }
    private string Key(string kind, int id, string name) => $"tenant-{_tenant.AfiliadaId}/{kind}/{id}/{name}";
    private bool KeyBelongs(string key, string kind, int id)
    {
        try { var parsed = WlUploadKey.Parse(key); return parsed.TenantId == _tenant.AfiliadaId && parsed.Kind == kind && parsed.ResourceId == id; }
        catch (ArgumentException) { return false; }
    }
    private static object Descriptor(WlStoredFile file, string downloadUrl, string status = null) =>
        new { file.FileName, file.ContentType, file.Size, file.Sha256, file.CreatedAt, downloadUrl, status };
}
