using System;
using System.Data.Entity;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities.WhiteLabel;
using Veiculando.Domain.Enums;
using Veiculando.WhiteLabel.Api.Middleware;

namespace Veiculando.WhiteLabel.Api.Controllers
{
    /// <summary>
    /// Contexto comercial mínimo para o checkout. Isto não é a superfície de
    /// "Minhas Campanhas": não cria, edita, lista histórico nem expõe pedidos.
    /// </summary>
    [ApiController]
    [Route("api/wl/app/checkout/context")]
    [Authorize]
    public sealed class AppCheckoutContextController : ControllerBase
    {
        private readonly VeiculandoDataContext _db;
        private readonly ITenantQueries _tenant;

        public AppCheckoutContextController(VeiculandoDataContext db, ITenantQueries tenant)
        {
            _db = db;
            _tenant = tenant;
        }

        [HttpGet]
        public async Task<IActionResult> Get(CancellationToken ct)
        {
            if (!TryUser(out var userId)) return Unauthorized();

            var onboarding = await _db.WlAppOnboardings.AsNoTracking()
                .SingleOrDefaultAsync(x => x.UsuarioId == userId && x.AfiliadaId == _tenant.AfiliadaId, ct);
            if (onboarding == null || onboarding.Status != WlAppKycStatus.Aprovado || string.IsNullOrWhiteSpace(onboarding.Documento))
                return StatusCode(403, new { message = "A aprovação do cadastro é necessária para escolher o contexto da compra." });

            var now = DateTime.UtcNow;
            var campanhas = await _db.Campanhas.AsNoTracking()
                .Include(x => x.Cliente)
                .Where(x => x.StatusExibicao == StatusExibicaoEnum.Ativo &&
                            x.Cliente.Cnpj.Numero == onboarding.Documento &&
                            x.Cliente.AfiliadasVinculadas.Any(v => v.IdAfiliada == _tenant.AfiliadaId &&
                                v.Status == StatusVinculoEnum.Ativo) &&
                            x.Status != StatusCampanhaEnum.Cancelada && x.Status != StatusCampanhaEnum.Aprovada)
                .OrderByDescending(x => x.DataAtualizacao)
                .Select(x => new { x.Id, x.Codigo, x.Nome, x.DataInicioPrevisto, x.DataFimPrevisto })
                .ToListAsync(ct);

            var periodos = await _db.Periodos.AsNoTracking()
                .Where(x => x.StatusExibicao == StatusExibicaoEnum.Ativo && x.DataFim >= now)
                .OrderBy(x => x.DataInicio)
                .ToListAsync(ct);

            return Ok(new
            {
                campaigns = campanhas.Select(c => new
                {
                    id = c.Id,
                    code = c.Codigo,
                    name = c.Nome,
                    periods = periodos.Where(p => p.DataInicio <= c.DataFimPrevisto && p.DataFim >= c.DataInicioPrevisto)
                        .Select(p => new { p.Codigo, p.Nome, p.DataInicio, p.DataFim })
                })
            });
        }

        private bool TryUser(out int id)
        {
            id = 0;
            return User.FindFirstValue("WlPerfil") == "Anunciante" &&
                   int.TryParse(User.FindFirstValue("WlAnuncianteId"), out id);
        }
    }
}
