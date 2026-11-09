using System;
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
using Veiculando.WhiteLabel.Api.Services;

namespace Veiculando.WhiteLabel.Api.Controllers
{
    /// <summary>
    /// Contexto comercial mínimo para a compra; não expõe gestão de campanhas.
    /// </summary>
    [ApiController]
    [Route("api/wl/app/checkout/context")]
    [Authorize]
    public sealed class AppCheckoutContextController : ControllerBase
    {
        private readonly VeiculandoDataContext _db;
        private readonly ITenantQueries _tenant;
        private readonly CarteiraProspeccao _carteira;
        private readonly AppCampanhasEscopo _escopo;

        public AppCheckoutContextController(VeiculandoDataContext db, ITenantQueries tenant, CarteiraProspeccao carteira,
            AppCampanhasEscopo escopo)
        {
            _db = db;
            _tenant = tenant;
            _carteira = carteira;
            _escopo = escopo;
        }

        [HttpGet]
        public async Task<IActionResult> Get(CancellationToken ct)
        {
            if (!TryUser(out var userId)) return Unauthorized();
            var escopo = await _escopo.ResolverAsync(User, userId,
                "A aprovação do cadastro é necessária para escolher o contexto da compra.", ct);
            if (escopo.StatusErro.HasValue)
                return escopo.StatusErro == 401
                    ? Unauthorized()
                    : StatusCode(escopo.StatusErro.Value, new { message = escopo.MensagemErro });

            var now = DateTime.UtcNow;
            // O contexto da compra tira canceladas e aprovadas; Minhas Campanhas mostra como histórico.
            var campanhas = await escopo.Campanhas
                .Where(x => x.Status != StatusCampanhaEnum.Cancelada && x.Status != StatusCampanhaEnum.Aprovada)
                .OrderByDescending(x => x.DataAtualizacao)
                .Select(x => new { x.Id, x.Codigo, x.Nome, x.DataInicioPrevisto, x.DataFimPrevisto })
                .ToListAsync(ct);

            // Mesmo conjunto de clientes do GET /api/wl/app/clients (VEI-RD-67e). Anunciante direto
            // não escolhe cliente na compra, então aqui continua sem lista.
            var clients = escopo.Perfil == "anunciante_direto"
                ? null
                : await _db.Clientes.AsNoTracking().Where(c => escopo.Clientes.Contains(c.Id)).OrderBy(c => c.Nome)
                    .Select(c => new { id = c.Id, name = c.Nome }).ToListAsync(ct);

            var periodos = await _db.Periodos.AsNoTracking()
                .Where(x => x.StatusExibicao == StatusExibicaoEnum.Ativo && x.DataFim >= now)
                .OrderBy(x => x.DataInicio)
                .ToListAsync(ct);

            return Ok(new
            {
                clients,
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
        /// e do usuário Core correspondente. Agências podem escolher somente
        /// clientes com representação contratual ativa na mesma exibidora.
        /// </summary>
        [HttpPost("campaigns")]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public async Task<IActionResult> CreateCampaign([FromBody] CreateCampaignRequest request, CancellationToken ct)
        {
            if (!TryUser(out var userId)) return Unauthorized();
            // Mesma validação do PUT de Minhas Campanhas (VEI-RD-68e): 400 sempre com details.
            var invalidos = CampanhaAppValidacao.Validar(request);
            if (invalidos.Count > 0) return CampanhaAppValidacao.Erro(CampanhaAppValidacao.Mensagem, invalidos);

            var onboarding = await _db.WlAppOnboardings.AsNoTracking()
                .SingleOrDefaultAsync(x => x.UsuarioId == userId && x.AfiliadaId == _tenant.AfiliadaId, ct);
            if (onboarding?.Status != WlAppKycStatus.Aprovado ||
                (onboarding.TipoConta != "ad" && onboarding.TipoConta != "ag" && onboarding.TipoConta != "pj") ||
                string.IsNullOrWhiteSpace(onboarding.Documento))
                return StatusCode(403, new { message = "A aprovação do cadastro empresarial é necessária para criar a campanha." });

            var buyer = await _tenant.UsuariosAnunciante.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == userId, ct);
            if (buyer == null) return Unauthorized();
            var sessao = await _carteira.ResolverAsync(User, buyer, ct);
            if (sessao.Modo == ModoSessaoApp.Recusada)
                return StatusCode(403, new { message = SessaoApp.MensagemRecusada });
            var carteira = sessao.Modo == ModoSessaoApp.Carteira;
            if (carteira)
            {
                if (!buyer.AgenciaId.HasValue || !request.ClientId.HasValue)
                    return CampanhaAppValidacao.Erro("Selecione um cliente da sua carteira.");
                if (!await _carteira.ClientesLiberados(sessao.VendedorId, buyer.AgenciaId.Value)
                        .AnyAsync(id => id == request.ClientId.Value, ct))
                    return StatusCode(403, new { message = "Este cliente não está liberado na sua carteira." });
            }
            else if (onboarding.TipoConta == "ag" && (!buyer.AgenciaId.HasValue || !request.ClientId.HasValue))
                return CampanhaAppValidacao.Erro("Selecione um anunciante representado por esta agência.");
            if (onboarding.TipoConta == "ad" && (!buyer.ClienteId.HasValue || !buyer.AgenciaId.HasValue))
                return Conflict(new { message = "Identidade de venda direta ainda não provisionada." });

            var desiredClientId = carteira || onboarding.TipoConta == "ag" ? request.ClientId : buyer.ClienteId;
            var client = await _db.Clientes.Include(x => x.AgenciasContratadas)
                .SingleOrDefaultAsync(x =>
                    (onboarding.TipoConta == "pj" ? x.Cnpj.Numero == onboarding.Documento : x.Id == desiredClientId) &&
                    x.StatusExibicao == StatusExibicaoEnum.Ativo &&
                    x.AfiliadasVinculadas.Any(v => v.IdAfiliada == _tenant.AfiliadaId &&
                        v.Status == StatusVinculoEnum.Ativo), ct);
            if (client == null)
                return Conflict(new { message = "O anunciante aprovado ainda não possui vínculo comercial ativo nesta exibidora." });

            var responsible = await _db.UsuariosAnunciantes.Include(x => x.Agencia)
                .SingleOrDefaultAsync(x => x.Email.Endereco == buyer.Email.Endereco &&
                    x.StatusAprovacao == StatusUsuarioEnum.Aprovado &&
                    x.StatusExibicao == StatusExibicaoEnum.Ativo, ct);
            if (responsible?.Agencia == null ||
                (buyer.AgenciaId.HasValue && responsible.Agencia.Id != buyer.AgenciaId.Value) ||
                (onboarding.TipoConta != "pj" && !await _db.AfiliadaAgencias.AnyAsync(link =>
                    link.IdAfiliada == _tenant.AfiliadaId && link.IdAgencia == responsible.Agencia.Id &&
                    link.Status == StatusVinculoEnum.Ativo, ct)) ||
                !client.AgenciasContratadas.AsQueryable()
                    .Any(AppCampanhasEscopo.AgenciaClienteVigente(responsible.Agencia.Id, DateTime.UtcNow)))
                return Conflict(new { message = "O cadastro aprovado ainda não está associado a um responsável comercial habilitado." });

            var campaign = new Campanha(responsible, client, request.Name.Trim(), request.Product.Trim(),
                request.Job?.Trim(), request.StartDate.Date, request.EndDate.Date, request.Budget);
            if (!campaign.IsValid())
                return CampanhaAppValidacao.Erro("Dados da campanha inválidos.",
                    campaign.Notifications.Select(n => n.Message));
            campaign.RegistrarOrigem(FonteOrigemEnum.WhiteLabel, responsible.Agencia.Id, responsible.Id);
            _db.Campanhas.Add(campaign);
            await _db.SaveChangesAsync(ct);
            return CreatedAtAction(nameof(Get), new { id = campaign.Id, code = campaign.Codigo, name = campaign.Nome });
        }

        public sealed class CreateCampaignRequest : CampanhaAppRequest
        {
            public int? ClientId { get; set; }
        }

        private bool TryUser(out int id)
        {
            id = 0;
            return User.FindFirstValue("WlPerfil") == "Anunciante" &&
                   int.TryParse(User.FindFirstValue("WlAnuncianteId"), out id);
        }
    }
}
