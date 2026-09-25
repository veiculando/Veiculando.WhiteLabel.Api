using System;
using System.ComponentModel.DataAnnotations;
using System.Data.Entity;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities;
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

        /// <summary>
        /// Cria uma campanha no contexto da compra, sem abrir a superfície de
        /// gestão de Minhas Campanhas. A identidade comercial vem do KYC aprovado
        /// e do usuário Core correspondente; nunca do cliente ou agência enviados
        /// pelo navegador.
        /// </summary>
        [HttpPost("campaigns")]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public async Task<IActionResult> CreateCampaign([FromBody] CreateCampaignRequest request, CancellationToken ct)
        {
            if (!TryUser(out var userId)) return Unauthorized();
            if (request == null || string.IsNullOrWhiteSpace(request.Name) ||
                string.IsNullOrWhiteSpace(request.Product) || request.EndDate.Date < request.StartDate.Date ||
                request.EndDate.Date < DateTime.UtcNow.Date || request.EndDate.Date > DateTime.UtcNow.Date.AddYears(2) ||
                request.Budget < 0)
                return BadRequest(new { message = "Informe nome, produto e datas válidas para a campanha." });

            var onboarding = await _db.WlAppOnboardings.AsNoTracking()
                .SingleOrDefaultAsync(x => x.UsuarioId == userId && x.AfiliadaId == _tenant.AfiliadaId, ct);
            if (onboarding?.Status != WlAppKycStatus.Aprovado || onboarding.TipoConta != "pj" ||
                string.IsNullOrWhiteSpace(onboarding.Documento))
                return StatusCode(403, new { message = "A aprovação do cadastro empresarial é necessária para criar a campanha." });

            var buyer = await _tenant.UsuariosAnunciante.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == userId, ct);
            if (buyer == null) return Unauthorized();

            var client = await _db.Clientes.Include(x => x.AgenciasContratadas)
                .SingleOrDefaultAsync(x => x.Cnpj.Numero == onboarding.Documento &&
                    x.StatusExibicao == StatusExibicaoEnum.Ativo &&
                    x.AfiliadasVinculadas.Any(v => v.IdAfiliada == _tenant.AfiliadaId &&
                        v.Status == StatusVinculoEnum.Ativo), ct);
            if (client == null)
                return Conflict(new { message = "O anunciante aprovado ainda não possui vínculo comercial ativo nesta exibidora." });

            var responsible = await _db.UsuariosAnunciantes.Include(x => x.Agencia)
                .SingleOrDefaultAsync(x => x.Email.Endereco == buyer.Email.Endereco &&
                    x.StatusAprovacao == StatusUsuarioEnum.Aprovado &&
                    x.StatusExibicao == StatusExibicaoEnum.Ativo, ct);
            if (responsible?.Agencia == null || !client.AgenciasContratadas.Any(link =>
                link.IdAgencia == responsible.Agencia.Id && link.StatusExibicao == StatusExibicaoEnum.Ativo &&
                link.DataInicioContrato <= DateTime.UtcNow && link.DataExpiracaoContrato >= DateTime.UtcNow))
                return Conflict(new { message = "O cadastro aprovado ainda não está associado a um responsável comercial habilitado." });

            var campaign = new Campanha(responsible, client, request.Name.Trim(), request.Product.Trim(),
                request.Job?.Trim(), request.StartDate.Date, request.EndDate.Date, request.Budget);
            if (!campaign.IsValid())
                return BadRequest(new { message = "Dados da campanha inválidos.", details = campaign.Notifications });
            campaign.RegistrarOrigem(FonteOrigemEnum.WhiteLabel, responsible.Agencia.Id, responsible.Id);
            _db.Campanhas.Add(campaign);
            await _db.SaveChangesAsync(ct);
            return CreatedAtAction(nameof(Get), new { id = campaign.Id, code = campaign.Codigo, name = campaign.Nome });
        }

        public sealed class CreateCampaignRequest
        {
            [Required, StringLength(100, MinimumLength = 3)] public string Name { get; set; }
            [Required, StringLength(100, MinimumLength = 2)] public string Product { get; set; }
            [StringLength(100)] public string Job { get; set; }
            public DateTime StartDate { get; set; }
            public DateTime EndDate { get; set; }
            [Range(0, 100000000)] public decimal? Budget { get; set; }
        }

        private bool TryUser(out int id)
        {
            id = 0;
            return User.FindFirstValue("WlPerfil") == "Anunciante" &&
                   int.TryParse(User.FindFirstValue("WlAnuncianteId"), out id);
        }
    }
}
