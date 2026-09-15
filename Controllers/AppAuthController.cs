using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Data.Entity.Infrastructure;
using System.Linq;
using System.Data.Entity;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Commands.Results.Usuarios;
using Veiculando.Domain.Entities.WhiteLabel;
using Veiculando.Domain.Enums;
using Veiculando.Infra.Security;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;
using BC = BCrypt.Net.BCrypt;

namespace Veiculando.WhiteLabel.Api.Controllers
{
    /// <summary>Autenticação do anunciante, distinta do operador da Exibidora.</summary>
    [ApiController]
    [Route("api/wl/app/auth")]
    public sealed partial class AppAuthController : ControllerBase
    {
        private readonly VeiculandoDataContext _db;
        private readonly ITenantQueries _tenant;
        private readonly JwtSettings _settings;
        private readonly ITenantContext _tenantContext;
        private readonly IWlTenantResolver _tenantResolver;
        private readonly IWlPasswordEmailSender _emailSender;
        private readonly IPasswordResetAttemptGuard _attemptGuard;
        private readonly ILogger<AppAuthController> _logger;
        private readonly IWlAppEmailSender _appEmailSender;
        private readonly IConfiguration _configuration;
        private readonly AppLoginAttemptGuard _loginAttemptGuard;
        private static readonly string DummyPasswordHash = BC.HashPassword("Senha-ficticia-sem-conta-registrada");

        private const int SenhaTamanhoMinimo = 8;
        private const int RecuperacaoSenhaPisoMs = 400;

        public AppAuthController(
            VeiculandoDataContext db,
            ITenantQueries tenant,
            IOptions<JwtSettings> settings,
            ITenantContext tenantContext,
            IWlTenantResolver tenantResolver,
            IWlPasswordEmailSender emailSender,
            IPasswordResetAttemptGuard attemptGuard,
            ILogger<AppAuthController> logger,
            IWlAppEmailSender appEmailSender,
            IConfiguration configuration,
            AppLoginAttemptGuard loginAttemptGuard)
        {
            _db = db;
            _tenant = tenant;
            _settings = settings.Value;
            _tenantContext = tenantContext;
            _tenantResolver = tenantResolver;
            _emailSender = emailSender;
            _attemptGuard = attemptGuard;
            _logger = logger;
            _appEmailSender = appEmailSender;
            _configuration = configuration;
            _loginAttemptGuard = loginAttemptGuard;
        }

