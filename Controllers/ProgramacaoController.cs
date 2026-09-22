using System;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Enums;
using Veiculando.WhiteLabel.Api.Configurations;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;

namespace Veiculando.WhiteLabel.Api.Controllers
{
    [ApiController]
    [Route("api/wl/[controller]")]
    // VEI-RD-93: estava com PecaGerenciar (provável cópia de LocaisController),
    // contradizendo o comentário em app.routes.ts de que /programacao exigia só
    // sessão. A migração de claims concede ProgramacaoVisualizar a todo operador
    // existente, então ninguém perde acesso com esta correção.
    [Authorize(Policy = AuthorizationSetup.ProgramacaoVisualizar)]
    public class ProgramacaoController : ControllerBase
    {
        private readonly VeiculandoDataContext _db;
        private readonly ITenantQueries _tenant;

        public ProgramacaoController(VeiculandoDataContext db, ITenantQueries tenant)
        {
            _db = db;
            _tenant = tenant;
        }

        /// <summary>
        /// Mensagem exata exigida pelo card (VEI-RD-86) quando Período Inicial
        /// vem depois de Período Final. O frontend compara a string, não só o
        /// status code — mudar o texto aqui quebraria essa checagem.
        /// </summary>
        public const string MsgPeriodoInvertido = "Período inicial deve ser anterior ou igual ao período final";

