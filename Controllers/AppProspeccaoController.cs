using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities.WhiteLabel;
using Veiculando.Infra.Security;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;

namespace Veiculando.WhiteLabel.Api.Controllers
{
    /// <summary>
    /// Resgate do session token de prospecção, no lado do App WL — VEI-RD-83 task c.
    /// </summary>
    /// <remarks>
    /// <para><b>Este é o "aceito no App WL" do teste de audience.</b> O outro lado —
    /// recusado no painel da Exibidora — é estrutural: o token é assinado com uma
    /// chave derivada do propósito <c>prospeccao-sessao</c>, que não é a chave do JWT
    /// do painel, então o painel não consegue validá-lo. Testar só a recusa deixaria
    /// passar uma implementação que recusasse tudo; por isso os dois existem.</para>
    ///
    /// <para><b>Uso único.</b> O <c>jti</c> resgatado é marcado no cache. Limitação
    /// conhecida e registrada: <see cref="IMemoryCache"/> é por INSTÂNCIA, então com o
    /// BFF em várias réplicas um token poderia ser resgatado na réplica que ainda não
    /// o viu. Um store compartilhado resolve e é mudança de schema — território de
    /// outro card. O TTL curto limita a janela; não a fecha.</para>
    ///
    /// <para><b>O token nunca entra em log</b>, nem o <c>jti</c>: o log registra a
    /// afiliada e o operador de origem, que é o que a auditoria precisa.</para>
    /// </remarks>
    [ApiController]
    [Route("api/wl/app/prospeccao")]
    public class AppProspeccaoController : ControllerBase
    {
        private const string PrefixoCacheUsado = "prospeccao-jti:";

        private readonly VeiculandoDataContext _db;
        private readonly ITenantQueries _tenant;
        private readonly IWlLinkTemporario _links;
        private readonly IMemoryCache _cache;
        private readonly JwtSettings _settings;
        private readonly ILogger<AppProspeccaoController> _logger;

        public AppProspeccaoController(
            VeiculandoDataContext db,
            ITenantQueries tenant,
            IWlLinkTemporario links,
            IMemoryCache cache,
            IOptions<JwtSettings> settings,
            ILogger<AppProspeccaoController> logger)
        {
            _db = db;
            _tenant = tenant;
            _links = links;
            _cache = cache;
            _settings = settings.Value;
            _logger = logger;
        }

        /// <summary>
        /// Troca o session token de prospecção por uma sessão autenticada do App WL.
        /// </summary>
        [AllowAnonymous]
        [HttpPost("sessao")]
        [EnableRateLimiting(Startup.RateLimitRecuperacaoSenha)]
        public async Task<IActionResult> Resgatar([FromBody] ResgateProspeccaoRequest request, CancellationToken ct)
        {
            // Mensagem única para qualquer recusa: distinguir "token inválido" de
            // "token já usado" ou "expirado" diria a um atacante em qual dos eixos
            // ele chegou perto.
            const string recusa = "Sessão de prospecção inválida ou expirada.";

            if (string.IsNullOrWhiteSpace(request?.Token) || request.OperadorId <= 0)
                return Unauthorized(new { message = recusa });

            var recurso = $"{request.OperadorId}:{request.AnuncianteId?.ToString(CultureInfo.InvariantCulture) ?? "-"}";

            var jti = _links.Validar(
                request.Token,
                WlLinkTemporario.PropositoProspeccao,
                recurso,
                _tenant.AfiliadaId);

            if (jti == null)
                return Unauthorized(new { message = recusa });

            var chaveUso = PrefixoCacheUsado + jti;
            if (_cache.TryGetValue(chaveUso, out _))
                return Unauthorized(new { message = recusa });

            // Marcado ANTES de emitir a sessão: se a emissão falhar, o token queima
            // junto. Um token que sobrevive a uma falha parcial é um token reusável.
            _cache.Set(chaveUso, true, TimeSpan.FromHours(1));

            var anunciante = await ResolverAnuncianteAsync(request.AnuncianteId, ct);
            if (anunciante == null)
                return Unauthorized(new { message = recusa });

            if (_db.Entry(anunciante).State == EntityState.Detached)
                _db.WlUsuariosAnunciante.Attach(anunciante);

            var sessao = new WlAppSessao(anunciante, _settings.ExpirationInMinutes);
            _db.WlAppSessoes.Add(sessao);

            var claims = new List<Claim>
            {
                new Claim("AfiliadaId", _tenant.AfiliadaId.ToString(CultureInfo.InvariantCulture)),
                new Claim("WlAnuncianteId", anunciante.Id.ToString(CultureInfo.InvariantCulture)),
                new Claim("WlPerfil", "Anunciante"),
                new Claim("jti", sessao.Id.ToString("N")),
                // Trilha de origem (ADR-WL-003) DENTRO da sessão: é o que faz todo
                // pedido criado aqui ser rastreável até o operador que abriu a
                // prospecção. Sem isto, a sessão seria indistinguível de um login
                // comum do anunciante — e a origem do pedido se perderia.
                new Claim("FonteOrigem", "WhiteLabel"),
                new Claim("FonteAgenciaId", _tenant.AfiliadaId.ToString(CultureInfo.InvariantCulture)),
                new Claim("FonteUsuarioId", request.OperadorId.ToString(CultureInfo.InvariantCulture)),
                new Claim("Prospeccao", "true")
            };

            // Mesmo tipo de identidade que AppAuthController usa: duas copias das
            // mesmas claims divergiriam no dia em que uma ganhasse um campo.
            var identidade = new AppAuthController.AppUsuarioJwtResult(anunciante.Id, anunciante.Email.Endereco);
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "WL_PROSPECCAO_RESGATE tenant={Tenant} operador={Operador} anunciante={Anunciante}",
                _tenant.AfiliadaId, request.OperadorId, anunciante.Id);

            return Ok(new
            {
                token = JwtService.GenerateToken(identidade, _settings, claims),
                expiresInMinutes = _settings.ExpirationInMinutes,
                email = anunciante.Email.Endereco,
                prospeccao = true,
                fonteUsuarioId = request.OperadorId
            });
        }

        /// <summary>
        /// Resolve em nome de quem a sessão abre.
        /// </summary>
        /// <remarks>
        /// PENDÊNCIA DO HUMANO, não arbitrada aqui: o Figma abre a prospecção sem
        /// escolher o cliente. Enquanto não houver seletor, a sessão só abre com um
        /// <c>AnuncianteId</c> explícito — recusar é mais seguro do que eleger um
        /// anunciante qualquer da afiliada e criar pedidos em nome de quem não pediu.
        /// O ponto de extensão está pronto dos dois lados.
        /// </remarks>
        private Task<WlUsuarioAnunciante> ResolverAnuncianteAsync(int? anuncianteId, CancellationToken ct)
        {
            if (anuncianteId == null) return Task.FromResult<WlUsuarioAnunciante>(null);

            return _tenant.UsuariosAnunciante
                .FirstOrDefaultAsync(u => u.Id == anuncianteId.Value, ct);
        }
    }

    public sealed class ResgateProspeccaoRequest
    {
        public string Token { get; set; }

        /// <summary>Operador que abriu a sessão — faz parte do recurso assinado.</summary>
        public int OperadorId { get; set; }

        /// <summary>Anunciante em nome de quem a sessão abre.</summary>
        public int? AnuncianteId { get; set; }
    }
}
