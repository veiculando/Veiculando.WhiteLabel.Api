using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
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
        private readonly ITenantContext _contexto;
        private readonly CampoConta _conta;

        public CampoAuthController(
            VeiculandoDataContext db,
            IOptions<JwtSettings> jwtSettings,
            ITenantQueries tenant,
            ITenantContext contexto,
            CampoConta conta)
        {
            _db = db;
            _jwtSettings = jwtSettings.Value;
            _tenant = tenant;
            _contexto = contexto;
            _conta = conta;
        }

        [AllowAnonymous]
        [EnableRateLimiting(Startup.RateLimitLogin)]
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.Email) || string.IsNullOrWhiteSpace(request?.Senha))
                return BadRequest(new { message = "Email e senha são obrigatórios." });

            var email = CampoConta.Normalizar(request.Email);
            var afiliadaId = _tenant.AfiliadaId;

            var credencial = await _conta.ConferirAsync(email, request.Senha, afiliadaId);
            if (!credencial.Decisao.Ok)
                return Unauthorized(new { message = CredenciaisInvalidas });

            if (credencial.Wl != null)
            {
                credencial.Wl.RegistrarLogin();
                await _db.SaveChangesAsync();
            }

            var nome = credencial.Core?.Nome ?? email;
            return Ok(Emitir(credencial.Decisao, email, nome, afiliadaId));
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
            var email = CampoConta.Normalizar(request?.Email);
            if (!string.IsNullOrWhiteSpace(email))
                await _conta.EnviarRecuperacaoAsync(email, _tenant.AfiliadaId, _contexto.Host, ct);
            return Ok(RespostaEmail);
        }

        [AllowAnonymous]
        [EnableRateLimiting(Startup.RateLimitRecuperacaoSenha)]
        [HttpPost("primeiro-acesso")]
        public async Task<IActionResult> SolicitarPrimeiroAcesso([FromBody] EsqueciSenhaRequest request, CancellationToken ct)
        {
            var email = CampoConta.Normalizar(request?.Email);
            if (!string.IsNullOrWhiteSpace(email))
                await _conta.EnviarConviteAsync(email, _tenant.AfiliadaId, _contexto.Host, ct);
            return Ok(RespostaEmail);
        }

        private const int SenhaTamanhoMinimo = 8;

        /// <summary>Conclui o convite de campo com o token do e-mail (aberto pelo app).</summary>
        [AllowAnonymous]
        [EnableRateLimiting(Startup.RateLimitRecuperacaoSenha)]
        [HttpPost("primeiro-acesso/concluir")]
        public async Task<IActionResult> ConcluirPrimeiroAcesso([FromBody] PrimeiroAcessoRequest request, CancellationToken ct)
        {
            const string erro = "Convite inválido, expirado ou já utilizado. Solicite um novo pelo app.";
            var invalido = ValidarPedidoSenha(request?.Email, request?.Token, request?.NovaSenha);
            if (invalido != null)
                return invalido;

            var email = request.Email.ToLowerInvariant().Trim();
            var usuario = await _db.WlUsuariosOperador.FirstOrDefaultAsync(u =>
                u.Email.Endereco == email
                && u.AfiliadaId == _tenant.AfiliadaId
                && u.StatusExibicao == StatusExibicaoEnum.Ativo, ct);
            if (usuario == null || !usuario.ValidarTokenConvite(request.Token.Trim()))
                return BadRequest(new { message = erro });

            usuario.ConcluirPrimeiroAcesso(BC.HashPassword(request.NovaSenha.Trim()));
            try { await _db.SaveChangesAsync(ct); }
            catch (System.Data.Entity.Infrastructure.DbUpdateConcurrencyException)
            {
                return BadRequest(new { message = erro });
            }
            return Ok(new { message = "Senha criada. Entre com o seu e-mail e a nova senha." });
        }

        /// <summary>Troca a senha com o token de recuperação de campo.</summary>
        [AllowAnonymous]
        [EnableRateLimiting(Startup.RateLimitRecuperacaoSenha)]
        [HttpPost("alterar-senha")]
        public async Task<IActionResult> AlterarSenha([FromBody] AlterarSenhaRequest request, CancellationToken ct)
        {
            const string erro = "Link de recuperação inválido ou expirado. Peça outro pelo app.";
            var invalido = ValidarPedidoSenha(request?.Email, request?.Token, request?.NovaSenha);
            if (invalido != null)
                return invalido;

            var email = request.Email.ToLowerInvariant().Trim();
            var usuario = await _db.WlUsuariosOperador.FirstOrDefaultAsync(u =>
                u.Email.Endereco == email
                && u.AfiliadaId == _tenant.AfiliadaId
                && u.StatusExibicao == StatusExibicaoEnum.Ativo
                && u.StatusConvite == StatusConviteWlEnum.Aceito, ct);
            if (usuario == null || !usuario.ValidarTokenRecuperacao(request.Token.Trim()))
                return BadRequest(new { message = erro });

            usuario.AlterarSenha(BC.HashPassword(request.NovaSenha.Trim()));
            try { await _db.SaveChangesAsync(ct); }
            catch (System.Data.Entity.Infrastructure.DbUpdateConcurrencyException)
            {
                return BadRequest(new { message = erro });
            }
            return Ok(new { message = "Senha alterada. Entre com a nova senha." });
        }

        private IActionResult ValidarPedidoSenha(string email, string token, string novaSenha)
        {
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(novaSenha))
                return BadRequest(new { message = "E-mail, token e nova senha são obrigatórios." });
            if (novaSenha.Trim().Length < SenhaTamanhoMinimo)
                return BadRequest(new { message = $"A senha precisa ter no mínimo {SenhaTamanhoMinimo} caracteres." });
            return null;
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
