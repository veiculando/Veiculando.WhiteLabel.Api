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
        public async Task<IActionResult> ListarGrade([FromBody] ProgramacaoFiltroDto dto)
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

            // `Periodo.Nome` e propriedade calculada e esta marcada com
            // `Ignore(x => x.Nome)` no PeriodoMap — nao existe coluna equivalente.
            // Projeta-la aqui fazia o EF6 lancar NotSupportedException ao traduzir a
            // query, e a grade de programacao respondia 500 sempre.
            //
            // A projecao traz o Periodo inteiro e o Nome e resolvido depois, ja em
            // memoria. Mesma correcao aplicada em LookupsController.GetPeriodos.
            var brutos = await query
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

            return Ok(grade);
        }
    }

    public class ProgramacaoFiltroDto
    {
        public int? IdPeriodo { get; set; }
        public int? IdLocal { get; set; }
    }
}
