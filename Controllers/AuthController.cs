using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Data.Entity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities.WhiteLabel;
using Veiculando.Domain.Enums;
using Veiculando.Infra.Security;
using Veiculando.WhiteLabel.Api.Configurations;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;
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
        private readonly ITenantContext _tenantContext;
        private readonly IWlTenantResolver _tenantResolver;
        private readonly IWlPasswordEmailSender _emailSender;
        private readonly IPasswordResetAttemptGuard _attemptGuard;
        private readonly ILogger<AuthController> _logger;

        /// <summary>Mesmo mínimo exigido no UsuariosController.</summary>
        private const int SenhaTamanhoMinimo = 8;

        /// <summary>
        /// Piso de duração do endpoint de esqueci-senha, em milissegundos.
        /// </summary>
        /// <remarks>
        /// A resposta é sempre o mesmo corpo 200, mas sem isto o TEMPO de
        /// resposta ainda distingue os caminhos: encontrar o usuário, gerar o
        /// token e chamar o SendGrid é ordens de magnitude mais lento do que só
        /// devolver a resposta genérica. Cronometrar o handler inteiro e
        /// completar a diferença com <see cref="Task.Delay(int, CancellationToken)"/>
        /// equaliza os três caminhos (e-mail existente, inexistente, limite
        /// atingido) sem precisar de um atraso artificial por branch.
        /// </remarks>
        private const int RecuperacaoSenhaPisoMs = 400;

        public AuthController(
            VeiculandoDataContext db,
            IOptions<JwtSettings> jwtSettings,
            ITenantQueries tenant,
            ITenantContext tenantContext,
            IWlTenantResolver tenantResolver,
            IWlPasswordEmailSender emailSender,
            IPasswordResetAttemptGuard attemptGuard,
            ILogger<AuthController> logger)
        {
            _db = db;
            _jwtSettings = jwtSettings.Value;
            _tenant = tenant;
            _tenantContext = tenantContext;
            _tenantResolver = tenantResolver;
            _emailSender = emailSender;
            _attemptGuard = attemptGuard;
            _logger = logger;
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
        /// Inicia a recuperação de senha do operador dentro do tenant do Host.
        /// </summary>
        /// <remarks>
        /// Sempre devolve 200 com o mesmo corpo — para e-mail cadastrado ou não —
        /// e com tempo aproximado (<see cref="RecuperacaoSenhaPisoMs"/>), para não
        /// virar oráculo de enumeração de contas. O link enviado usa sempre o
        /// <see cref="ITenantContext.Host"/> da própria requisição: o
        /// <c>TenantMiddleware</c> só resolve o tenant quando esse Host bate com
        /// um <c>WlDominio</c> com <c>Estado == Active</c>, então ele já É o
        /// domínio ativo do tenant — nunca uma URL informada pelo cliente.
        /// </remarks>
        [AllowAnonymous]
        [EnableRateLimiting(Startup.RateLimitRecuperacaoSenha)]
        [HttpPost("esqueci-senha")]
        public async Task<IActionResult> EsqueciSenha([FromBody] EsqueciSenhaRequest request, CancellationToken ct)
        {
            var cronometro = Stopwatch.StartNew();
            var afiliadaId = _tenant.AfiliadaId;
            var normalizedEmail = (request?.Email ?? string.Empty).ToLowerInvariant().Trim();

            try
            {
                // Segunda camada de limite, por hash do e-mail e independente de
                // IP — cobre o atacante distribuído mirando UMA conta.
                if (string.IsNullOrWhiteSpace(normalizedEmail) || !_attemptGuard.PermitirTentativa(afiliadaId, normalizedEmail))
                {
                    return Ok(RespostaRecuperacaoSenha);
                }

                var usuario = await _tenant.UsuariosAfiliada
                    .FirstOrDefaultAsync(u => u.Email.Endereco == normalizedEmail
                                           && u.StatusExibicao == StatusExibicaoEnum.Ativo, ct);

                // Usuário inexistente: nenhum caminho perceptível além do piso de
                // tempo comum ao fim do método — não há token para gerar nem
                // e-mail para enviar.
                if (usuario == null)
                {
                    return Ok(RespostaRecuperacaoSenha);
                }

                var tokenBruto = usuario.GerarTokenRecuperacao();
                await _db.SaveChangesAsync(ct);

                try
                {
                    var branding = await _tenantResolver.ObterBrandingAsync(afiliadaId);
                    var link = MontarLinkRedefinicao(tokenBruto, normalizedEmail);
                    await _emailSender.EnviarRecuperacaoAsync(usuario.Email.Endereco, branding?.NomeExibicao, link, ct);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    // Falha de envio invalida o token: um token que ninguém
                    // recebeu não deve continuar válido por 2h esperando alguém
                    // adivinhar. Log estruturado sem PII — nem e-mail, nem token,
                    // nem link.
                    usuario.InvalidarTokenRecuperacao();
                    await _db.SaveChangesAsync(ct);
                    _logger.LogError(ex,
                        "Falha ao enviar e-mail de recuperação de senha para a afiliada {AfiliadaId}; token invalidado.",
                        afiliadaId);
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
        /// Conclui a recuperação de senha com o token recebido por e-mail.
        /// </summary>
        /// <remarks>
        /// Mesma mensagem genérica para token inválido, expirado, já usado, ou
        /// pertencente a outro tenant: o filtro por
        /// <see cref="ITenantQueries.UsuariosAfiliada"/> já garante que só um
        /// registro da própria afiliada é considerado, então um token de outro
        /// tenant simplesmente não encontra usuário aqui — não é um caso
        /// separado a mais.
        /// </remarks>
        [AllowAnonymous]
        [EnableRateLimiting(Startup.RateLimitRecuperacaoSenha)]
        [HttpPost("alterar-senha")]
        public async Task<IActionResult> AlterarSenha([FromBody] AlterarSenhaRequest request, CancellationToken ct)
        {
            const string erroGenerico = "Link de recuperação inválido ou expirado. Solicite uma nova recuperação de senha.";

            if (string.IsNullOrWhiteSpace(request?.Email) || string.IsNullOrWhiteSpace(request?.Token) || string.IsNullOrWhiteSpace(request?.NovaSenha))
                return BadRequest(new { message = "E-mail, token e nova senha são obrigatórios." });

            if (request.NovaSenha.Trim().Length < SenhaTamanhoMinimo)
                return BadRequest(new { message = $"A senha precisa ter no mínimo {SenhaTamanhoMinimo} caracteres." });

            var normalizedEmail = request.Email.ToLowerInvariant().Trim();

            var usuario = await _tenant.UsuariosAfiliada
                .FirstOrDefaultAsync(u => u.Email.Endereco == normalizedEmail
                                       && u.StatusExibicao == StatusExibicaoEnum.Ativo, ct);

            if (usuario == null || !usuario.ValidarTokenRecuperacao(request.Token.Trim()))
                return BadRequest(new { message = erroGenerico });

            // AlterarSenha já invalida o token de recuperação (WlUsuarioAfiliada),
            // então uma segunda tentativa com o mesmo token cai no ramo acima.
            usuario.AlterarSenha(BC.HashPassword(request.NovaSenha.Trim()));

            if (!usuario.IsValid())
                return BadRequest(usuario.Notifications);

            await _db.SaveChangesAsync(ct);

            return Ok(new { message = "Senha alterada com sucesso. Faça login com a nova senha." });
        }

        /// <summary>
        /// URL de redefinição enviada por e-mail — sempre no domínio ativo do
        /// tenant resolvido pelo Host da própria requisição, nunca informado
        /// pelo cliente.
        /// </summary>
        private string MontarLinkRedefinicao(string tokenBruto, string email)
        {
            var query = $"token={Uri.EscapeDataString(tokenBruto)}&email={Uri.EscapeDataString(email)}";
            return $"https://{_tenantContext.Host}/login/alterar-senha?{query}";
        }

        private static readonly object RespostaRecuperacaoSenha = new
        {
            message = "Se o e-mail informado estiver cadastrado nesta instância, enviaremos instruções para redefinir a senha."
        };

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

    public class EsqueciSenhaRequest
    {
        public string Email { get; set; }
    }

    public class AlterarSenhaRequest
    {
        public string Email { get; set; }
        public string Token { get; set; }
        public string NovaSenha { get; set; }
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
