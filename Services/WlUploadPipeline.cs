using System;
using System.Data.Entity;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Veiculando.Data.Contexts;

namespace Veiculando.WhiteLabel.Api.Services;

/// <summary>Compensação e reconciliação usam consulta nova, nunca o estado ainda rastreado pelo EF.</summary>
public sealed class WlUploadReferences
{
    public const string Prefix = "wl-upload:";

    public async Task<bool> ExistsAsync(string key, CancellationToken ct)
    {
        var resource = WlUploadKey.Parse(key);
        using var db = new VeiculandoDataContext();
        if (resource.Kind == "pecas")
            return await db.Pecas.AnyAsync(p => p.Id == resource.ResourceId && p.Local.IdAfiliada == resource.TenantId
                && p.Foto.ArquivoNome == resource.FileName, ct);
        var reference = Prefix + key;
        return await db.CheckingFotos.AnyAsync(f => f.UrlArquivo == reference && f.IdPedidoItem == resource.ResourceId
            && f.CheckingItem.PedidoInsercaoItem.PedidoInsercao.IdAfiliada == resource.TenantId, ct);
    }
}

public sealed class WlUploadPipeline
{
    private readonly IWlUploadStorage _storage;
    private readonly ILogger<WlUploadPipeline> _logger;
    public WlUploadPipeline(IWlUploadStorage storage, ILogger<WlUploadPipeline> logger) => (_storage, _logger) = (storage, logger);

    public async Task SaveAsync(WlStoredFile file, Stream body, int actorId, Func<Task> commit,
        Func<Task<bool>> referenceExists, CancellationToken ct)
    {
        try
        {
            await _storage.PutAsync(file, body, actorId, ct);
            ct.ThrowIfCancellationRequested();
            await commit();
        }
        catch
        {
            // O cliente pode ter desconectado. Limpeza possui prazo próprio e nunca remove uma referência confirmada.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                if (!await referenceExists()) await _storage.DeleteAsync(file.Key, cleanup.Token);
            }
            catch (Exception error)
            {
                _logger.LogError(error, "WL_UPLOAD_CLEANUP_PENDING {Key}: reconciliador tentará novamente", file.Key);
            }
            throw;
        }
        try { await _storage.CommitAsync(file.Key, ct); }
        catch (Exception error)
        {
            // Blob e referência já estão gravados; o reconciliador confirma a referência antes de qualquer limpeza.
            _logger.LogWarning(error, "WL_UPLOAD_RECONCILE_PENDING {Key}", file.Key);
        }
    }
}

public sealed class WlUploadReconciler : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<WlUploadReconciler> _logger;
    public WlUploadReconciler(IServiceScopeFactory scopes, ILogger<WlUploadReconciler> logger) => (_scopes, _logger) = (scopes, logger);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var storage = scope.ServiceProvider.GetRequiredService<IWlUploadStorage>();
                var references = scope.ServiceProvider.GetRequiredService<WlUploadReferences>();
                // Margem maior que o timeout da requisição/SQL; uploads em andamento não entram na limpeza.
                foreach (var key in await storage.PendingAsync(DateTimeOffset.UtcNow.AddHours(-24), stoppingToken))
                {
                    if (await references.ExistsAsync(key, stoppingToken)) await storage.CommitAsync(key, stoppingToken);
                    else await storage.DeleteAsync(key, stoppingToken);
                    _logger.LogInformation("WL_UPLOAD_RECONCILED {Key}", key);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) { _logger.LogError(error, "WL_UPLOAD_RECONCILE_FAILED; nova tentativa em 15 minutos"); }
        }
    }
}
