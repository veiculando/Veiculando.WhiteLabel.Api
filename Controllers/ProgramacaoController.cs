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
    [Authorize(Policy = AuthorizationSetup.PecaGerenciar)]
    public class ProgramacaoController : ControllerBase
    {
        private readonly VeiculandoDataContext _db;
        private readonly ITenantQueries _tenant;

        public ProgramacaoController(VeiculandoDataContext db, ITenantQueries tenant)
        {
            _db = db;
            _tenant = tenant;
        }

        [HttpPost("listar")]
        public async Task<IActionResult> ListarGrade(
            [FromBody] ProgramacaoFiltroDto dto,
            [FromQuery] WlPaginaRequest pagina)
        {
            var afiliadaId = _tenant.AfiliadaId;

            var query = _tenant.PecaPeriodoStatus;

            if (dto?.IdPeriodo.HasValue == true && dto.IdPeriodo.Value > 0)
            {
                query = query.Where(pps => pps.IdPeriodo == dto.IdPeriodo.Value);
            }

            if (dto?.IdLocal.HasValue == true && dto.IdLocal.Value > 0)
            {
                query = query.Where(pps => pps.Peca.IdLocal == dto.IdLocal.Value);
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

    public class ProgramacaoFiltroDto
    {
        public int? IdPeriodo { get; set; }
        public int? IdLocal { get; set; }
    }
}
