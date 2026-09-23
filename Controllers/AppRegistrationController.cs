using System;
using System.ComponentModel.DataAnnotations;
using System.Data.Entity;
using System.Data.Entity.Infrastructure;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Veiculando.Domain.Entities.WhiteLabel;
using Veiculando.Domain.Enums;
using Veiculando.WhiteLabel.Api.Services;
using BC = BCrypt.Net.BCrypt;

namespace Veiculando.WhiteLabel.Api.Controllers;

public sealed partial class AppAuthController
{
    private string TermsVersion => _configuration["WlApp:TermsVersion"] ?? "1.0";
    private string PrivacyVersion => _configuration["WlApp:PrivacyVersion"] ?? "1.0";
    // VEI-RD-82 task b: a exigencia passou a vir de AfiliadaConfiguracao, no banco,
    // e nao mais de appsettings. Antes era configuracao de deploy: ligar a regra para
    // uma exibidora exigia editar arquivo e reiniciar o BFF, e a tela de Configuracoes
    // nao tinha como refletir nem auditar o estado. A lista de dominios tambem saiu
    // daqui (estava escrita inline logo abaixo) e vive em WlPoliticaEmailCorporativo,
    // que e a mesma fonte que a tela exibe - duas copias divergiriam em silencio.
    private Task<bool> RequireCorporateEmailAsync(CancellationToken ct) =>
        _politicaEmail.ExigeEmailCorporativoAsync(_tenant.AfiliadaId, ct);
    private static readonly object RegistrationResponse = new { message = "Se o cadastro puder prosseguir, enviaremos um código de confirmação para o e-mail informado." };

    [AllowAnonymous]
    [HttpGet("policy")]
    public async Task<IActionResult> Policy(CancellationToken ct) => Ok(new { termsVersion = TermsVersion, privacyVersion = PrivacyVersion,
        requireCorporateEmail = await RequireCorporateEmailAsync(ct), blockedDomains = _politicaEmail.DominiosBloqueados });

