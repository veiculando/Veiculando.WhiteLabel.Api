using System;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Enums;
using Veiculando.Domain.Repositories;
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
        private readonly ILocalRepository _localRepository;

        public DashboardController(
            VeiculandoDataContext db,
            ITenantContext tenantContext,
            ILocalRepository localRepository)
        {
            _db = db;
            _tenantContext = tenantContext;
            _localRepository = localRepository;
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

            // Reusa a contagem do core (TP-R2, Tarefa 7) em vez de duplicar a regra
            // aqui. Só foi possível depois de corrigir o método: a versão anterior
            // filtrava StatusExibicao == Ativo — não AprovacaoPendente — e não
            // recebia afiliadaId, contando as 221 afiliadas juntas. A sobrecarga
            // usada abaixo recorta por afiliada e por origem.
            var alertasAprovaçãoPendente = _localRepository.CountAprovacaoPendente(
                afiliadaId, FonteOrigemEnum.WhiteLabel);

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