        [HttpPost("listar")]
        public async Task<IActionResult> ListarGrade(
            [FromBody] ProgramacaoFiltroDto dto,
            [FromQuery] WlPaginaRequest pagina)
        {
            var afiliadaId = _tenant.AfiliadaId;

            var query = _tenant.PecaPeriodoStatus;

            if (dto?.IdLocal.HasValue == true && dto.IdLocal.Value > 0)
            {
                query = query.Where(pps => pps.Peca.IdLocal == dto.IdLocal.Value);
            }

            if (dto?.Periodicidade.HasValue == true)
            {
                query = query.Where(pps => pps.Periodo.Periodicidade.Tipo == dto.Periodicidade.Value);
            }

            // Periodo Inicial/Final e um INTERVALO de bi-semanas/meses, nao um
            // periodo unico — troca o antigo IdPeriodo. A validacao de ordem
            // acontece aqui, contra as datas reais (Periodo.Id nao e sequencial
            // por data), antes de qualquer coisa tocar o SQL da grade.
            if (dto?.IdPeriodoInicial.HasValue == true || dto?.IdPeriodoFinal.HasValue == true)
            {
                DateTime? dataInicio = null;
                DateTime? dataFim = null;

                if (dto.IdPeriodoInicial.HasValue)
                {
                    dataInicio = await _db.Periodos
                        .Where(p => p.Id == dto.IdPeriodoInicial.Value)
                        .Select(p => (DateTime?)p.DataInicio)
                        .FirstOrDefaultAsync();

                    if (dataInicio == null)
                        return BadRequest(new { message = "Período inicial não encontrado." });
                }

                if (dto.IdPeriodoFinal.HasValue)
                {
                    dataFim = await _db.Periodos
                        .Where(p => p.Id == dto.IdPeriodoFinal.Value)
                        .Select(p => (DateTime?)p.DataInicio)
                        .FirstOrDefaultAsync();

                    if (dataFim == null)
                        return BadRequest(new { message = "Período final não encontrado." });
                }

                if (dataInicio.HasValue && dataFim.HasValue && dataInicio.Value > dataFim.Value)
                    return BadRequest(new { message = MsgPeriodoInvertido });

                if (dataInicio.HasValue)
                    query = query.Where(pps => pps.Periodo.DataInicio >= dataInicio.Value);

                if (dataFim.HasValue)
                    query = query.Where(pps => pps.Periodo.DataInicio <= dataFim.Value);
            }

            if (dto?.Status.HasValue == true)
            {
                query = query.Where(pps => pps.Status == dto.Status.Value);
            }

            if (dto?.IdCidade.HasValue == true && dto.IdCidade.Value > 0)
            {
                query = query.Where(pps => pps.Peca.Local.IdCidade == dto.IdCidade.Value);
            }

            if (!string.IsNullOrWhiteSpace(dto?.Anunciante))
            {
                var termo = dto.Anunciante.Trim();
                // So cobre celulas ja vinculadas a um Pedido (Solicitada em
                // diante) — celula "Disponivel" nao tem anunciante, e busca por
                // anunciante nao deveria trazê-la mesmo.
                query = query.Where(pps => pps.Pedido != null && pps.Pedido.Campanha.Cliente.Nome.Contains(termo));
            }

            // A pagina e de PECAS, nao de celulas.
            //
            // A grade e peca (linha) x periodo (coluna). Paginar a lista plana de
            // celulas cortaria uma peca no meio: parte dos periodos dela na pagina
            // 1, o resto na 2, e a linha apareceria duas vezes incompleta. Entao
            // primeiro se decide QUAIS pecas entram na pagina, depois se busca
            // todas as celulas delas.
            var (page, pageSize) = WlPaginacao.Normalizar(pagina);

            var pecasQuery = query
                .Select(pps => new
                {
                    PecaId = pps.IdPeca,
                    PecaCodigo = pps.Peca.Codigo,
                    LocalId = pps.Peca.IdLocal,
                    LocalCodigo = pps.Peca.Local.Codigo
                })
                .Distinct();

            var total = await pecasQuery.CountAsync();

            // Desempate por PecaId: locais e codigos de peca se repetem entre
            // pecas diferentes, e sem ordem total a pagina 2 pode repetir uma
            // linha que a 1 ja trouxe.
            var pecasPagina = await pecasQuery
                .OrderBy(p => p.LocalCodigo)
                .ThenBy(p => p.PecaCodigo)
                .ThenBy(p => p.PecaId)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            var idsPecaPagina = pecasPagina.Select(p => p.PecaId).ToList();

            if (idsPecaPagina.Count == 0)
                return Ok(WlPaginacao.Montar(Array.Empty<object>(), page, pageSize, total));

            // `Periodo.Nome` e propriedade calculada e esta marcada com
            // `Ignore(x => x.Nome)` no PeriodoMap — nao existe coluna equivalente.
            // Projeta-la aqui fazia o EF6 lancar NotSupportedException ao traduzir a
            // query, e a grade de programacao respondia 500 sempre.
            //
            // A projecao traz o Periodo inteiro e o Nome e resolvido depois, ja em
            // memoria. Mesma correcao aplicada em LookupsController.GetPeriodos.
            var brutos = await query
                .Where(pps => idsPecaPagina.Contains(pps.IdPeca))
                .Select(pps => new
                {
                    PecaId = pps.IdPeca,
                    PecaCodigo = pps.Peca.Codigo,
                    LocalId = pps.Peca.IdLocal,
                    LocalCodigo = pps.Peca.Local.Codigo,
                    PeriodoId = pps.IdPeriodo,
                    Periodo = pps.Periodo,
                    Status = pps.Status
                })
                .ToListAsync();

            var grade = brutos
                .Select(x => new
                {
                    x.PecaId,
                    x.PecaCodigo,
                    x.LocalId,
                    x.LocalCodigo,
                    x.PeriodoId,
                    PeriodoNome = x.Periodo != null ? x.Periodo.Nome : null,
                    Status = x.Status.ToString()
                })
                .ToList();

            return Ok(WlPaginacao.Montar(grade, page, pageSize, total));
        }
    }

    /// <summary>
    /// Filtros da grade de programação (VEI-RD-86). Substitui o antigo par
    /// IdPeriodo/IdLocal por seis filtros aplicados no servidor — nunca em
    /// memória, sempre como <c>Where</c> traduzido para SQL.
    /// </summary>
    public class ProgramacaoFiltroDto
    {
        /// <summary>Mantido por compatibilidade com quem já filtra por local direto.</summary>
        public int? IdLocal { get; set; }

        public PeriodicidadeEnum? Periodicidade { get; set; }

        /// <summary>Id do Periodo que abre o intervalo.</summary>
        public int? IdPeriodoInicial { get; set; }

        /// <summary>Id do Periodo que fecha o intervalo.</summary>
        public int? IdPeriodoFinal { get; set; }

        public StatusPecaPeriodoEnum? Status { get; set; }

        public int? IdCidade { get; set; }

        /// <summary>Busca textual pelo nome do anunciante (Cliente) da campanha vinculada à célula.</summary>
        public string Anunciante { get; set; }
    }
}
