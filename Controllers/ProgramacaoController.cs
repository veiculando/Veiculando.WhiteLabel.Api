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
        private readonly ITenantContext _tenantContext;

        public ProgramacaoController(VeiculandoDataContext db, ITenantContext tenantContext)
        {
            _db = db;
            _tenantContext = tenantContext;
        }

        [HttpPost("listar")]
        public async Task<IActionResult> ListarGrade([FromBody] ProgramacaoFiltroDto dto)
        {
            var afiliadaId = _tenantContext.AfiliadaId;

            var query = _db.PecaPeriodoStatus
                .Where(pps => pps.Peca.Local.IdAfiliada == afiliadaId);

            if (dto?.IdPeriodo.HasValue == true && dto.IdPeriodo.Value > 0)
            {
                query = query.Where(pps => pps.IdPeriodo == dto.IdPeriodo.Value);
            }

            if (dto?.IdLocal.HasValue == true && dto.IdLocal.Value > 0)
            {
                query = query.Where(pps => pps.Peca.IdLocal == dto.IdLocal.Value);
            }

            var grade = await query
                .Select(pps => new
                {
                    PecaId = pps.IdPeca,
                    PecaCodigo = pps.Peca.Codigo,
                    LocalId = pps.Peca.IdLocal,
                    LocalCodigo = pps.Peca.Local.Codigo,
                    PeriodoId = pps.IdPeriodo,
                    PeriodoNome = pps.Periodo.Nome,
                    Status = pps.Status.ToString()
                })
                .ToListAsync();

            return Ok(grade);
        }
    }

    public class ProgramacaoFiltroDto
    {
        public int? IdPeriodo { get; set; }
        public int? IdLocal { get; set; }
    }
}