    [AllowAnonymous]
    [EnableRateLimiting(Startup.RateLimitRecuperacaoSenha)]
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegistrationRequest request, CancellationToken ct)
    {
        if (!request.AcceptedTerms || request.TermsVersion != TermsVersion || request.PrivacyVersion != PrivacyVersion)
            return BadRequest(new { message = "Aceite as versões vigentes dos termos e da privacidade." });
        var phone = new string((request.Phone ?? "").Where(ch => ch >= '0' && ch <= '9').ToArray());
        if (phone.Length < 10 || phone.Length > 13 || request.Password.Length < 8 || System.Text.Encoding.UTF8.GetByteCount(request.Password) > 72 ||
            !request.Password.Any(char.IsLetter) || !request.Password.Any(char.IsDigit))
            return BadRequest(new { message = "Informe telefone válido e senha de 8 a 72 caracteres, com letras e números." });
        var email = request.Email.Trim().ToLowerInvariant();
        // Barreira do SERVIDOR (PRD 6.3 / criterio 8.16): vale mesmo chamando a API
        // direto, sem passar pelo frontend. A normalizacao do dominio esta dentro
        // da politica, para "Fulano@GMAIL.COM" nao escapar por uma maiuscula.
        if (!await _politicaEmail.PermiteAsync(_tenant.AfiliadaId, email, ct))
            return BadRequest(new { message = "Esta marca exige um e-mail corporativo." });
        if (!_attemptGuard.PermitirTentativa(_tenant.AfiliadaId, "register|" + email)) return Ok(RegistrationResponse);
        // O índice único é a última barreira contra dois cadastros simultâneos.
        if (await _tenant.Usuarios.AnyAsync(u => u.Email.Endereco == email, ct)) return Ok(RegistrationResponse);
        var user = new WlUsuarioAnunciante(email, BC.HashPassword(request.Password), _tenant.AfiliadaId);
        var identity = new WlAppIdentidade(user, request.Name.Trim(), phone, TermsVersion, PrivacyVersion);
        var code = identity.GerarCodigo();
        _db.WlUsuariosAnunciante.Add(user); _db.WlAppIdentidades.Add(identity);
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException error) when (DuplicateEmail(error)) { return Ok(RegistrationResponse); }
        if (!await EnviarCodigoAsync(identity, code, ct))
            return StatusCode(503, new { message = "Não foi possível enviar o código agora. Tente reenviar em instantes." });
        _logger.LogInformation("WL_APP_REGISTER tenant={Tenant} actor={Actor}", _tenant.AfiliadaId, user.Id);
        return Ok(RegistrationResponse);
    }

    [AllowAnonymous]
    [EnableRateLimiting(Startup.RateLimitRecuperacaoSenha)]
    [HttpPost("confirm-email")]
    public async Task<IActionResult> ConfirmEmail([FromBody] ConfirmationRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var identity = await _db.WlAppIdentidades.Include(i => i.Usuario).SingleOrDefaultAsync(i => i.Usuario.AfiliadaId == _tenant.AfiliadaId &&
            i.Usuario.Email.Endereco == email && i.Usuario.StatusExibicao == StatusExibicaoEnum.Ativo, ct);
        var confirmed = identity != null && identity.ConfirmarEmail(request.Code);
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { confirmed = false; }
        if (!confirmed) return BadRequest(new { message = "Código inválido ou expirado. Solicite novo envio." });
        _logger.LogInformation("WL_APP_EMAIL_CONFIRMED tenant={Tenant} actor={Actor}", _tenant.AfiliadaId, identity.Id);
        return Ok(await EmitirSessaoAsync(identity.Usuario));
    }

    [AllowAnonymous]
    [EnableRateLimiting(Startup.RateLimitRecuperacaoSenha)]
    [HttpPost("resend-confirmation")]
    public async Task<IActionResult> ResendConfirmation([FromBody] EmailRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        if (!_attemptGuard.PermitirTentativa(_tenant.AfiliadaId, "resend|" + email)) return Ok(RegistrationResponse);
        var identity = await _db.WlAppIdentidades.Include(i => i.Usuario).SingleOrDefaultAsync(i => i.Usuario.AfiliadaId == _tenant.AfiliadaId &&
            i.Usuario.Email.Endereco == email && i.Usuario.StatusExibicao == StatusExibicaoEnum.Ativo, ct);
        if (identity?.PodeReenviar == true)
        {
            var code = identity.GerarCodigo();
            try { await _db.SaveChangesAsync(ct); await EnviarCodigoAsync(identity, code, ct); }
            catch (DbUpdateConcurrencyException) { }
        }
        return Ok(RegistrationResponse);
    }

    private async Task<bool> EnviarCodigoAsync(WlAppIdentidade identity, string code, CancellationToken ct)
    {
        try
        {
            var brand = await _tenantResolver.ObterBrandingAsync(_tenant.AfiliadaId);
            await _appEmailSender.ConfirmacaoAsync(identity.Usuario.Email.Endereco, brand?.NomeExibicao ?? "Veiculando", code, ct);
            return true;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            identity.InvalidarCodigo(); await _db.SaveChangesAsync(ct);
            _logger.LogError(error, "WL_APP_EMAIL_SEND_FAILED tenant={Tenant} actor={Actor}", _tenant.AfiliadaId, identity.Id);
            return false;
        }
    }

    private static bool DuplicateEmail(DbUpdateException error)
    {
        for (Exception cause = error; cause != null; cause = cause.InnerException)
            if (cause is System.Data.SqlClient.SqlException sql && (sql.Number == 2601 || sql.Number == 2627)) return true;
        return false;
    }

    public sealed class RegistrationRequest
    {
        [Required, StringLength(200, MinimumLength = 2)] public string Name { get; set; }
        [Required, EmailAddress, StringLength(254)] public string Email { get; set; }
        [Required, StringLength(72, MinimumLength = 8)] public string Password { get; set; }
        [Required, StringLength(30)] public string Phone { get; set; }
        public bool AcceptedTerms { get; set; }
        [Required, StringLength(50)] public string TermsVersion { get; set; }
        [Required, StringLength(50)] public string PrivacyVersion { get; set; }
    }
    public sealed class EmailRequest { [Required, EmailAddress, StringLength(254)] public string Email { get; set; } }
    public sealed class ConfirmationRequest
    {
        [Required, EmailAddress, StringLength(254)] public string Email { get; set; }
        [Required, RegularExpression("^[0-9]{6}$")] public string Code { get; set; }
    }
}
