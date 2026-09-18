using System;
using System.Data.Entity;
using System.Linq;
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
    /// Campanhas — módulo CONSULTIVO (VEI-RD-51). Somente leitura.
    /// </summary>
    /// <remarks>
    /// <para><b>Nenhum verbo de mutação existe aqui</b> (PRD §5.7/§6.4/§9). Não é que
    /// as rotas de escrita estejam desabilitadas: elas não são declaradas, então um
    /// POST/PUT/PATCH/DELETE nesta rota responde 405 pelo roteador, sem depender de
    /// nenhuma checagem que alguém pudesse afrouxar depois. O design confirma a mesma
    /// decisão de forma independente: o frame 154:5942 traz o badge "Módulo Consultivo"
    /// e não desenha um só botão de mutação.</para>
    ///
    /// <para><b>Visibilidade sem migration.</b> <c>Campanha</c> não tem
    /// <c>IdAfiliada</c>. A cadeia <c>Campanha → Pedido → PedidoReserva|PedidoInsercao</c>
    /// está em <see cref="ITenantQueries.Campanhas"/>, para que nenhum endpoint precise
    /// reescrevê-la — e portanto nenhum possa errá-la.</para>
    /// </remarks>
    [ApiController]
    [Route("api/wl/campanhas")]
    [Authorize(Policy = AuthorizationSetup.ClienteGerenciar)]
    public class CampanhasController : ControllerBase
    {
        private readonly ITenantQueries _tenant;

        public CampanhasController(ITenantQueries tenant) => _tenant = tenant;

        /// <summary>
        /// Listagem paginada. Contagem de peças e valor total são agregados EM SQL —
        /// invariante #5: nada é somado no navegador.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetAll(
            [FromQuery] string status,
            [FromQuery] int? anuncianteId,
            [FromQuery] int? agenciaId,
            [FromQuery] int? periodoId,
            [FromQuery] string nome,
            [FromQuery] WlPaginaRequest pagina)
        {
            var query = _tenant.Campanhas.AsQueryable();

            if (Enum.TryParse<StatusCampanhaEnum>(status, ignoreCase: true, out var statusFiltro))
                query = query.Where(c => c.Status == statusFiltro);

            if (anuncianteId.HasValue)
                query = query.Where(c => c.IdCliente == anuncianteId.Value);

            if (agenciaId.HasValue)
                query = query.Where(c => c.IdAgencia == agenciaId.Value);

            if (periodoId.HasValue)
                query = query.Where(c => c.Pedidos.Any(p => p.IdPeriodo == periodoId.Value));

            if (!string.IsNullOrWhiteSpace(nome))
            {
                var termo = nome.Trim();
                query = query.Where(c =>
                    c.Nome.Contains(termo) ||
                    c.Codigo.Contains(termo) ||
                    c.Cliente.Nome.Contains(termo) ||
                    c.Agencia.Nome.Contains(termo));
            }

            var (page, pageSize) = WlPaginacao.Normalizar(pagina);
            var total = await query.CountAsync();
            var afiliadaId = _tenant.AfiliadaId;

            var itens = await query
                .OrderByDescending(c => c.DataInicioPrevisto)
                .ThenBy(c => c.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(c => new
                {
                    c.Id,
                    c.Codigo,
                    c.Nome,
                    c.Status,
                    c.DataInicioPrevisto,
                    c.DataFimPrevisto,
                    Anunciante = c.Cliente.Nome,
                    // Invariante #12: agência vazia é "Venda Direta (Sem Agência)", nunca
                    // travessão. A agência-espelho é registro real (VEI-RD-79 task b), então
                    // o normal é vir preenchido; o fallback cobre a base ainda não provisionada.
                    Agencia = c.Agencia == null ? null : c.Agencia.Nome,
                    // Rótulo E intervalo: o Figma mostra "Bissemana 16 — 2026 (03/08/2026 -
                    // 16/08/2026)". Só o rótulo não diz quando; só o intervalo não diz qual.
                    //
                    // Vêm os campos crus, não o rótulo montado: Periodo.Nome é um getter
                    // calculado em C# (RetornaNome), que o EF não sabe traduzir para SQL —
                    // usá-lo aqui quebraria a projeção. A montagem do texto fica na tela,
                    // que é onde formatação de data pertence. O que NÃO pode sair do
                    // servidor são os agregados (peças, valor), e esses saem.
                    Periodo = c.Pedidos
                        .OrderBy(p => p.Periodo.DataInicio)
                        .Select(p => new
                        {
                            p.Periodo.Id,
                            p.Periodo.Codigo,
                            Periodicidade = p.Periodo.Periodicidade.Tipo,
                            p.Periodo.DataInicio,
                            p.Periodo.DataFim
                        })
                        .FirstOrDefault(),
                    Pecas = c.Pedidos
                        .Where(p => p.PedidosReserva.Any(r => r.IdAfiliada == afiliadaId)
                                 || p.PedidosInsercao.Any(i => i.IdAfiliada == afiliadaId))
                        .SelectMany(p => p.Itens).Count(),
                    // ValorBruto: valor de mídia da campanha antes de descontos e comissões.
                    // PENDÊNCIA a confirmar com o Humano/design: o Figma diz só "Valor Total"
                    // (R$ 44.000) e o domínio tem seis valores possíveis por item (Tabela,
                    // Bruto, LiquidoVeiculacao, LiquidoAnunciante...). Bruto é o que casa com
                    // a leitura comercial de "total da campanha"; se a régua for outra, muda
                    // aqui, num lugar só.
                    ValorTotal = (decimal?)c.Pedidos
                        .Where(p => p.PedidosReserva.Any(r => r.IdAfiliada == afiliadaId)
                                 || p.PedidosInsercao.Any(i => i.IdAfiliada == afiliadaId))
                        .SelectMany(p => p.Itens).Sum(i => (decimal?)i.ValorBruto) ?? 0m
                })
                .ToListAsync();

            return Ok(WlPaginacao.Montar(itens, page, pageSize, total));
        }

        /// <summary>
        /// Detalhe consultivo. Campanha fora do tenant devolve <b>404, não 403</b>:
        /// um 403 confirmaria que a campanha existe, o que já é informação comercial.
        /// </summary>
        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(int id)
        {
            var afiliadaId = _tenant.AfiliadaId;

            var campanha = await _tenant.Campanhas
                .Where(c => c.Id == id)
                .Select(c => new
                {
                    c.Id,
                    c.Codigo,
                    c.Nome,
                    c.Produto,
                    c.Job,
                    c.Status,
                    c.DataInicioPrevisto,
                    c.DataFimPrevisto,
                    c.Verba,
                    Anunciante = c.Cliente.Nome,
                    Agencia = c.Agencia == null ? null : c.Agencia.Nome,
                    Pedidos = c.Pedidos
                        .Where(p => p.PedidosReserva.Any(r => r.IdAfiliada == afiliadaId)
                                 || p.PedidosInsercao.Any(i => i.IdAfiliada == afiliadaId))
                        .Select(p => new
                        {
                            p.Id,
                            p.Codigo,
                            p.Status,
                            p.StatusPagamento,
                            Periodo = new
                            {
                                p.Periodo.Id,
                                p.Periodo.Codigo,
                                Periodicidade = p.Periodo.Periodicidade.Tipo,
                                p.Periodo.DataInicio,
                                p.Periodo.DataFim
                            },
                            Pecas = p.Itens.Count(),
                            Valor = (decimal?)p.Itens.Sum(i => (decimal?)i.ValorBruto) ?? 0m
                        })
                        .ToList()
                })
                .FirstOrDefaultAsync();

            if (campanha == null)
                return NotFound(new { message = "Campanha não encontrada." });

            return Ok(campanha);
        }
    }
}
