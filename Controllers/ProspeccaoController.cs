using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities.WhiteLabel;
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
    ///
    /// <para><b>Auditoria (cenário 7).</b> Cada emissão grava um evento em
    /// <c>WL_ProspeccaoSessaoEvento</c>, tabela append-only. O resgate e os pedidos
    /// criados na sessão gravam os seus eventos no mesmo jti, e
    /// <c>GET auditoria</c> junta tudo por sessão. O índice único (Jti, Evento) da
    /// tabela também fecha a limitação acima: um segundo resgate do mesmo jti é
    /// recusado pelo banco, em qualquer réplica.</para>
    /// </remarks>
    [ApiController]
    [Route("api/wl/prospeccao")]
    [Authorize(Policy = AuthorizationSetup.PedidoCriar)]
    public class ProspeccaoController : ControllerBase
    {
        private const int TtlPadraoSegundos = 120;

        private const int LimiteAuditoria = 100;

        private readonly VeiculandoDataContext _db;
        private readonly IWlLinkTemporario _links;
        private readonly ITenantQueries _tenant;
        private readonly IConfiguration _config;
        private readonly ILogger<ProspeccaoController> _logger;

        public ProspeccaoController(
            VeiculandoDataContext db,
            IWlLinkTemporario links,
            ITenantQueries tenant,
            IConfiguration config,
            ILogger<ProspeccaoController> logger)
        {
            _db = db;
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
        /// <para>O log registra quem abriu, quando e para qual cliente — nunca o token.
        /// A trilha consultável é gravada ANTES de o token sair: se a gravação falhar, a
        /// sessão não é emitida. Sessão sem auditoria é exatamente o que o cenário 7 proíbe.</para>
        /// </remarks>
        [HttpPost("sessao")]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public async Task<IActionResult> AbrirSessao([FromBody] ProspeccaoSessaoRequest request, CancellationToken ct)
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

            _db.WlProspeccaoSessaoEventos.Add(WlProspeccaoSessaoEvento.Emitida(
                token.Jti, _tenant.AfiliadaId, operadorId.Value, request?.AnuncianteId,
                token.ExpiraEm.UtcDateTime.Subtract(ttl), token.ExpiraEm.UtcDateTime));
            await _db.SaveChangesAsync(ct);

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

        /// <summary>
        /// Auditoria consultável das sessões de prospecção da exibidora (cenário 7):
        /// quem abriu, quando, para qual cliente, quando foi usada e o que foi criado.
        /// </summary>
        /// <remarks>
        /// Exige também <c>UsuarioAfiliadaGerenciar</c>: a trilha mostra sessões de
        /// TODOS os operadores, então não basta poder abrir uma. Mais recentes primeiro,
        /// limitadas às últimas <see cref="LimiteAuditoria"/> sessões.
        /// </remarks>
        [HttpGet("auditoria")]
        [Authorize(Policy = AuthorizationSetup.UsuarioAfiliadaGerenciar)]
        public async Task<IActionResult> Auditoria(CancellationToken ct)
        {
            var jtis = await _tenant.ProspeccaoEventos
                .Where(e => e.Evento == WlProspeccaoEventoTipo.Emitida)
                .OrderByDescending(e => e.EmitidaEm)
                .Take(LimiteAuditoria)
                .Select(e => e.Jti)
                .ToListAsync(ct);

            var eventos = await _tenant.ProspeccaoEventos
                .Where(e => jtis.Contains(e.Jti))
                .ToListAsync(ct);

            var operadorIds = eventos.Select(e => e.OperadorId).Distinct().ToList();
            var operadores = await _tenant.UsuariosAfiliada
                .Where(u => operadorIds.Contains(u.Id))
                .Select(u => new { u.Id, u.Nome, Email = u.Email.Endereco })
                .ToListAsync(ct);

            var anuncianteIds = eventos.Where(e => e.AnuncianteId.HasValue)
                .Select(e => e.AnuncianteId.Value).Distinct().ToList();
            var anunciantes = await _tenant.UsuariosAnunciante
                .Where(u => anuncianteIds.Contains(u.Id))
                .Select(u => new { u.Id, Email = u.Email.Endereco })
                .ToListAsync(ct);

            var sessoes = eventos
                .GroupBy(e => e.Jti)
                .Select(g =>
                {
                    var emissao = g.First(e => e.Evento == WlProspeccaoEventoTipo.Emitida);
                    var resgate = g.FirstOrDefault(e => e.Evento == WlProspeccaoEventoTipo.Resgatada);
                    var anuncianteId = resgate?.AnuncianteId ?? emissao.AnuncianteId;
                    var operador = operadores.FirstOrDefault(o => o.Id == emissao.OperadorId);

                    return new
                    {
                        Sessao = emissao.Jti,
                        Operador = new { Id = emissao.OperadorId, operador?.Nome, operador?.Email },
                        Anunciante = anuncianteId == null ? null : new
                        {
                            Id = anuncianteId.Value,
                            anunciantes.FirstOrDefault(a => a.Id == anuncianteId.Value)?.Email
                        },
                        emissao.EmitidaEm,
                        emissao.ExpiraEm,
                        UsadaEm = resgate?.OcorridoEm,
                        Pedidos = g.Where(e => e.Evento == WlProspeccaoEventoTipo.PedidoCriado)
                            .OrderBy(e => e.OcorridoEm)
                            .Select(e => new { CriadoEm = e.OcorridoEm, Codigos = LerCodigos(e.CodigosPedido) })
                            .ToList()
                    };
                })
                .OrderByDescending(s => s.EmitidaEm)
                .ToList();

            return Ok(new { itens = sessoes });
        }

        private static IReadOnlyList<string> LerCodigos(string json)
        {
            try { return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>(); }
            catch (System.Text.Json.JsonException) { return new List<string>(); }
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
