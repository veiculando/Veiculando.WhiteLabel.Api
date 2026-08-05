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
        private readonly ITenantQueries _tenant;

        public LookupsController(VeiculandoDataContext db, ITenantQueries tenant)
        {
            _db = db;
            _tenant = tenant;
        }

        [HttpGet("cidades")]
        public async Task<IActionResult> GetCidades()
        {
            var afiliadaId = _tenant.AfiliadaId;
            var cidades = await _tenant.Locais
                .Where(l => l.StatusExibicao == StatusExibicaoEnum.Ativo)
                .Select(l => new { l.Cidade.Id, l.Cidade.Nome, l.Cidade.Estado.Sigla })
                .Distinct()
                .ToListAsync();

            return Ok(cidades);
        }

        [HttpGet("formatos")]
        public async Task<IActionResult> GetFormatos()
        {
            var formatos = await _tenant.Pecas
                .Where(p => p.StatusExibicao == StatusExibicaoEnum.Ativo)
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

        /// <summary>
        /// Bi-semanas ativas, mais recentes primeiro.
        /// </summary>
        /// <remarks>
        /// <c>Periodo.Nome</c> NAO e coluna: e propriedade calculada
        /// (<c>get => RetornaNome()</c>) e esta marcada com <c>Ignore(x =&gt; x.Nome)</c>
        /// no <c>PeriodoMap</c>. Dentro de um <c>Select</c> traduzido para SQL o EF6
        /// lanca NotSupportedException ("The specified type member 'Nome' is not
        /// supported in LINQ to Entities"), e este endpoint respondia 500 sempre —
        /// o dropdown de bi-semana da tela de Programacao nunca carregava.
        ///
        /// <para>Mesma classe do <c>String.Split</c> que quebrava a listagem de
        /// operadores: expressao que so existe em C# usada onde o EF precisa gerar
        /// SQL. A projecao materializa as colunas reais primeiro e o <c>Nome</c> e
        /// calculado depois, ja em memoria.</para>
        /// </remarks>
        [HttpGet("periodos")]
        public async Task<IActionResult> GetPeriodos()
        {
            var brutos = await _db.Periodos
                .Where(p => p.StatusExibicao == StatusExibicaoEnum.Ativo)
                .OrderByDescending(p => p.DataInicio)
                .ToListAsync();

            var periodos = brutos
                .Select(p => new { p.Id, p.Nome, p.DataInicio, p.DataFim })
                .ToList();

            return Ok(periodos);
        }
    }
}
