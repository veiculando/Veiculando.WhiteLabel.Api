using System;
using System.Data.Entity;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities;
using Veiculando.WhiteLabel.Api.Configurations;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;

namespace Veiculando.WhiteLabel.Api.Controllers
{
    /// <summary>
    /// Configurações → Cadastro e acesso — VEI-RD-82, frame 287:12843.
    /// </summary>
    /// <remarks>
    /// <para><b>Permissão.</b> <c>UsuarioAfiliadaGerenciar</c>: é a permissão de
    /// administração da exibidora, e o que esta tela governa é quem consegue entrar.
    /// A lista canônica (<c>WlPermissoesValidas</c>) não prevê uma
    /// <c>ConfiguracaoGerenciar</c>, e criá-la exigiria migration de claims — escopo
    /// de outro card.</para>
    ///
    /// <para><b>Convites NÃO entram aqui.</b> O plano original previa gestão e
    /// listagem de convites nesta tela; o frame 287:12843 contém apenas a política de
    /// e-mail corporativo. O requisito do PRD §5.14 continua válido, mas sem tela
    /// desenhada — pendência de design registrada em VEI-RD-82 task d. Os convites por
    /// organização já são cobertos pela aba 5 do detalhe de KYC (VEI-RD-81).</para>
    /// </remarks>
    [ApiController]
    [Route("api/wl/config/cadastro-acesso")]
    [Authorize(Policy = AuthorizationSetup.UsuarioAfiliadaGerenciar)]
    public class ConfigCadastroAcessoController : ControllerBase
    {
        private readonly VeiculandoDataContext _db;
        private readonly ITenantQueries _tenant;
        private readonly IWlPoliticaEmailCorporativo _politica;

        public ConfigCadastroAcessoController(
            VeiculandoDataContext db,
            ITenantQueries tenant,
            IWlPoliticaEmailCorporativo politica)
        {
            _db = db;
            _tenant = tenant;
            _politica = politica;
        }

        private int? WlUsuarioId =>
            int.TryParse(User.FindFirst("WlUsuarioId")?.Value, out var id) ? id : (int?)null;

        /// <summary>
        /// Estado da política, a lista de domínios bloqueados e o histórico de alterações.
        /// </summary>
        /// <remarks>
        /// A lista vem do servidor porque é o servidor que barra. O frontend apenas
        /// exibe — não há endpoint para editá-la nesta versão, e a tela não desenha
        /// controle de edição (invariante #8: o proibido some do DOM).
        /// </remarks>
        [HttpGet]
        public async Task<IActionResult> Obter(CancellationToken ct)
        {
            var exige = await _politica.ExigeEmailCorporativoAsync(_tenant.AfiliadaId, ct);

            var historico = await _tenant.ConfiguracaoHistorico.AsNoTracking()
                .Where(h => h.Campo == WlPoliticaEmailCorporativo.CampoExigirEmailCorporativo)
                .OrderByDescending(h => h.DataHora)
                .Take(50)
                .Select(h => new
                {
                    h.Id,
                    h.ValorAnterior,
                    h.ValorNovo,
                    h.DataHora,
                    Usuario = h.Usuario == null ? null : h.Usuario.Nome
                })
                .ToListAsync(ct);

            return Ok(new
            {
                ExigirEmailCorporativoNoCadastro = exige,
                DominiosBloqueados = _politica.DominiosBloqueados,
                DominiosEditaveis = false,
                Historico = historico
            });
        }

        /// <summary>
        /// Atualiza a política. Auditado: valor anterior, valor novo, operador e
        /// timestamp, numa linha nova de <c>AfiliadaConfiguracaoHistorico</c>.
        /// </summary>
        /// <remarks>
        /// <para>Append-only: nenhuma linha anterior é alterada ou apagada. É o que
        /// permite a tela mostrar "Inativo → Ativo · Rafael Andrade · 01/08/2026 14:12"
        /// e responder quem ligou a regra e quando.</para>
        ///
        /// <para>Salvar com o mesmo valor não grava histórico. Uma linha "Ativo → Ativo"
        /// não registra mudança nenhuma e só serviria para afogar o histórico real —
        /// o operador que abre e fecha a tela não deve aparecer na auditoria.</para>
        /// </remarks>
        [HttpPut]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public async Task<IActionResult> Atualizar(
            [FromBody] CadastroAcessoRequest request, CancellationToken ct)
        {
            if (request == null)
                return BadRequest(new { message = "Informe o valor da política." });

            var operadorId = WlUsuarioId;
            if (operadorId == null)
                return Unauthorized();

            var usuario = await _db.Usuarios.FirstOrDefaultAsync(u => u.Id == operadorId.Value, ct);
            if (usuario == null)
                return Unauthorized();

            var afiliada = await _db.Afiliadas.FirstOrDefaultAsync(a => a.Id == _tenant.AfiliadaId, ct);
            if (afiliada == null)
                return NotFound(new { message = "Exibidora não encontrada." });

            using var transacao = _db.Database.BeginTransaction();
            try
            {
                var config = await _db.AfiliadaConfiguracoes
                    .FirstOrDefaultAsync(c => c.IdAfiliada == _tenant.AfiliadaId, ct);

                if (config == null)
                {
                    config = new AfiliadaConfiguracao(afiliada, usuario);
                    if (!config.IsValid())
                    {
                        transacao.Rollback();
                        return BadRequest(new { message = "Não foi possível criar a configuração da exibidora." });
                    }
                    _db.AfiliadaConfiguracoes.Add(config);
                }

                var anterior = config.ExigirEmailCorporativoNoCadastro;
                var novo = request.ExigirEmailCorporativoNoCadastro;

                if (anterior != novo)
                {
                    config.AlterarExigirEmailCorporativo(novo);

                    var registro = new AfiliadaConfiguracaoHistorico(
                        afiliada,
                        WlPoliticaEmailCorporativo.CampoExigirEmailCorporativo,
                        anterior ? "Ativo" : "Inativo",
                        novo ? "Ativo" : "Inativo",
                        usuario);

                    if (!registro.IsValid())
                    {
                        transacao.Rollback();
                        return BadRequest(new { message = "Não foi possível registrar a alteração." });
                    }

                    _db.AfiliadaConfiguracaoHistoricos.Add(registro);
                }

                await _db.SaveChangesAsync(ct);
                transacao.Commit();

                return Ok(new
                {
                    ExigirEmailCorporativoNoCadastro = novo,
                    DominiosBloqueados = _politica.DominiosBloqueados
                });
            }
            catch
            {
                transacao.Rollback();
                throw;
            }
        }
    }

    public sealed class CadastroAcessoRequest
    {
        public bool ExigirEmailCorporativoNoCadastro { get; set; }
    }
}
