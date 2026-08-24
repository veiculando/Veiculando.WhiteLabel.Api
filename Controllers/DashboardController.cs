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
        private readonly ITenantQueries _tenant;
        private readonly ILocalRepository _localRepository;

        public DashboardController(
            VeiculandoDataContext db,
            ITenantQueries tenant,
            ILocalRepository localRepository)
        {
            _db = db;
            _tenant = tenant;
            _localRepository = localRepository;
        }

        /// <summary>
        /// Obtém KPIs operacionais e alertas do painel Exibidora WL (TP-1).
        /// </summary>
        [HttpGet("kpis")]
        public async Task<IActionResult> GetKpis()
        {
            var afiliadaId = _tenant.AfiliadaId;

            // AsNoTracking: todas estas consultas são read-only; evita tracking
            // desnecessário no ChangeTracker do EF e reduz uso de memória.
            var locaisAtivos = await _tenant.Locais
                .AsNoTracking()
                .CountAsync(l => l.StatusExibicao == StatusExibicaoEnum.Ativo);

            var pecasEmExibicao = await _tenant.Pecas
                .AsNoTracking()
                .CountAsync(p => p.StatusExibicao == StatusExibicaoEnum.Ativo);

            var pedidosPendentes = await _tenant.PedidosReserva
                .AsNoTracking()
                .CountAsync(pr => pr.Status == StatusPedidoReservaEnum.Solicitado);

            // Reusa a contagem do core (TP-R2, Tarefa 7) em vez de duplicar a regra
            // aqui. Só foi possível depois de corrigir o método: a versão anterior
            // filtrava StatusExibicao == Ativo — não AprovacaoPendente — e não
            // recebia afiliadaId, contando as 221 afiliadas juntas. A sobrecarga
            // usada abaixo recorta por afiliada e por origem.
            var alertasAprovaçãoPendente = _localRepository.CountAprovacaoPendente(
                afiliadaId, FonteOrigemEnum.WhiteLabel);

            // ReceitaMensal foi removida do payload (TP-B, seção 2): o valor era
            // sempre 0m fixo, e o PRD vigente proíbe apresentar zero como dado
            // real — o operador lia "R$ 0,00" como "nenhuma receita neste mês",
            // não como "ainda não medimos isso". Não há regra financeira aprovada
            // para calcular o valor de verdade; reintroduzir o campo exige essa
            // regra primeiro, não um novo placeholder.
            return Ok(new
            {
                LocaisAtivos = locaisAtivos,
                PecasEmExibicao = pecasEmExibicao,
                PedidosPendentes = pedidosPendentes,
                AlertasAprovaçãoPendente = alertasAprovaçãoPendente
            });
        }
    }
}
