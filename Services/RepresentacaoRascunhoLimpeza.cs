using System;
using System.Data.Entity;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities.WhiteLabel;
using Veiculando.Domain.Enums;

namespace Veiculando.WhiteLabel.Api.Services;

/// <summary>Cliente do Core que nasceu do pedido de representação da agência (VEI-RD-67h, VEI-RD-167).</summary>
public static class RepresentacaoClienteCore
{
    /// <summary>
    /// O cliente nasceu deste pedido e não é usado em mais nada: nenhum outro vínculo ativo,
    /// nenhum contrato ativo com agência, nenhuma campanha, nenhuma carteira de vendedora e nenhum
    /// outro pedido de representação em aberto. Só esse cliente pode ser inativado pelo pedido.
    /// </summary>
    public static async Task<bool> SoDoPedidoAsync(VeiculandoDataContext db, WlRepresentacao r, CancellationToken ct)
    {
        if (r.Origem != WlCarteiraOrigem.Cadastro) return false;
        var id = r.ClienteId;
        return !await db.AfiliadaClientes.AnyAsync(v => v.IdCliente == id && v.IdAfiliada != r.AfiliadaId &&
                   v.Status == StatusVinculoEnum.Ativo, ct)
            && !await db.AgenciaClientes.AnyAsync(l => l.IdCliente == id && l.StatusExibicao == StatusExibicaoEnum.Ativo, ct)
            && !await db.Campanhas.AnyAsync(c => c.IdCliente == id, ct)
            && !await db.WlCarteiraClientes.AnyAsync(c => c.ClienteId == id && c.Status != WlCarteiraStatus.Reprovado, ct)
            && !await db.WlRepresentacoes.AnyAsync(o => o.ClienteId == id && o.Id != r.Id && o.Status != WlCarteiraStatus.Reprovado, ct);
    }
}

/// <summary>
/// VEI-RD-167: pedido de representação de cliente novo que a agência nunca enviou (ou com ajustes
/// pedidos que ela abandonou) não deixa Cliente ativo no Core. Mesmo desenho da carteira
/// (<see cref="CarteiraRascunhoLimpeza"/>, VEI-RD-161).
/// </summary>
/// <remarks>O vínculo com a exibidora já nasce inativo; aqui só o Cliente é inativado. Se a agência
/// retomar e a exibidora aprovar, a aprovação reativa o cliente (mesma regra da reprovação).</remarks>
public sealed class RepresentacaoRascunhoLimpeza
{
    public const string ChaveDias = "Representacao:RascunhoInativarAposDias";
    public const int DiasPadrao = 7;

    private readonly int _dias;
    private readonly ILogger<RepresentacaoRascunhoLimpeza> _logger;

    public RepresentacaoRascunhoLimpeza(IConfiguration configuration, ILogger<RepresentacaoRascunhoLimpeza> logger)
    {
        var dias = configuration.GetValue(ChaveDias, DiasPadrao);
        _dias = dias > 0 ? dias : DiasPadrao;
        _logger = logger;
    }

    /// <summary>Inativa os Clientes de pedidos parados; devolve quantos inativou.</summary>
    public async Task<int> ExecutarAsync(DateTime agoraUtc, CancellationToken ct)
    {
        var limite = agoraUtc.AddDays(-_dias);
        using var db = new VeiculandoDataContext();
        var candidatas = await db.WlRepresentacoes.AsNoTracking()
            .Where(r => (r.Status == WlCarteiraStatus.Rascunho || r.Status == WlCarteiraStatus.AjustesSolicitados) &&
                r.Origem == WlCarteiraOrigem.Cadastro && r.AtualizadoEm < limite)
            .Join(db.Clientes.Where(x => x.StatusExibicao == StatusExibicaoEnum.Ativo), r => r.ClienteId, x => x.Id, (r, x) => r.Id)
            .ToListAsync(ct);

        var inativados = 0;
        foreach (var id in candidatas)
        {
            using var transaction = db.Database.BeginTransaction();
            // Relê dentro da transação: a agência pode ter enviado nesse meio-tempo.
            var representacao = await db.WlRepresentacoes.AsNoTracking().SingleAsync(r => r.Id == id, ct);
            if ((representacao.Status != WlCarteiraStatus.Rascunho && representacao.Status != WlCarteiraStatus.AjustesSolicitados) ||
                representacao.AtualizadoEm >= limite ||
                !await RepresentacaoClienteCore.SoDoPedidoAsync(db, representacao, ct))
                continue;
            await CarteiraClienteCore.AlterarStatusAsync(db, representacao.ClienteId, StatusExibicaoEnum.Inativo, ct);
            transaction.Commit();
            inativados++;
            _logger.LogInformation("WL_REPRESENTACAO_RASCUNHO_INATIVADO {Representacao} {Cliente}", representacao.Id, representacao.ClienteId);
        }
        return inativados;
    }
}
