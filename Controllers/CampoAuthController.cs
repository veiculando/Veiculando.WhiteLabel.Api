using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Data.Entity;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Enums;
using Veiculando.Infra.Security;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;
using BC = BCrypt.Net.BCrypt;

namespace Veiculando.WhiteLabel.Api.Controllers
{
    [ApiController]
    [Route("api/wl/campo/auth")]
    public class CampoAuthController : ControllerBase
    {
        public const string ClaimUsuarioOperadorId = "UsuarioOperadorId";
        public const string ClaimWlUsuarioOperadorId = "WlUsuarioOperadorId";
        public const string ClaimOrigem = "OrigemCampo";

        private const string CredenciaisInvalidas = "Credenciais inválidas.";

        private readonly VeiculandoDataContext _db;
        private readonly JwtSettings _jwtSettings;
        private readonly ITenantQueries _tenant;
        private readonly WlPublicLinks _links;
        private readonly IWlTenantResolver _tenantResolver;
        private readonly IWlPasswordEmailSender _email;
        private readonly IPasswordResetAttemptGuard _tentativas;
        private readonly ILogger<CampoAuthController> _logger;

        public CampoAuthController(
            VeiculandoDataContext db,
            IOptions<JwtSettings> jwtSettings,
            ITenantQueries tenant,
            WlPublicLinks links,
            IWlTenantResolver tenantResolver,
            IWlPasswordEmailSender email,
            IPasswordResetAttemptGuard tentativas,
            ILogger<CampoAuthController> logger)
        {
            _db = db;
            _jwtSettings = jwtSettings.Value;
            _tenant = tenant;
            _links = links;
            _tenantResolver = tenantResolver;
            _email = email;
            _tentativas = tentativas;
            _logger = logger;
        }

