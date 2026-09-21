using System;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Enums;
using Veiculando.Domain.Repositories;
using Veiculando.WhiteLabel.Api.Configurations;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;

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
        private readonly IReceitaService _receita;

        public DashboardController(
            VeiculandoDataContext db,
            ITenantQueries tenant,
            ILocalRepository localRepository,
            IReceitaService receita)
        {
            _db = db;
            _tenant = tenant;
            _localRepository = localRepository;
            _receita = receita;
        }

        /// <summary>
        /// Obtém KPIs operacionais e alertas do painel Exibidora WL (TP-1).
        /// </summary>
        [HttpGet("kpis")]
        public async Task<IActionResult> GetKpis()
        {
            var afiliadaId = _tenant.AfiliadaId;

            var locaisAtivos = await _tenant.Locais
                .AsNoTracking()
                .CountAsync(l => l.StatusExibicao == StatusExibicaoEnum.Ativo);

            var pecasEmExibicao = await _tenant.Pecas
                .AsNoTracking()
                .CountAsync(p => p.StatusExibicao == StatusExibicaoEnum.Ativo);

            var pedidosPendentes = await _tenant.PedidosReserva
                .AsNoTracking()
                .CountAsync(pr => pr.Status == StatusPedidoReservaEnum.Solicitado);

            var alertasAprovaçãoPendente = _localRepository.CountAprovacaoPendente(
                afiliadaId, FonteOrigemEnum.WhiteLabel);

            return Ok(new
            {
                LocaisAtivos = locaisAtivos,
                PecasEmExibicao = pecasEmExibicao,
                PedidosPendentes = pedidosPendentes,
                AlertasAprovaçãoPendente = alertasAprovaçãoPendente
            });
        }

        /// <summary>
        /// KPIs financeiros e operacionais do Dashboard (VEI-RD-85, Figma 153:1265),
        /// escopados a um <c>Periodo.Id</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>Faturamento Previsto — indisponível de propósito.</b> Não existe
        /// regra financeira aprovada para este número (nem para o comparativo "vs
        /// ciclo anterior"), e o PRD vigente proíbe apresentar zero como se fosse
        /// dado real (ver comentário histórico em <see cref="GetKpis"/> sobre
        /// <c>ReceitaMensal</c>). Pendência do Humano (Plano Tático Ordem 5, seção
        /// 6) — <c>Disponivel = false</c> é a resposta honesta até a fórmula e a
        /// fonte de receita serem definidas; não inventar um cálculo aqui.</para>
        ///
        /// <para><b>Taxa de Ocupação OOH e Demandas Pendentes — fórmulas
        /// provisórias.</b> Também pendências do Humano, mas sem risco de exibir
        /// dinheiro fictício: implementadas com a leitura mais direta dos dados
        /// existentes (candidatas citadas no próprio plano tático), marcadas aqui
        /// e no plano de testes como sujeitas a confirmação — não como
        /// definitivas.</para>
        /// </remarks>
        [HttpGet("kpis-financeiro")]
        [Authorize(Policy = AuthorizationSetup.FinanceiroVisualizar)]
        public async Task<IActionResult> GetKpisFinanceiro([FromQuery] int? periodoId)
        {
            var periodo = await ResolverPeriodoAsync(periodoId);
            if (periodo == null)
                return NotFound(new { message = "Nenhum período comercial ativo foi encontrado." });

            var hoje = DateTime.Today;
            var periodoVigente = periodo.DataInicio.Date <= hoje && hoje <= periodo.DataFim.Date;

            // Ocupação e Campanhas Ativas reaproveitam a grade de disponibilidade
            // (PecaPeriodoStatus) que a Programação já usa — não é uma tabela nova
            // nem um cálculo novo, só um agregado sobre ela. Ausência de linha
            // para o par peça/período significa "disponível"; qualquer status
            // além de Indisponivel representa algum compromisso comercial.
            var gradeDoPeriodo = _tenant.PecaPeriodoStatus
                .AsNoTracking()
                .Where(pps => pps.IdPeriodo == periodo.Id && pps.Status != StatusPecaPeriodoEnum.Indisponivel);

            var pecasOcupadas = await gradeDoPeriodo
                .Select(pps => pps.IdPeca)
                .Distinct()
                .CountAsync();

            var pecasAtivas = await _tenant.Pecas
                .AsNoTracking()
                .CountAsync(p => p.StatusExibicao == StatusExibicaoEnum.Ativo);

            var campanhasAtivas = await gradeDoPeriodo
                .Where(pps => pps.Pedido.Campanha.Status != StatusCampanhaEnum.Cancelada)
                .Select(pps => pps.Pedido.Campanha.Id)
                .Distinct()
                .CountAsync();

            var reservasPendentes = await _tenant.PedidosReserva
                .AsNoTracking()
                .CountAsync(pr => pr.Status == StatusPedidoReservaEnum.Solicitado);

            var pisPendentes = await _tenant.PedidosInsercao
                .AsNoTracking()
                .CountAsync(pi => pi.Status == StatusPedidoInsercaoEnum.Novo);

            var faturamentoPrevisto = await _receita.ObterFaturamentoPrevistoAsync(periodo.Id, _tenant);

            return Ok(new
            {
                Periodo = new
                {
                    periodo.Id,
                    periodo.Nome,
                    periodo.DataInicio,
                    periodo.DataFim,
                    Vigente = periodoVigente
                },
                FaturamentoPrevisto = faturamentoPrevisto,
                TaxaOcupacaoOOH = new
                {
                    Disponivel = true,
                    FormulaProvisoria = true,
                    Percentual = pecasAtivas > 0 ? Math.Round(100m * pecasOcupadas / pecasAtivas, 1) : 0m,
                    PecasOcupadas = pecasOcupadas,
                    PecasAtivas = pecasAtivas
                },
                CampanhasAtivas = campanhasAtivas,
                DemandasPendentes = new
                {
                    FormulaProvisoria = true,
                    Reservas = reservasPendentes,
                    PedidosInsercao = pisPendentes
                }
            });
        }

        /// <summary>Solicitações de reserva com item no período informado.</summary>
        [HttpGet("reservas")]
        [Authorize(Policy = AuthorizationSetup.FinanceiroVisualizar)]
        public async Task<IActionResult> GetReservasDoPeriodo([FromQuery] int periodoId, [FromQuery] int limite = 10)
        {
            limite = limite is < 1 or > 50 ? 10 : limite;

            var brutos = await _tenant.PedidosReserva
                .AsNoTracking()
                .Where(pr => pr.Itens.Any(i => i.PedidoItem.IdPeriodo == periodoId))
                .OrderByDescending(pr => pr.DataCadastro)
                .ThenBy(pr => pr.Id)
                .Take(limite)
                .Select(pr => new
                {
                    pr.Id,
                    pr.Codigo,
                    pr.Status,
                    Agencia = pr.Pedido.Campanha.Agencia != null ? pr.Pedido.Campanha.Agencia.Nome : null,
                    Cliente = pr.Pedido.Campanha.Cliente != null ? pr.Pedido.Campanha.Cliente.Nome : null,
                    Campanha = pr.Pedido.Campanha.Nome,
                    ItensCount = pr.Itens.Count,
                    pr.ValorTotalBruto
                })
                .ToListAsync();

            var reservas = brutos
                .Select(pr => new
                {
                    pr.Id,
                    pr.Codigo,
                    Status = pr.Status.ToString(),
                    pr.Agencia,
                    pr.Cliente,
                    pr.Campanha,
                    pr.ItensCount,
                    pr.ValorTotalBruto
                })
                .ToList();

            return Ok(reservas);
        }

        /// <summary>Pedidos de Inserção com item no período informado.</summary>
        [HttpGet("pedidos-insercao")]
        [Authorize(Policy = AuthorizationSetup.FinanceiroVisualizar)]
        public async Task<IActionResult> GetPedidosInsercaoDoPeriodo([FromQuery] int periodoId, [FromQuery] int limite = 10)
        {
            limite = limite is < 1 or > 50 ? 10 : limite;

            var brutos = await _tenant.PedidosInsercao
                .AsNoTracking()
                .Where(pi => pi.StatusExibicao == StatusExibicaoEnum.Ativo
                          && pi.Itens.Any(i => i.PedidoItem.IdPeriodo == periodoId))
                .OrderByDescending(pi => pi.DataCadastro)
                .ThenBy(pi => pi.Id)
                .Take(limite)
                .Select(pi => new
                {
                    pi.Id,
                    pi.Codigo,
                    pi.Status,
                    Anunciante = pi.Pedido.Campanha.Cliente != null ? pi.Pedido.Campanha.Cliente.Nome : null,
                    Cidade = pi.Itens
                        .Where(i => i.PedidoItem.IdPeriodo == periodoId)
                        .Select(i => i.PedidoItem.Peca.Local.Cidade.Nome)
                        .FirstOrDefault(),
                    NumeroDePecas = pi.Itens.Count,
                    pi.ValorLiquidoVeiculacao
                })
                .ToListAsync();

            var pis = brutos
                .Select(pi => new
                {
                    pi.Id,
                    pi.Codigo,
                    Status = pi.Status.ToString(),
                    pi.Anunciante,
                    pi.Cidade,
                    pi.NumeroDePecas,
                    pi.ValorLiquidoVeiculacao
                })
                .ToList();

            return Ok(pis);
        }

        /// <summary>
        /// Resolve o período comercial: o informado (se válido e bissemanal), senão
        /// o vigente agora, senão o próximo bissemanal ativo.
        /// </summary>
        private async Task<Veiculando.Domain.Entities.Periodo> ResolverPeriodoAsync(int? periodoId)
        {
            if (periodoId.HasValue)
            {
                var informado = await _db.Periodos
                    .AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Id == periodoId.Value
                        && p.StatusExibicao == StatusExibicaoEnum.Ativo);

                if (informado != null)
                    return informado;
            }

            var hoje = DateTime.Today;

            var ativos = await _db.Periodos
                .AsNoTracking()
                .Where(p => p.StatusExibicao == StatusExibicaoEnum.Ativo
                         && p.Periodicidade.Tipo == PeriodicidadeEnum.Bissemanal)
                .OrderBy(p => p.DataInicio)
                .ToListAsync();

            return ativos.FirstOrDefault(p => p.DataInicio.Date <= hoje && hoje <= p.DataFim.Date)
                ?? ativos.FirstOrDefault(p => p.DataInicio.Date > hoje)
                ?? ativos.LastOrDefault();
        }
    }
}
