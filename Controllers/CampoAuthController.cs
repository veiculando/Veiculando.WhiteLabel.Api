using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
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

        public CampoAuthController(
            VeiculandoDataContext db,
            IOptions<JwtSettings> jwtSettings,
            ITenantQueries tenant)
        {
            _db = db;
            _jwtSettings = jwtSettings.Value;
            _tenant = tenant;
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
