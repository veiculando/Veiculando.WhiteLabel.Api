using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities.WhiteLabel;
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
        private readonly VeiculandoDataContext _db;

        public RelatoriosController(ITenantQueries tenant, IReceitaService receita, VeiculandoDataContext db)
        {
            _tenant = tenant;
            _receita = receita;
            _db = db;
        }

        /// <summary>
        /// Período desconhecido é 404, não um resumo zerado: zeros para um Id que
        /// não existe seriam lidos como "não faturou nada" (R$ 0,00 falso).
        /// </summary>
        private Task<bool> PeriodoExisteAsync(int periodoId) =>
            _db.Periodos.AsNoTracking().AnyAsync(p => p.Id == periodoId && p.StatusExibicao == StatusExibicaoEnum.Ativo);

        private static IActionResult PeriodoNaoEncontrado() =>
            new NotFoundObjectResult(new { message = "Período não encontrado." });

        /// <summary>Resumo financeiro e operacional do período, agregado no servidor.</summary>
        [HttpGet("resumo")]
        public async Task<IActionResult> GetResumo([FromQuery] int periodoId)
        {
            if (!await PeriodoExisteAsync(periodoId))
                return PeriodoNaoEncontrado();

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
            if (!await PeriodoExisteAsync(periodoId))
                return PeriodoNaoEncontrado();

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

        /// <summary>
        /// Pedidos fechados na Prospecção, por vendedora (VEI-RD-157f).
        /// </summary>
        /// <remarks>
        /// <para>A base é o evento <c>PedidoCriado</c> da Prospecção, que guarda o operador da
        /// sessão e os códigos dos pedidos — e não <c>Pedido.FonteUsuarioId</c>, que fora da
        /// Prospecção guarda o anunciante (peer review I2). O pedido fica com quem vendeu:
        /// trocar a responsável pela carteira depois não o move.</para>
        /// <para>Nomes de coluna estáveis: o arquivo deve servir de base para o SAP.</para>
        /// </remarks>
        [HttpGet("pedidos-por-vendedora")]
        public async Task<IActionResult> PedidosPorVendedora([FromQuery] DateTime? de, [FromQuery] DateTime? ate,
            [FromQuery] int? vendedorId)
        {
            var (linhas, erro) = await LinhasPorVendedoraAsync(de, ate, vendedorId);
            if (erro != null) return BadRequest(new { message = erro });
            return Ok(new
            {
                de = Inicio(de), ate = Fim(ate),
                totalPedidos = linhas.Count,
                valorBrutoTotal = linhas.Sum(l => l.ValorBruto),
                porVendedora = linhas.GroupBy(l => new { l.VendedorId, l.Vendedora })
                    .Select(g => new { g.Key.VendedorId, g.Key.Vendedora, pedidos = g.Count(), valorBruto = g.Sum(l => l.ValorBruto) })
                    .OrderByDescending(g => g.valorBruto),
                pedidos = linhas
            });
        }

        [HttpGet("pedidos-por-vendedora/exportar")]
        [Authorize(Policy = AuthorizationSetup.RelatorioExportar)]
        public async Task<IActionResult> ExportarPedidosPorVendedora([FromQuery] DateTime? de, [FromQuery] DateTime? ate,
            [FromQuery] int? vendedorId)
        {
            var (linhas, erro) = await LinhasPorVendedoraAsync(de, ate, vendedorId);
            if (erro != null) return BadRequest(new { message = erro });
            var csv = new StringBuilder();
            csv.AppendLine("VendedoraId;Vendedora;Pedido;DataPedido;Cliente;CnpjCliente;Campanha;Periodo;ValorBruto;ValorLiquidoAnunciante;Status");
            foreach (var l in linhas)
                csv.AppendLine(string.Join(';',
                    l.VendedorId.ToString(CultureInfo.InvariantCulture), CsvEscape(l.Vendedora), CsvEscape(l.Pedido),
                    l.DataPedido.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), CsvEscape(l.Cliente),
                    l.CnpjCliente, CsvEscape(l.Campanha), CsvEscape(l.Periodo),
                    l.ValorBruto.ToString("F2", CultureInfo.InvariantCulture),
                    l.ValorLiquidoAnunciante.ToString("F2", CultureInfo.InvariantCulture), CsvEscape(l.Status)));
            return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray(), "text/csv",
                $"pedidos-por-vendedora-{Inicio(de):yyyyMMdd}-{Fim(ate):yyyyMMdd}.csv");
        }

        public sealed record LinhaVendedora(int VendedorId, string Vendedora, string Pedido, DateTime DataPedido,
            string Cliente, string CnpjCliente, string Campanha, string Periodo, decimal ValorBruto,
            decimal ValorLiquidoAnunciante, string Status);

        private static DateTime Inicio(DateTime? de) => (de ?? new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1)).Date;
        private static DateTime Fim(DateTime? ate) => (ate ?? DateTime.UtcNow).Date;

        private async Task<(List<LinhaVendedora> Linhas, string Erro)> LinhasPorVendedoraAsync(DateTime? de, DateTime? ate, int? vendedorId)
        {
            var inicio = Inicio(de);
            var fimExclusivo = Fim(ate).AddDays(1);
            if (fimExclusivo <= inicio) return (null, "A data final deve ser igual ou posterior à inicial.");
            if ((fimExclusivo - inicio).TotalDays > 366) return (null, "Escolha um intervalo de até um ano.");

            var eventos = await _tenant.ProspeccaoEventos.AsNoTracking()
                .Where(e => e.Evento == WlProspeccaoEventoTipo.PedidoCriado && e.OcorridoEm >= inicio && e.OcorridoEm < fimExclusivo &&
                    (vendedorId == null || e.OperadorId == vendedorId))
                .Select(e => new { e.OperadorId, e.CodigosPedido })
                .ToListAsync();
            var vendedoraPorPedido = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var e in eventos)
            {
                string[] codigos;
                try { codigos = JsonSerializer.Deserialize<string[]>(e.CodigosPedido ?? "[]") ?? Array.Empty<string>(); }
                catch (JsonException) { continue; }
                foreach (var codigo in codigos.Where(c => !string.IsNullOrWhiteSpace(c))) vendedoraPorPedido[codigo] = e.OperadorId;
            }
            if (vendedoraPorPedido.Count == 0) return (new List<LinhaVendedora>(), null);

            var codigosPedido = vendedoraPorPedido.Keys.ToList();
            var pedidos = await _db.Pedidos.AsNoTracking()
                .Where(p => codigosPedido.Contains(p.Codigo))
                .Select(p => new
                {
                    p.Codigo, p.DataCadastro, p.Status, p.ValorTotalBruto, p.ValorLiquidoAnunciante,
                    Cliente = p.Campanha.Cliente.Nome, Cnpj = p.Campanha.Cliente.Cnpj.Numero,
                    Campanha = p.Campanha.Nome, Periodo = p.Periodo.Codigo
                })
                .ToListAsync();
            var ids = vendedoraPorPedido.Values.Distinct().ToList();
            var emails = await _tenant.Usuarios.Where(u => ids.Contains(u.Id))
                .Select(u => new { u.Id, Email = u.Email.Endereco }).ToDictionaryAsync(u => u.Id, u => u.Email);

            var linhas = pedidos
                .Select(p =>
                {
                    var vendedora = vendedoraPorPedido[p.Codigo];
                    return new LinhaVendedora(vendedora, emails.TryGetValue(vendedora, out var email) ? email : null,
                        p.Codigo, p.DataCadastro, p.Cliente, CarteiraProspeccao.Digitos(p.Cnpj), p.Campanha, p.Periodo,
                        p.ValorTotalBruto, p.ValorLiquidoAnunciante, p.Status.ToString());
                })
                .OrderBy(l => l.Vendedora).ThenBy(l => l.DataPedido)
                .ToList();
            return (linhas, null);
        }

        private static string CsvEscape(string valor)
        {
            if (string.IsNullOrEmpty(valor)) return string.Empty;
            // Célula que começa com = + - @ vira fórmula no Excel; o nome do cliente vem de cadastro feito no App.
            if ("=+-@".IndexOf(valor[0]) >= 0) valor = "'" + valor;
            return valor.Contains(';') || valor.Contains('"')
                ? $"\"{valor.Replace("\"", "\"\"")}\""
                : valor;
        }
    }
}
