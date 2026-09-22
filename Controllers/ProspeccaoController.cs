using System;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Veiculando.WhiteLabel.Api.Configurations;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;

namespace Veiculando.WhiteLabel.Api.Controllers
{
    /// <summary>
    /// Sessão de prospecção — VEI-RD-83. O operador fecha pedidos em nome do cliente:
    /// o BFF emite um session token temporário e o App WL abre já autenticado, com a
    /// origem vinculada ao operador.
    /// </summary>
    /// <remarks>
    /// <para><b>A barreira de audience é a guarda central deste card.</b> O JWT do
    /// painel é validado com <c>ValidateAudience = false</c> (ver
    /// <c>AuthenticationSetup</c>): qualquer token assinado com o segredo do painel é
    /// aceito por ele. Se o token de prospecção fosse um JWT com o mesmo segredo, ele
    /// seria aceito no painel da Exibidora — exatamente o que o card proíbe — e a
    /// proteção dependeria de alguém ligar a validação de audience algum dia.</para>
    ///
    /// <para>Por isso o token não é um JWT do painel: é assinado por
    /// <see cref="IWlLinkTemporario"/> com uma CHAVE DERIVADA do propósito
    /// <c>prospeccao-sessao</c>. O painel não consegue validá-lo nem que queira, porque
    /// a chave é outra. A separação deixa de ser configuração e passa a ser aritmética.</para>
    ///
    /// <para><b>Pendências do Humano — registradas, não arbitradas:</b></para>
    /// <list type="bullet">
    ///   <item>O VALOR do TTL. A copy do Figma diz apenas "O acesso encerra depois de um
    ///   tempo". Aqui ele é configurável (<c>WlProspeccao:TtlSegundos</c>) com um default
    ///   curto de 120s; o número final é decisão do Humano.</item>
    ///   <item>A SELEÇÃO DE CLIENTE. O Figma abre a sessão sem escolher em nome de quem.
    ///   O fluxo implementado é o do Figma; <c>AnuncianteId</c> existe no request como
    ///   ponto de extensão opcional, para que acrescentar um seletor depois não exija
    ///   refazer a emissão.</item>
    ///   <item>O ESCOPO FINO do token dentro do App WL.</item>
    /// </list>
    ///
    /// <para><b>Limitação conhecida do uso único.</b> O <c>jti</c> consumido é guardado
    /// em <see cref="IMemoryCache"/>, que é por INSTÂNCIA. Com o BFF rodando em mais de
    /// uma réplica, um token poderia ser reapresentado na réplica que ainda não o viu.
    /// Um store compartilhado (tabela ou cache distribuído) resolve, e é mudança de
    /// schema — território de outro card. Registrado como pendência, não escondido:
    /// o TTL curto limita a janela, mas não a fecha.</para>
    /// </remarks>
    [ApiController]
    [Route("api/wl/prospeccao")]
    [Authorize(Policy = AuthorizationSetup.PedidoCriar)]
    public class ProspeccaoController : ControllerBase
    {
        private const int TtlPadraoSegundos = 120;

        private readonly IWlLinkTemporario _links;
        private readonly ITenantQueries _tenant;
        private readonly IConfiguration _config;
        private readonly ILogger<ProspeccaoController> _logger;

        public ProspeccaoController(
            IWlLinkTemporario links,
            ITenantQueries tenant,
            IConfiguration config,
            ILogger<ProspeccaoController> logger)
        {
            _links = links;
            _tenant = tenant;
            _config = config;
            _logger = logger;
        }

        private int? WlUsuarioId =>
            int.TryParse(User.FindFirst("WlUsuarioId")?.Value, out var id) ? id : (int?)null;

        /// <summary>
        /// Emite o session token e devolve a URL do App WL. Rate-limited (PRD §7).
        /// </summary>
        /// <remarks>
        /// <para>O token vai no CORPO da resposta, não na URL devolvida. Token em query
        /// string entra no histórico do navegador, no Referer da próxima requisição e
        /// no log de qualquer proxy no caminho — o card é explícito: não persistir na
        /// URL. O App WL recebe a URL e apresenta o token por outro meio.</para>
        ///
        /// <para>O log registra quem abriu, quando e para qual cliente — nunca o token.</para>
        /// </remarks>
        [HttpPost("sessao")]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public IActionResult AbrirSessao([FromBody] ProspeccaoSessaoRequest request)
        {
            var operadorId = WlUsuarioId;
            if (operadorId == null)
                return Unauthorized();

            var ttl = TimeSpan.FromSeconds(
                _config.GetValue<int?>("WlProspeccao:TtlSegundos") ?? TtlPadraoSegundos);

            var appUrl = _config["WlProspeccao:AppUrl"];
            if (string.IsNullOrWhiteSpace(appUrl))
                return StatusCode(503, new { message = "A URL do App WL não está configurada para esta exibidora." });

            // O recurso assinado amarra o token ao OPERADOR que o pediu: um token emitido
            // para um operador não vale para outro, mesmo dentro da mesma exibidora. É o
            // que torna a trilha de origem confiável em vez de declaratória.
            var recurso = $"{operadorId.Value}:{request?.AnuncianteId?.ToString() ?? "-"}";

            var token = _links.Assinar(
                WlLinkTemporario.PropositoProspeccao,
                recurso,
                _tenant.AfiliadaId,
                ttl);

            _logger.LogInformation(
                "WL_PROSPECCAO_SESSAO tenant={Tenant} operador={Operador} anunciante={Anunciante} expiraEm={ExpiraEm}",
                _tenant.AfiliadaId, operadorId.Value, request?.AnuncianteId, token.ExpiraEm);

            return Ok(new
            {
                AppUrl = appUrl,
                Token = token.Token,
                ExpiraEm = token.ExpiraEm,
                TtlSegundos = (int)ttl.TotalSeconds,
                // Trilha de origem (ADR-WL-003): o App WL injeta isto em todo pedido
                // criado dentro da sessão, para que ele seja rastreável até quem abriu.
                FonteOrigem = "WhiteLabel",
                FonteAgenciaId = _tenant.AfiliadaId,
                FonteUsuarioId = operadorId.Value
            });
        }
    }

    public sealed class ProspeccaoSessaoRequest
    {
        /// <summary>
        /// Ponto de extensão, opcional. O Figma abre a sessão sem escolher em nome de
        /// quem; se um seletor de Anunciante for adicionado depois, ele preenche isto
        /// sem que a emissão precise mudar.
        /// </summary>
        public int? AnuncianteId { get; set; }
    }
}
