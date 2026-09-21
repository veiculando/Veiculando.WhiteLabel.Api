using System;
using System.Data.Entity;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Veiculando.Domain.Enums;
using Veiculando.WhiteLabel.Api.Configurations;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;

namespace Veiculando.WhiteLabel.Api.Controllers
{
    /// <summary>
    /// Relatórios financeiros (VEI-RD-92, Figma 286:11255).
    /// </summary>
    /// <remarks>
    /// <para><b>O que este controller NÃO decide sozinho.</b> O Figma retificou
    /// uma tela que o PRD v2.2 nem previa — não há seção correspondente para
    /// validar contra, e duas perguntas ficam em aberto até a revisão v2.3:
    /// exatamente quais agregações o frame mostra (por anunciante? por
    /// ocupação?) e se a exportação é PDF ou CSV. Enquanto isso, <c>/resumo</c>
    /// expõe as agregações inequívocas dos dados que já existem (contagem por
    /// status de PI e de reserva, peças veiculadas) e <c>/exportar</c> usa CSV —
    /// o formato mais simples de defender sem confirmação — deixando as duas
    /// perguntas explícitas no plano de testes em vez de resolvidas por
    /// suposição.</para>
    ///
    /// <para><b>O que este controller genuinamente não pode inventar.</b> A
    /// receita ("Faturamento Previsto") vem de <see cref="IReceitaService"/>, a
    /// MESMA fonte que o KPI do Dashboard usa — ver o comentário em
    /// <c>ReceitaService</c>. Enquanto a fórmula não for definida pelo Humano,
    /// as duas telas mostram o mesmo "indisponível" em vez de dois números
    /// diferentes para o mesmo faturamento.</para>
    /// </remarks>
    [ApiController]
    [Route("api/wl/relatorios")]
    [Authorize(Policy = AuthorizationSetup.FinanceiroVisualizar)]
    public class RelatoriosController : ControllerBase
    {
        private readonly ITenantQueries _tenant;
        private readonly IReceitaService _receita;

        public RelatoriosController(ITenantQueries tenant, IReceitaService receita)
        {
            _tenant = tenant;
            _receita = receita;
        }

        /// <summary>Resumo financeiro e operacional do período, agregado no servidor.</summary>
        [HttpGet("resumo")]
        public async Task<IActionResult> GetResumo([FromQuery] int periodoId)
        {
            var pisDoPeriodo = _tenant.PedidosInsercao
                .AsNoTracking()
                .Where(pi => pi.StatusExibicao == StatusExibicaoEnum.Ativo
                          && pi.Itens.Any(i => i.PedidoItem.IdPeriodo == periodoId));

            var pisPorStatusBrutos = await pisDoPeriodo
                .GroupBy(pi => pi.Status)
                .Select(g => new { Status = g.Key, Quantidade = g.Count(), ValorLiquido = g.Sum(pi => pi.ValorLiquidoVeiculacao) })
                .ToListAsync();

            var pisPorStatus = pisPorStatusBrutos
                .Select(g => new { Status = g.Status.ToString(), g.Quantidade, g.ValorLiquido })
                .ToList();

            var reservasDoPeriodo = _tenant.PedidosReserva
                .AsNoTracking()
                .Where(pr => pr.Itens.Any(i => i.PedidoItem.IdPeriodo == periodoId));

            var reservasPorStatusBrutos = await reservasDoPeriodo
                .GroupBy(pr => pr.Status)
                .Select(g => new { Status = g.Key, Quantidade = g.Count() })
                .ToListAsync();

            var reservasPorStatus = reservasPorStatusBrutos
                .Select(g => new { Status = g.Status.ToString(), g.Quantidade })
                .ToList();

            var pecasVeiculadas = await _tenant.PecaPeriodoStatus
                .AsNoTracking()
                .CountAsync(pps => pps.IdPeriodo == periodoId && pps.Status == StatusPecaPeriodoEnum.Faturado);

            var faturamentoPrevisto = await _receita.ObterFaturamentoPrevistoAsync(periodoId, _tenant);

            return Ok(new
            {
                PeriodoId = periodoId,
                FaturamentoPrevisto = faturamentoPrevisto,
                ValorLiquidoVeiculacaoTotal = pisPorStatusBrutos.Sum(g => g.ValorLiquido),
                PedidosInsercao = new
                {
                    Total = pisPorStatusBrutos.Sum(g => g.Quantidade),
                    PorStatus = pisPorStatus
                },
                Reservas = new
                {
                    Total = reservasPorStatusBrutos.Sum(g => g.Quantidade),
                    PorStatus = reservasPorStatus
                },
                PecasVeiculadas = pecasVeiculadas
            });
        }

        /// <summary>
        /// Exporta o resumo do período em CSV.
        /// </summary>
        /// <remarks>
        /// Formato provisório (pendência: o Figma tem uma ação de download, mas
        /// não confirma PDF ou CSV). Protegido por <c>RelatorioExportar</c>,
        /// distinta de <c>FinanceiroVisualizar</c> — ver quem tem cada uma antes
        /// de assumir que visualizar implica exportar.
        /// </remarks>
        [HttpGet("exportar")]
        [Authorize(Policy = AuthorizationSetup.RelatorioExportar)]
        public async Task<IActionResult> Exportar([FromQuery] int periodoId)
        {
            var pis = await _tenant.PedidosInsercao
                .AsNoTracking()
                .Where(pi => pi.StatusExibicao == StatusExibicaoEnum.Ativo
                          && pi.Itens.Any(i => i.PedidoItem.IdPeriodo == periodoId))
                .Select(pi => new
                {
                    pi.Codigo,
                    pi.DataCadastro,
                    pi.Status,
                    Anunciante = pi.Pedido.Campanha.Cliente != null ? pi.Pedido.Campanha.Cliente.Nome : null,
                    pi.ValorLiquidoVeiculacao
                })
                .ToListAsync();

            var csv = new StringBuilder();
            csv.AppendLine("Codigo;DataCadastro;Status;Anunciante;ValorLiquidoVeiculacao");
            foreach (var pi in pis)
            {
                csv.AppendLine(string.Join(';',
                    CsvEscape(pi.Codigo),
                    pi.DataCadastro.ToString("yyyy-MM-dd"),
                    CsvEscape(pi.Status.ToString()),
                    CsvEscape(pi.Anunciante),
                    pi.ValorLiquidoVeiculacao.ToString("F2")));
            }

            var bytes = Encoding.UTF8.GetBytes(csv.ToString());
            return File(bytes, "text/csv", $"relatorio-financeiro-periodo-{periodoId}.csv");
        }

        private static string CsvEscape(string valor)
        {
            if (string.IsNullOrEmpty(valor)) return string.Empty;
            return valor.Contains(';') || valor.Contains('"')
                ? $"\"{valor.Replace("\"", "\"\"")}\""
                : valor;
        }
    }
}
