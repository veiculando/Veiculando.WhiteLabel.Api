using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Veiculando.Data.Contexts;
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
        private readonly WhiteLabelDataContext _wlContext;
        private readonly JwtSettings _jwtSettings;
        private readonly ITenantContext _tenantContext;

        public AuthController(
            WhiteLabelDataContext wlContext,
            IOptions<JwtSettings> jwtSettings,
            ITenantContext tenantContext)
        {
            _wlContext = wlContext;
            _jwtSettings = jwtSettings.Value;
            _tenantContext = tenantContext;
        }

        /// <summary>
        /// Login do Operador/Usuário WhiteLabel.
        /// Valida credenciais no WL_Usuario (banco WL isolado) e emite JWT local
        /// com claims de permissões granulares (ADR-WL-008, ADR-WL-005).
        /// </summary>
        [AllowAnonymous]
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.Email) || string.IsNullOrWhiteSpace(request?.Senha))
                return BadRequest(new { message = "Email e senha são obrigatórios." });

            var usuario = _wlContext.WlUsuarios
                .FirstOrDefault(u => u.Email == request.Email && u.Ativo);

            if (usuario == null || !BC.Verify(request.Senha, usuario.SenhaHash))
                return Unauthorized(new { message = "Credenciais inválidas." });

            // Valida que o usuário pertence à instância correta (Tenant-locked — ADR-WL-005)
            var afiliadaId = _tenantContext.AfiliadaId;
            if (usuario.AfiliadaId != afiliadaId)
                return Unauthorized(new { message = "Acesso não autorizado para esta instância." });

            // Monta as claims com as permissões granulares do WL_Usuario
            var permissionClaims = BuildPermissionClaims(usuario);

            // Adiciona claims de identidade WL específicas
            var extraClaims = new List<Claim>
            {
                new Claim("AfiliadaId", afiliadaId.ToString()),
                new Claim("WlUsuarioId", usuario.Id.ToString()),
            };
            extraClaims.AddRange(permissionClaims);

            // Reutiliza o JwtService do core (Veiculando.Infra.Security)
            var userResult = new WlUsuarioJwtResult(usuario.Id, usuario.Nome, usuario.Email);
            var token = JwtService.GenerateToken(userResult, _jwtSettings, extraClaims);

            return Ok(new LoginResponse
            {
                Token = token,
                ExpiresInMinutes = _jwtSettings.ExpirationInMinutes,
                Nome = usuario.Nome,
                Email = usuario.Email,
                // Retorna permissões no response para o frontend ajustar o menu sem precisar decodificar o JWT
                Permissoes = new PermissoesDto
                {
                    PecaGerenciar = usuario.PecaGerenciar,
                    PedidoReservaGerenciar = usuario.PedidoReservaGerenciar,
                    FinanceiroVisualizar = usuario.FinanceiroVisualizar,
                    ClienteGerenciar = usuario.ClienteGerenciar
                }
            });
        }

        /// <summary>
        /// Retorna as permissões do usuário autenticado (para hidratação de UI sem re-decode do JWT).
        /// </summary>
        [Authorize]
        [HttpGet("me")]
        public IActionResult Me()
        {
            var wlUsuarioId = User.FindFirstValue("WlUsuarioId");
            if (!int.TryParse(wlUsuarioId, out var id))
                return Unauthorized();

            var usuario = _wlContext.WlUsuarios.FirstOrDefault(u => u.Id == id && u.Ativo);
            if (usuario == null) return Unauthorized();

            return Ok(new
            {
                usuario.Id,
                usuario.Nome,
                usuario.Email,
                Permissoes = new PermissoesDto
                {
                    PecaGerenciar = usuario.PecaGerenciar,
                    PedidoReservaGerenciar = usuario.PedidoReservaGerenciar,
                    FinanceiroVisualizar = usuario.FinanceiroVisualizar,
                    ClienteGerenciar = usuario.ClienteGerenciar
                }
            });
        }

        // --- Helpers ---

        private static IEnumerable<Claim> BuildPermissionClaims(Veiculando.Domain.Entities.WhiteLabel.WlUsuario usuario)
        {
            var claims = new List<Claim>();
            // A claim "permission" é verificada no AuthGuard do Angular (Exibidora WL)
            if (usuario.PecaGerenciar)            claims.Add(new Claim("permission", "PecaGerenciar"));
            if (usuario.PedidoReservaGerenciar)   claims.Add(new Claim("permission", "PedidoReservaGerenciar"));
            if (usuario.FinanceiroVisualizar)      claims.Add(new Claim("permission", "FinanceiroVisualizar"));
            if (usuario.ClienteGerenciar)          claims.Add(new Claim("permission", "ClienteGerenciar"));
            return claims;
        }
    }

    // --- DTOs e helpers locais ---

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
        public PermissoesDto Permissoes { get; set; }
    }

    public class PermissoesDto
    {
        public bool PecaGerenciar { get; set; }
        public bool PedidoReservaGerenciar { get; set; }
        public bool FinanceiroVisualizar { get; set; }
        public bool ClienteGerenciar { get; set; }
    }

    /// <summary>
    /// Adapter para que o JwtService do core consiga emitir token para WL_Usuario.
    /// Implementa a interface IUsuarioResult de Veiculando.Domain.Commands.Results.Usuarios.
    /// </summary>
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
