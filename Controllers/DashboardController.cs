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

            var locaisAtivos = await _db.Locais
                .CountAsync(l => l.IdAfiliada == afiliadaId && l.StatusExibicao == StatusExibicaoEnum.Ativo);

            var pecasEmExibicao = await _db.Pecas
                .CountAsync(p => p.Local.IdAfiliada == afiliadaId && p.StatusExibicao == StatusExibicaoEnum.Ativo);

            var pedidosPendentes = await _db.PedidosReserva
                .CountAsync(pr => pr.IdAfiliada == afiliadaId && pr.Status == StatusPedidoReservaEnum.Solicitado);

            var alertasAprovaçãoPendente = await _db.Locais
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
