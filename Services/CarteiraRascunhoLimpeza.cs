using System;
using System.Data.Entity;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Veiculando.Domain.Entities.WhiteLabel;
using Veiculando.Domain.Enums;
using Veiculando.Data.Contexts;

namespace Veiculando.WhiteLabel.Api.Services;

/// <summary>
/// Status no Core do Cliente que nasceu do cadastro da carteira (VEI-SUP-39, VEI-RD-161).
/// </summary>
public static class CarteiraClienteCore
{
    /// <summary>
    /// O Cliente nasceu do cadastro da vendedora e não é usado em mais nada: nenhum outro
    /// vínculo ativo com exibidora, nenhum contrato ativo com agência e nenhuma campanha.
    /// Só esse cliente pode ser inativado pela carteira.
    /// </summary>
    public static async Task<bool> SoDaCarteiraAsync(VeiculandoDataContext db, WlCarteiraCliente carteira, CancellationToken ct)
    {
        if (carteira.Origem != WlCarteiraOrigem.Cadastro) return false;
        var id = carteira.ClienteId;
        return !await db.AfiliadaClientes.AnyAsync(v => v.IdCliente == id && v.IdAfiliada != carteira.AfiliadaId &&
                   v.Status == StatusVinculoEnum.Ativo, ct)
            && !await db.AgenciaClientes.AnyAsync(l => l.IdCliente == id && l.StatusExibicao == StatusExibicaoEnum.Ativo, ct)
            && !await db.Campanhas.AnyAsync(c => c.IdCliente == id, ct);
    }

    /// <summary>
    /// O Cliente do Core não expõe Inativar/Aprovar (são protegidos em EntityDefBase); o
    /// UPDATE é restrito à coluna de status e roda na transação de quem chama.
    /// </summary>
    public static Task AlterarStatusAsync(VeiculandoDataContext db, int clienteId, StatusExibicaoEnum status, CancellationToken ct) =>
        db.Database.ExecuteSqlCommandAsync(
            "UPDATE dbo.Cliente SET StatusExibicao = @p0, DataAtualizacao = GETDATE() WHERE Id = @p1",
            ct, (int)status, clienteId);
}

/// <summary>
/// VEI-RD-161: rascunho da carteira que a vendedora nunca enviou (ou cadastro com ajustes pedidos
/// que ela abandonou, VEI-SUP-40) não deixa Cliente ativo no Core.
/// O vínculo com a exibidora já nasce inativo; aqui só o Cliente é inativado. A retomada e a
/// aprovação reativam como na reprovação (VEI-SUP-39).
/// </summary>
public sealed class CarteiraRascunhoLimpeza
{
    public const string ChaveDias = "Carteira:RascunhoInativarAposDias";
    public const int DiasPadrao = 7;

    private readonly int _dias;
    private readonly ILogger<CarteiraRascunhoLimpeza> _logger;

    public CarteiraRascunhoLimpeza(IConfiguration configuration, ILogger<CarteiraRascunhoLimpeza> logger)
    {
        var dias = configuration.GetValue(ChaveDias, DiasPadrao);
        _dias = dias > 0 ? dias : DiasPadrao;
        _logger = logger;
    }

    /// <summary>Inativa os Clientes de rascunhos parados; devolve quantos inativou.</summary>
    public async Task<int> ExecutarAsync(DateTime agoraUtc, CancellationToken ct)
    {
        var limite = agoraUtc.AddDays(-_dias);
        using var db = new VeiculandoDataContext();
        var candidatas = await db.WlCarteiraClientes.AsNoTracking()
            .Where(c => (c.Status == WlCarteiraStatus.Rascunho || c.Status == WlCarteiraStatus.AjustesSolicitados) &&
                c.Origem == WlCarteiraOrigem.Cadastro && c.AtualizadoEm < limite)
            .Join(db.Clientes.Where(x => x.StatusExibicao == StatusExibicaoEnum.Ativo), c => c.ClienteId, x => x.Id, (c, x) => c.Id)
            .ToListAsync(ct);

        var inativados = 0;
        foreach (var id in candidatas)
        {
            using var transaction = db.Database.BeginTransaction();
            // Relê dentro da transação: a vendedora pode ter enviado ou outra ter retomado.
            var carteira = await db.WlCarteiraClientes.AsNoTracking().SingleAsync(c => c.Id == id, ct);
            if ((carteira.Status != WlCarteiraStatus.Rascunho && carteira.Status != WlCarteiraStatus.AjustesSolicitados) ||
                carteira.AtualizadoEm >= limite ||
                !await CarteiraClienteCore.SoDaCarteiraAsync(db, carteira, ct))
                continue;
            await CarteiraClienteCore.AlterarStatusAsync(db, carteira.ClienteId, StatusExibicaoEnum.Inativo, ct);
            transaction.Commit();
            inativados++;
            _logger.LogInformation("WL_CARTEIRA_RASCUNHO_INATIVADO {Carteira} {Cliente}", carteira.Id, carteira.ClienteId);
        }
        return inativados;
    }
}

public sealed class CarteiraRascunhoAgendador : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<CarteiraRascunhoAgendador> _logger;
    public CarteiraRascunhoAgendador(IServiceScopeFactory scopes, ILogger<CarteiraRascunhoAgendador> logger) => (_scopes, _logger) = (scopes, logger);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<CarteiraRascunhoLimpeza>().ExecutarAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) { _logger.LogError(error, "WL_CARTEIRA_RASCUNHO_FALHOU; nova tentativa em 6 horas"); }
        }
    }
}