        [AllowAnonymous]
        [EnableRateLimiting(Startup.RateLimitLogin)]
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.Email) || string.IsNullOrWhiteSpace(request?.Senha))
                return BadRequest(new { message = "Email e senha são obrigatórios." });

            var email = request.Email.ToLower().Trim();
            var afiliadaId = _tenant.AfiliadaId;

            var wl = await _db.WlUsuariosOperador
                .FirstOrDefaultAsync(u => u.Email.Endereco == email
                    && u.AfiliadaId == afiliadaId
                    && u.StatusExibicao == StatusExibicaoEnum.Ativo);

            var core = wl == null
                ? await _db.UsuariosOperador.FirstOrDefaultAsync(u =>
                    u.Email.Endereco == email
                    && u.IdAfiliada == afiliadaId
                    && u.StatusExibicao == StatusExibicaoEnum.Ativo
                    && u.EmailConfirmado
                    && u.StatusAprovacao == StatusUsuarioEnum.Aprovado)
                : null;

            var senhaWl = wl != null
                && wl.StatusConvite == StatusConviteWlEnum.Aceito
                && !string.IsNullOrWhiteSpace(wl.SenhaHash)
                && BC.Verify(request.Senha, wl.SenhaHash);
            var senhaCore = core != null && SenhaUsuarioCore.Confere(request.Senha, core.Senha);

            var decisao = CampoLoginDecisao.Resolver(
                wl != null,
                senhaWl,
                wl?.Usuario_OperadorId,
                wl?.Id,
                core != null,
                senhaCore,
                core?.Id ?? 0);

            if (!decisao.Ok)
                return Unauthorized(new { message = CredenciaisInvalidas });

            if (wl != null)
            {
                wl.RegistrarLogin();
                await _db.SaveChangesAsync();
            }

            var nome = core?.Nome ?? email;
            return Ok(Emitir(decisao, email, nome, afiliadaId));
        }

        [Authorize]
        [HttpPost("refresh")]
        public async Task<IActionResult> Refresh()
        {
            var idTexto = User.FindFirstValue(ClaimUsuarioOperadorId);
            if (!int.TryParse(idTexto, out var usuarioOperadorId))
                return Unauthorized(new { message = "Sessão inválida." });

            var afiliadaId = _tenant.AfiliadaId;
            var core = await _db.UsuariosOperador
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == usuarioOperadorId
                    && u.IdAfiliada == afiliadaId
                    && u.StatusExibicao == StatusExibicaoEnum.Ativo);

            if (core == null)
                return Unauthorized(new { message = "Sessão inválida." });

            int? wlId = null;
            var wlTexto = User.FindFirstValue(ClaimWlUsuarioOperadorId);
            if (int.TryParse(wlTexto, out var parsed))
                wlId = parsed;

            var origem = User.FindFirstValue(ClaimOrigem) == nameof(OrigemCampo.WhiteLabel)
                ? OrigemCampo.WhiteLabel
                : OrigemCampo.Core;
            var decisao = origem == OrigemCampo.WhiteLabel && wlId != null
                ? CampoLoginDecisao.WhiteLabel(core.Id, wlId.Value)
                : CampoLoginDecisao.Core(core.Id);

            return Ok(Emitir(decisao, core.Email.Endereco, core.Nome, afiliadaId));
        }

        private static readonly object RespostaEmail = new
        {
            message = "Se o e-mail informado estiver cadastrado nesta instância, enviaremos o caminho."
        };

        [AllowAnonymous]
        [EnableRateLimiting(Startup.RateLimitRecuperacaoSenha)]
        [HttpPost("esqueci-senha")]
        public async Task<IActionResult> EsqueciSenha([FromBody] EsqueciSenhaRequest request, CancellationToken ct)
        {
            var email = (request?.Email ?? string.Empty).ToLowerInvariant().Trim();
            var afiliadaId = _tenant.AfiliadaId;
            if (string.IsNullOrWhiteSpace(email) || !_tentativas.PermitirTentativa(afiliadaId, email))
                return Ok(RespostaEmail);

            var usuario = await _db.WlUsuariosOperador.FirstOrDefaultAsync(u =>
                u.Email.Endereco == email
                && u.AfiliadaId == afiliadaId
                && u.StatusExibicao == StatusExibicaoEnum.Ativo
                && u.StatusConvite == StatusConviteWlEnum.Aceito, ct);
            if (usuario == null)
                return Ok(RespostaEmail);

            var token = usuario.GerarTokenRecuperacao();
            await _db.SaveChangesAsync(ct);
            try
            {
                var marca = (await _tenantResolver.ObterBrandingAsync(afiliadaId))?.NomeExibicao;
                await _email.EnviarRecuperacaoAsync(usuario.Email.Endereco, marca, _links.CampoRecuperacao(token, email), ct);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                usuario.InvalidarTokenRecuperacao();
                await _db.SaveChangesAsync(ct);
                _logger.LogError(ex, "Falha ao enviar esqueci senha de campo para a afiliada {AfiliadaId}.", afiliadaId);
            }

            return Ok(RespostaEmail);
        }

        [AllowAnonymous]
        [EnableRateLimiting(Startup.RateLimitRecuperacaoSenha)]
        [HttpPost("primeiro-acesso")]
        public async Task<IActionResult> SolicitarPrimeiroAcesso([FromBody] EsqueciSenhaRequest request, CancellationToken ct)
        {
            var email = (request?.Email ?? string.Empty).ToLowerInvariant().Trim();
            var afiliadaId = _tenant.AfiliadaId;
            if (string.IsNullOrWhiteSpace(email) || !_tentativas.PermitirTentativa(afiliadaId, email))
                return Ok(RespostaEmail);

            var usuario = await _db.WlUsuariosOperador.FirstOrDefaultAsync(u =>
                u.Email.Endereco == email
                && u.AfiliadaId == afiliadaId
                && u.StatusExibicao == StatusExibicaoEnum.Ativo
                && u.StatusConvite == StatusConviteWlEnum.Pendente, ct);
            if (usuario == null)
                return Ok(RespostaEmail);

            string token;
            try
            {
                token = usuario.GerarTokenConvite();
            }
            catch (InvalidOperationException)
            {
                return Ok(RespostaEmail);
            }

            await _db.SaveChangesAsync(ct);
            try
            {
                var marca = (await _tenantResolver.ObterBrandingAsync(afiliadaId))?.NomeExibicao;
                await _email.EnviarConviteAsync(usuario.Email.Endereco, marca, _links.CampoPrimeiroAcesso(token, email), ct);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.LogError(ex, "Falha ao enviar primeiro acesso de campo para a afiliada {AfiliadaId}.", afiliadaId);
            }

            return Ok(RespostaEmail);
        }

        private LoginResponse Emitir(CampoLoginDecisao decisao, string email, string nome, int afiliadaId)
        {
            var claims = new List<Claim>
            {
                new Claim("AfiliadaId", afiliadaId.ToString()),
                new Claim(ClaimUsuarioOperadorId, decisao.UsuarioOperadorId.ToString()),
                new Claim(ClaimOrigem, decisao.Origem.ToString())
            };
            if (decisao.WlUsuarioOperadorId != null)
                claims.Add(new Claim(ClaimWlUsuarioOperadorId, decisao.WlUsuarioOperadorId.Value.ToString()));

            var identidade = new WlUsuarioJwtResult(decisao.UsuarioOperadorId, nome, email);
            return new LoginResponse
            {
                Token = JwtService.GenerateToken(identidade, _jwtSettings, claims),
                ExpiresInMinutes = _jwtSettings.ExpirationInMinutes,
                Nome = nome,
                Email = email,
                Permissoes = new string[0]
            };
        }
    }
}
