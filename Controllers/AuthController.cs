using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Data.Entity;
using Microsoft.Extensions.Options;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities.WhiteLabel;
using Veiculando.Domain.Enums;
using Veiculando.Infra.Security;
using Veiculando.WhiteLabel.Api.Configurations;
using Veiculando.WhiteLabel.Api.Middleware;
using BC = BCrypt.Net.BCrypt;

namespace Veiculando.WhiteLabel.Api.Controllers
{
    [ApiController]
    [Route("api/wl/auth")]
    public class AuthController : ControllerBase
    {
        private readonly VeiculandoDataContext _db;
        private readonly JwtSettings _jwtSettings;
        private readonly ITenantQueries _tenant;

        public AuthController(
            VeiculandoDataContext db,
            IOptions<JwtSettings> jwtSettings,
            ITenantQueries tenant)
        {
            _db = db;
            _jwtSettings = jwtSettings.Value;
            _tenant = tenant;
        }

        /// <summary>
        /// Login do Operador/Usuário WhiteLabel.
        /// Autentica via WlUsuarioAfiliada no VeiculandoDataContext e emite JWT com claims de permissão dinâmicas (ADR-WL-005, ADR-WL-008, TP-0).
        /// </summary>
        [AllowAnonymous]
        [EnableRateLimiting(Startup.RateLimitLogin)]
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.Email) || string.IsNullOrWhiteSpace(request?.Senha))
                return BadRequest(new { message = "Email e senha são obrigatórios." });

            var normalizedEmail = request.Email.ToLower().Trim();
            var afiliadaId = _tenant.AfiliadaId;

            var usuario = await _tenant.UsuariosAfiliada
                .FirstOrDefaultAsync(u => u.Email.Endereco == normalizedEmail 
                                       && u.StatusExibicao == StatusExibicaoEnum.Ativo );

            if (usuario == null || !BC.Verify(request.Senha, usuario.SenhaHash))
                return Unauthorized(new { message = "Credenciais inválidas." });

            usuario.RegistrarLogin();
            await _db.SaveChangesAsync();

            return Ok(EmitirSessao(usuario, afiliadaId));
        }

        /// <summary>
        /// Retorna os dados e permissões do operador autenticado.
        /// </summary>
        [Authorize]
        [HttpGet("me")]
        public async Task<IActionResult> Me()
        {
            var wlUsuarioIdStr = User.FindFirstValue("WlUsuarioId");
            if (!int.TryParse(wlUsuarioIdStr, out var id))
                return Unauthorized();

            var afiliadaId = _tenant.AfiliadaId;

            var usuario = await _tenant.UsuariosAfiliada
                .FirstOrDefaultAsync(u => u.Id == id 
                                       && u.StatusExibicao == StatusExibicaoEnum.Ativo );

            if (usuario == null) 
                return Unauthorized();

            return Ok(new
            {
                usuario.Id,
                usuario.Nome,
                Email = usuario.Email.Endereco,
                usuario.Cargo,
                usuario.Departamento,
                usuario.TelefoneComercial,
                usuario.DataUltimoLogin,
                Permissoes = usuario.ObterPermissoes()
            });
        }

        /// <summary>
        /// Renova o JWT do operador autenticado sem exigir re-login.
        /// Deve ser chamado pelo frontend quando o token está próximo da expiração.
        /// </summary>
        [Authorize]
        [HttpPost("refresh")]
        public async Task<IActionResult> Refresh()
        {
            var wlUsuarioIdStr = User.FindFirstValue("WlUsuarioId");
            if (!int.TryParse(wlUsuarioIdStr, out var id))
                return Unauthorized();

            var afiliadaId = _tenant.AfiliadaId;

            var usuario = await _tenant.UsuariosAfiliada
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == id
                                       && u.StatusExibicao == StatusExibicaoEnum.Ativo);

            if (usuario == null)
                return Unauthorized(new { message = "Sessão inválida." });

            return Ok(EmitirSessao(usuario, afiliadaId));
        }

        /// <summary>
        /// Monta as claims e o JWT de uma sessão de operador.
        /// </summary>
        /// <remarks>
        /// O login e o refresh precisam emitir exatamente o mesmo conjunto de
        /// claims — o bloco estava duplicado nos dois, e um token de refresh que
        /// divergisse do de login tiraria permissões do operador no meio da
        /// sessão, com o sintoma aparecendo só depois da renovação.
        ///
        /// <para>A claim de permissão usa <see cref="AuthorizationSetup.ClaimPermissao"/>,
        /// a mesma constante que as policies exigem. Antes o literal
        /// <c>"permission"</c> estava escrito à mão aqui, apesar de o
        /// <c>AuthorizationSetup</c> documentar que os dois lados compartilham a
        /// constante justamente para não descasarem.</para>
        /// </remarks>
        private LoginResponse EmitirSessao(WlUsuarioAfiliada usuario, int afiliadaId)
        {
            var permissoes = usuario.ObterPermissoes();

            var extraClaims = new List<Claim>
            {
                new Claim("AfiliadaId", afiliadaId.ToString()),
                new Claim("WlUsuarioId", usuario.Id.ToString()),
            };

            foreach (var perm in permissoes)
            {
                extraClaims.Add(new Claim(ClaimTypes.Role, perm));
                extraClaims.Add(new Claim(AuthorizationSetup.ClaimPermissao, perm));
            }

            var userResult = new WlUsuarioJwtResult(usuario.Id, usuario.Nome, usuario.Email.Endereco);

            return new LoginResponse
            {
                Token = JwtService.GenerateToken(userResult, _jwtSettings, extraClaims),
                ExpiresInMinutes = _jwtSettings.ExpirationInMinutes,
                Nome = usuario.Nome,
                Email = usuario.Email.Endereco,
                Permissoes = permissoes
            };
        }
    }

    public class LoginRequest
    {
        public string Email { get; set; }
        public string Senha { get; set; }
    }

    public class LoginResponse
    {
        public string Token { get; set; }
        public int ExpiresInMinutes { get; set; }
        public string Nome { get; set; }
        public string Email { get; set; }
        public string[] Permissoes { get; set; }
    }

    internal class WlUsuarioJwtResult : Veiculando.Domain.Commands.Results.Usuarios.IUsuarioResult
    {
        public WlUsuarioJwtResult(int id, string nome, string email)
        {
            Id = id;
            Nome = nome;
            Email = email;
            Perfil = "OperadorWL";
        }
        public int Id { get; set; }
        public string Nome { get; set; }
        public string Email { get; set; }
        public string Perfil { get; set; }
    }
}
