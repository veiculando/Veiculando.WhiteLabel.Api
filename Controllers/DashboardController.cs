using System;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Enums;
using Veiculando.WhiteLabel.Api.Middleware;

namespace Veiculando.WhiteLabel.Api.Controllers
{
    [ApiController]
    [Route("api/wl/[controller]")]
    [Authorize]
    public class DashboardController : ControllerBase
    {
        private readonly VeiculandoDataContext _db;
        private readonly ITenantContext _tenantContext;

        public DashboardController(VeiculandoDataContext db, ITenantContext tenantContext)
        {
            _db = db;
            _tenantContext = tenantContext;
        }

        /// <summary>
        /// Obtém KPIs operacionais e alertas do painel Exibidora WL (TP-1).
        /// </summary>
        [HttpGet("kpis")]
        public async Task<IActionResult> GetKpis()
        {
            var afiliadaId = _tenantContext.AfiliadaId;

            // AsNoTracking: todas estas consultas são read-only; evita tracking
            // desnecessário no ChangeTracker do EF e reduz uso de memória.
            var locaisAtivos = await _db.Locais
                .AsNoTracking()
                .CountAsync(l => l.IdAfiliada == afiliadaId && l.StatusExibicao == StatusExibicaoEnum.Ativo);

            var pecasEmExibicao = await _db.Pecas
                .AsNoTracking()
                .CountAsync(p => p.Local.IdAfiliada == afiliadaId && p.StatusExibicao == StatusExibicaoEnum.Ativo);

            var pedidosPendentes = await _db.PedidosReserva
                .AsNoTracking()
                .CountAsync(pr => pr.IdAfiliada == afiliadaId && pr.Status == StatusPedidoReservaEnum.Solicitado);

            // ⚠️ NÃO trocar por ILocalRepository.CountAprovacaoPendente().
            //
            // A Tarefa 7 do TP-R2 pedia esse reuso, mas o método do core está
            // errado para este fim, em dois pontos (LocalRepository.cs:465):
            //   - filtra StatusExibicao == Ativo, e não AprovacaoPendente;
            //   - não recebe afiliadaId, contando as 221 afiliadas juntas.
            //
            // Reusá-lo transformaria o alerta "locais aguardando aprovação" na
            // contagem global de locais ativos. A consulta abaixo é a correta;
            // o método do core (e o endpoint GET /api/local/count-aprovacao-pendente
            // que o expõe) precisa de correção própria, fora desta sprint.
            var alertasAprovaçãoPendente = await _db.Locais
                .AsNoTracking()
                .CountAsync(l => l.IdAfiliada == afiliadaId
                              && l.FonteOrigem == FonteOrigemEnum.WhiteLabel
                              && l.StatusExibicao == StatusExibicaoEnum.AprovacaoPendente);

            return Ok(new
            {
                LocaisAtivos = locaisAtivos,
                PecasEmExibicao = pecasEmExibicao,
                PedidosPendentes = pedidosPendentes,
                ReceitaMensal = 0m, // Receita mockada na V1 conforme TP-1
                AlertasAprovaçãoPendente = alertasAprovaçãoPendente
            });
        }
    }
}