        [AllowAnonymous]
        [EnableRateLimiting(Startup.RateLimitLogin)]
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] AppLoginRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.Email) || string.IsNullOrWhiteSpace(request?.Password))
                return BadRequest(new { message = "E-mail e senha são obrigatórios." });

            var email = request.Email.Trim().ToLowerInvariant();
            if (email.Length > 254 || System.Text.Encoding.UTF8.GetByteCount(request.Password) > 72)
                return Unauthorized(new { message = "Credenciais inválidas." });
            if (!_loginAttemptGuard.Allow(_tenant.AfiliadaId, email))
                return Unauthorized(new { message = "Credenciais inválidas." });
            var usuario = await _tenant.UsuariosAnunciante.FirstOrDefaultAsync(u =>
                u.Email.Endereco == email &&
                u.StatusExibicao == StatusExibicaoEnum.Ativo &&
                u.StatusConvite == StatusConviteWlEnum.Aceito);

            var validPassword = BC.Verify(request.Password, string.IsNullOrWhiteSpace(usuario?.SenhaHash) ? DummyPasswordHash : usuario.SenhaHash);
            if (usuario == null || !validPassword)
                return Unauthorized(new { message = "Credenciais inválidas." });

            var identity = await _db.WlAppIdentidades.AsNoTracking().SingleOrDefaultAsync(i => i.Id == usuario.Id);
            if (identity != null && !identity.EmailConfirmado)
                return Unauthorized(new { message = "Confirme seu e-mail para continuar.", code = "email_unconfirmed" });

            usuario.RegistrarLogin();
            return Ok(await EmitirSessaoAsync(usuario));
        }

        [Authorize]
        [HttpGet("me")]
        public async Task<IActionResult> Me()
        {
            if (User.FindFirstValue("WlPerfil") != "Anunciante" ||
                !int.TryParse(User.FindFirstValue("WlAnuncianteId"), out var id))
                return Unauthorized();

            var usuario = await _tenant.UsuariosAnunciante.AsNoTracking().FirstOrDefaultAsync(u =>
                u.Id == id &&
                u.StatusExibicao == StatusExibicaoEnum.Ativo &&
                u.StatusConvite == StatusConviteWlEnum.Aceito);
            if (usuario == null) return Unauthorized();

            return Ok(await LerPerfilAsync(usuario));
        }

        /// <summary>
        /// Renova exclusivamente a sessão do anunciante. O perfil e a afiliada
        /// são extraídos do JWT e validados de novo contra o tenant do Host.
        /// </summary>
        [Authorize]
        [HttpPost("refresh")]
        public async Task<IActionResult> Refresh()
        {
            if (User.FindFirstValue("WlPerfil") != "Anunciante" ||
                !int.TryParse(User.FindFirstValue("WlAnuncianteId"), out var id))
                return Unauthorized();

            var usuario = await _tenant.UsuariosAnunciante.AsNoTracking().FirstOrDefaultAsync(u =>
                u.Id == id &&
                u.StatusExibicao == StatusExibicaoEnum.Ativo &&
                u.StatusConvite == StatusConviteWlEnum.Aceito);
            if (usuario == null) return Unauthorized(new { message = "Sessão inválida." });

            if (!Guid.TryParse(User.FindFirstValue("jti"), out var sessionId)) return Unauthorized();
            var current = await _db.WlAppSessoes.SingleOrDefaultAsync(s => s.Id == sessionId && s.UsuarioId == id && s.AfiliadaId == _tenant.AfiliadaId);
            if (current == null || current.RevogadaEm.HasValue || current.ExpiraEm <= DateTime.UtcNow) return Unauthorized();
            current.Revogar();
            try { return Ok(await EmitirSessaoAsync(usuario)); }
            catch (DbUpdateConcurrencyException) { return Unauthorized(new { message = "Sessão já renovada." }); }
        }

        /// <summary>
        /// Inicia a recuperação sem revelar se a conta existe neste tenant.
        /// </summary>
        [AllowAnonymous]
        [EnableRateLimiting(Startup.RateLimitRecuperacaoSenha)]
        [HttpPost("forgot-password")]
        public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request, CancellationToken ct)
        {
            var cronometro = Stopwatch.StartNew();
            var email = (request?.Email ?? string.Empty).Trim().ToLowerInvariant();

            try
            {
                if (string.IsNullOrWhiteSpace(email) || !_attemptGuard.PermitirTentativa(_tenant.AfiliadaId, email))
                    return Ok(RespostaRecuperacaoSenha);

                var usuario = await _tenant.UsuariosAnunciante.FirstOrDefaultAsync(u =>
                    u.Email.Endereco == email &&
                    u.StatusExibicao == StatusExibicaoEnum.Ativo &&
                    u.StatusConvite == StatusConviteWlEnum.Aceito, ct);
                if (usuario == null) return Ok(RespostaRecuperacaoSenha);

                var token = usuario.GerarTokenRecuperacao();
                await _db.SaveChangesAsync(ct);
                try
                {
                    var branding = await _tenantResolver.ObterBrandingAsync(_tenant.AfiliadaId);
                    await _appEmailSender.RecuperacaoAsync(usuario.Email.Endereco, branding?.NomeExibicao ?? "Veiculando", MontarLinkRedefinicao(token, email), ct);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    usuario.InvalidarTokenRecuperacao();
                    await _db.SaveChangesAsync(ct);
                    _logger.LogError(ex, "Falha ao enviar recuperação de senha do anunciante para a afiliada {AfiliadaId}; token invalidado.", _tenant.AfiliadaId);
                }

                return Ok(RespostaRecuperacaoSenha);
            }
            finally
            {
                var restanteMs = RecuperacaoSenhaPisoMs - (int)cronometro.ElapsedMilliseconds;
                if (restanteMs > 0 && !ct.IsCancellationRequested)
                {
                    try { await Task.Delay(restanteMs, ct); } catch (OperationCanceledException) { }
                }
            }
        }

        /// <summary>
        /// Consome uma única vez o token enviado pelo fluxo de recuperação.
        /// </summary>
        [AllowAnonymous]
        [EnableRateLimiting(Startup.RateLimitRecuperacaoSenha)]
        [HttpPost("reset-password")]
        public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request, CancellationToken ct)
        {
            const string erroGenerico = "Link de recuperação inválido ou expirado. Solicite uma nova recuperação de senha.";
            if (string.IsNullOrWhiteSpace(request?.Email) || string.IsNullOrWhiteSpace(request?.Token) || string.IsNullOrWhiteSpace(request?.NewPassword))
                return BadRequest(new { message = "E-mail, token e nova senha são obrigatórios." });
            if (request.NewPassword.Length < SenhaTamanhoMinimo || request.NewPassword.Length > 72)
                return BadRequest(new { message = $"A senha precisa ter no mínimo {SenhaTamanhoMinimo} caracteres." });

            var email = request.Email.Trim().ToLowerInvariant();
            var usuario = await _tenant.UsuariosAnunciante.FirstOrDefaultAsync(u =>
                u.Email.Endereco == email &&
                u.StatusExibicao == StatusExibicaoEnum.Ativo &&
                u.StatusConvite == StatusConviteWlEnum.Aceito, ct);
            if (usuario == null || !usuario.ValidarTokenRecuperacao(request.Token.Trim()))
                return BadRequest(new { message = erroGenerico });

            usuario.AlterarSenha(BC.HashPassword(request.NewPassword));
            foreach (var session in await _db.WlAppSessoes.Where(s => s.UsuarioId == usuario.Id && s.AfiliadaId == _tenant.AfiliadaId && !s.RevogadaEm.HasValue).ToListAsync(ct)) session.Revogar();
            try { await _db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { return BadRequest(new { message = erroGenerico }); }
            return Ok(new { message = "Senha alterada com sucesso. Faça login com a nova senha." });
        }

        private async Task<object> EmitirSessaoAsync(WlUsuarioAnunciante usuario)
        {
            // Add(session) percorre a navegação: uma entidade AsNoTracking precisa
            // ser anexada como existente, nunca reinserida como nova identidade.
            if (_db.Entry(usuario).State == EntityState.Detached)
                _db.WlUsuariosAnunciante.Attach(usuario);
            var session = new WlAppSessao(usuario, _settings.ExpirationInMinutes);
            _db.WlAppSessoes.Add(session);
            var profile = await LerPerfilAsync(usuario);
            var claims = new List<Claim>
            {
                new Claim("AfiliadaId", _tenant.AfiliadaId.ToString()),
                new Claim("WlAnuncianteId", usuario.Id.ToString()),
                new Claim("WlPerfil", "Anunciante"),
                new Claim("jti", session.Id.ToString("N")),
                new Claim("KycStatus", profile.KycStatus)
            };
            var result = new AppUsuarioJwtResult(usuario.Id, usuario.Email.Endereco);
            await _db.SaveChangesAsync();
            return new
            {
                token = JwtService.GenerateToken(result, _settings, claims),
                expiresInMinutes = _settings.ExpirationInMinutes,
                name = profile.Name,
                email = usuario.Email.Endereco,
                accountType = profile.AccountType,
                kycStatus = profile.KycStatus,
                organizationId = profile.OrganizationId
            };
        }

        private async Task<AppProfile> LerPerfilAsync(WlUsuarioAnunciante usuario)
        {
            var identity = await _db.WlAppIdentidades.AsNoTracking().SingleOrDefaultAsync(i => i.Id == usuario.Id);
            var onboarding = await _db.WlAppOnboardings.AsNoTracking().SingleOrDefaultAsync(o => o.UsuarioId == usuario.Id && o.AfiliadaId == _tenant.AfiliadaId);
            return new AppProfile(identity?.Nome ?? usuario.Email.Endereco, usuario.Email.Endereco,
                onboarding?.TipoConta ?? (string.IsNullOrWhiteSpace(usuario.Cnpj) ? "pf" : "pj"),
                KycStatusName(onboarding?.Status ?? WlAppKycStatus.Rascunho), onboarding?.Id);
        }

        internal static string KycStatusName(WlAppKycStatus status) => status switch
        {
            WlAppKycStatus.PendenteVerificacao => "pending", WlAppKycStatus.EmAnalise => "in_review",
            WlAppKycStatus.AjustesSolicitados => "adjustments_required", WlAppKycStatus.Aprovado => "approved",
            WlAppKycStatus.Rejeitado => "rejected", WlAppKycStatus.Suspenso => "suspended", _ => "incomplete"
        };

        private sealed record AppProfile(string Name, string Email, string AccountType, string KycStatus, Guid? OrganizationId);

        [Authorize]
        [HttpPost("logout")]
        public async Task<IActionResult> Logout(CancellationToken ct)
        {
            if (!Guid.TryParse(User.FindFirstValue("jti"), out var sessionId) || !int.TryParse(User.FindFirstValue("WlAnuncianteId"), out var userId)) return Unauthorized();
            var session = await _db.WlAppSessoes.SingleOrDefaultAsync(s => s.Id == sessionId && s.UsuarioId == userId && s.AfiliadaId == _tenant.AfiliadaId, ct);
            if (session == null) return Unauthorized();
            session.Revogar(); await _db.SaveChangesAsync(ct); return NoContent();
        }

        private string MontarLinkRedefinicao(string token, string email)
        {
            var query = $"token={Uri.EscapeDataString(token)}&email={Uri.EscapeDataString(email)}";
            return $"https://{_tenantContext.Host}/esqueci-senha?{query}";
        }


        private static readonly object RespostaRecuperacaoSenha = new
        {
            message = "Se o e-mail informado estiver cadastrado nesta instância, enviaremos instruções para redefinir a senha."
        };

        public sealed class AppLoginRequest
        {
            public string Email { get; set; }
            public string Password { get; set; }
        }

        public sealed class ForgotPasswordRequest
        {
            public string Email { get; set; }
        }

        public sealed class ResetPasswordRequest
        {
            public string Email { get; set; }
            public string Token { get; set; }
            public string NewPassword { get; set; }
        }

        private sealed class AppUsuarioJwtResult : IUsuarioResult
        {
            public AppUsuarioJwtResult(int id, string email)
            {
                Id = id;
                Nome = email;
                Email = email;
            }
            public int Id { get; set; }
            public string Nome { get; set; }
            public string Email { get; set; }
            public string Perfil { get; set; } = "Anunciante";
        }
    }
}
