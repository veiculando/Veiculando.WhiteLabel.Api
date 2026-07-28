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
    public class LookupsController : ControllerBase
    {
        private readonly VeiculandoDataContext _db;
        private readonly ITenantContext _tenantContext;

        public LookupsController(VeiculandoDataContext db, ITenantContext tenantContext)
        {
            _db = db;
            _tenantContext = tenantContext;
        }

        [HttpGet("cidades")]
        public async Task<IActionResult> GetCidades()
        {
            var afiliadaId = _tenantContext.AfiliadaId;
            var cidades = await _db.Locais
                .Where(l => l.IdAfiliada == afiliadaId && l.StatusExibicao == StatusExibicaoEnum.Ativo)
                .Select(l => new { l.Cidade.Id, l.Cidade.Nome, l.Cidade.Estado.Sigla })
                .Distinct()
                .ToListAsync();

            return Ok(cidades);
        }

        [HttpGet("formatos")]
        public async Task<IActionResult> GetFormatos()
        {
            var formatos = await _db.Pecas
                .Where(p => p.Local.IdAfiliada == _tenantContext.AfiliadaId && p.StatusExibicao == StatusExibicaoEnum.Ativo)
                .Select(p => new { p.Formato.Largura, p.Formato.Altura })
                .Distinct()
                .ToListAsync();

            return Ok(formatos);
        }

        [HttpGet("suportes")]
        public async Task<IActionResult> GetSuportes()
        {
            var suportes = await _db.TiposSuporte
                .Where(s => s.StatusExibicao == StatusExibicaoEnum.Ativo)
                .Select(s => new { s.Id, s.Nome })
                .ToListAsync();

            return Ok(suportes);
        }

        [HttpGet("segmentos")]
        public async Task<IActionResult> GetSegmentos()
        {
            var segmentos = await _db.Segmento
                .Select(s => new { s.Id, s.Nome })
                .ToListAsync();

            return Ok(segmentos);
        }

        [HttpGet("pois")]
        public async Task<IActionResult> GetPois()
        {
            var pois = await _db.PoiCategoria
                .Select(p => new { p.Id, p.Nome })
                .ToListAsync();

            return Ok(pois);
        }

        [HttpGet("periodos")]
        public async Task<IActionResult> GetPeriodos()
        {
            var periodos = await _db.Periodos
                .Where(p => p.StatusExibicao == StatusExibicaoEnum.Ativo)
                .OrderByDescending(p => p.DataInicio)
                .Select(p => new { p.Id, p.Nome, p.DataInicio, p.DataFim })
                .ToListAsync();

            return Ok(periodos);
        }
    }
}
