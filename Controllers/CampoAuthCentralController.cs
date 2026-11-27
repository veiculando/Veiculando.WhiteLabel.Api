using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Veiculando.WhiteLabel.Api.Services;

namespace Veiculando.WhiteLabel.Api.Controllers
{
    /// <summary>
    /// Entrada do app de campo sem informar a exibidora (VEI-RD-181).
    /// </summary>
    /// <remarks>
    /// O app chama estas rotas num Host fixo do build e não sabe ainda qual é a
    /// exibidora do operador. Por isso elas ignoram o tenant do Host e procuram o
    /// e-mail em todas as afiliadas. Nenhuma emite token: o app recebe o domínio da
    /// exibidora e entra por /api/wl/campo/auth/login nele, que segue com o tenant,
    /// o vínculo do token e o registro do login de sempre.
    /// </remarks>
    [ApiController]
    [Route("api/wl/campo/auth/central")]
    public class CampoAuthCentralController : ControllerBase
    {
        /// <summary>Teto de afiliadas conferidas por pedido: cada uma custa um BCrypt.</summary>
        public const int MaximoAfiliadas = 20;

        private const string CredenciaisInvalidas = "Credenciais inválidas.";

        private static readonly object RespostaEmail = new
        {
            message = "Se o e-mail informado estiver cadastrado, enviaremos o caminho."
        };

        private readonly CampoConta _conta;

        public CampoAuthCentralController(CampoConta conta)
        {
            _conta = conta;
        }

        /// <summary>
        /// Exibidoras em que o e-mail e a senha conferem. Quase sempre uma; com mais
        /// de uma o app pede para o operador escolher. Nenhuma: 401 com a mensagem
        /// genérica do login, sem dizer se o e-mail existe.
        /// </summary>
        [AllowAnonymous]
        [EnableRateLimiting(Startup.RateLimitLogin)]
        [HttpPost("exibidoras")]
        public async Task<IActionResult> Exibidoras([FromBody] LoginRequest request, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(request?.Email) || string.IsNullOrWhiteSpace(request?.Senha))
                return BadRequest(new { message = "Email e senha são obrigatórios." });

            var email = CampoConta.Normalizar(request.Email);
            var achadas = new List<CampoExibidoraResposta>();
            foreach (var afiliadaId in (await _conta.AfiliadasDoEmailAsync(email, ct)).Take(MaximoAfiliadas))
            {
                var credencial = await _conta.ConferirAsync(email, request.Senha, afiliadaId);
                if (!credencial.Decisao.Ok)
                    continue;
                var exibidora = await _conta.ExibidoraAsync(afiliadaId, ct);
                if (exibidora != null)
                    achadas.Add(CampoExibidoraResposta.De(exibidora));
            }

            if (achadas.Count == 0)
                return Unauthorized(new { message = CredenciaisInvalidas });

            return Ok(new CampoExibidorasResposta
            {
                Exibidoras = achadas.OrderBy(e => e.NomeExibicao, System.StringComparer.CurrentCultureIgnoreCase).ToList()
            });
        }

        /// <summary>Envia o link de troca de senha para cada exibidora do e-mail, no domínio dela.</summary>
        [AllowAnonymous]
        [EnableRateLimiting(Startup.RateLimitRecuperacaoSenha)]
        [HttpPost("esqueci-senha")]
        public async Task<IActionResult> EsqueciSenha([FromBody] EsqueciSenhaRequest request, CancellationToken ct)
        {
            await ParaCadaExibidora(request?.Email, (email, exibidora) =>
                _conta.EnviarRecuperacaoAsync(email, exibidora.AfiliadaId, exibidora.Host, ct), ct);
            return Ok(RespostaEmail);
        }

        /// <summary>Envia o convite de primeiro acesso para cada exibidora do e-mail, no domínio dela.</summary>
        [AllowAnonymous]
        [EnableRateLimiting(Startup.RateLimitRecuperacaoSenha)]
        [HttpPost("primeiro-acesso")]
        public async Task<IActionResult> PrimeiroAcesso([FromBody] EsqueciSenhaRequest request, CancellationToken ct)
        {
            await ParaCadaExibidora(request?.Email, (email, exibidora) =>
                _conta.EnviarConviteAsync(email, exibidora.AfiliadaId, exibidora.Host, ct), ct);
            return Ok(RespostaEmail);
        }

        private async Task ParaCadaExibidora(string emailInformado, System.Func<string, CampoExibidora, Task> acao, CancellationToken ct)
        {
            var email = CampoConta.Normalizar(emailInformado);
            if (string.IsNullOrWhiteSpace(email))
                return;

            foreach (var afiliadaId in (await _conta.AfiliadasDoEmailAsync(email, ct)).Take(MaximoAfiliadas))
            {
                var exibidora = await _conta.ExibidoraAsync(afiliadaId, ct);
                if (exibidora != null)
                    await acao(email, exibidora);
            }
        }
    }

    public sealed class CampoExibidorasResposta
    {
        public List<CampoExibidoraResposta> Exibidoras { get; set; }
    }

    /// <summary>O app guarda o Host e a marca; nunca recebe o AfiliadaId (ADR-WL-020).</summary>
    public sealed class CampoExibidoraResposta
    {
        public string Host { get; set; }
        public string NomeExibicao { get; set; }
        public string LogoUrl { get; set; }
        public string PrimaryColor { get; set; }

        public static CampoExibidoraResposta De(CampoExibidora e) => new CampoExibidoraResposta
        {
            Host = e.Host,
            NomeExibicao = e.NomeExibicao,
            LogoUrl = e.LogoUrl,
            PrimaryColor = e.PrimaryColor
        };
    }
}
