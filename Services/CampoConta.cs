using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities;
using Veiculando.Domain.Entities.WhiteLabel;
using Veiculando.Domain.Enums;
using Veiculando.WhiteLabel.Api.Middleware;
using BC = BCrypt.Net.BCrypt;

namespace Veiculando.WhiteLabel.Api.Services
{
    /// <summary>Resultado da conferência de e-mail e senha de campo numa afiliada.</summary>
    public sealed class CampoCredencial
    {
        public CampoLoginDecisao Decisao { get; set; }
        public WLUsuario_Operador Wl { get; set; }
        public Usuario_Operador Core { get; set; }
    }

    /// <summary>Exibidora em que o operador de campo pode entrar (VEI-RD-181).</summary>
    public sealed class CampoExibidora
    {
        public int AfiliadaId { get; set; }
        public string Host { get; set; }
        public string NomeExibicao { get; set; }
        public string LogoUrl { get; set; }
        public string PrimaryColor { get; set; }
    }

    /// <summary>
    /// Conta do operador de campo: conferência de senha e pedidos por e-mail numa
    /// afiliada. O login por Host (CampoAuthController) e o login central
    /// (CampoAuthCentralController, VEI-RD-181) usam as mesmas regras.
    /// </summary>
    public sealed class CampoConta
    {
        private readonly VeiculandoDataContext _db;
        private readonly IWlTenantResolver _tenantResolver;
        private readonly WlPublicLinks _links;
        private readonly IWlPasswordEmailSender _email;
        private readonly IPasswordResetAttemptGuard _tentativas;
        private readonly ILogger<CampoConta> _logger;

        public CampoConta(
            VeiculandoDataContext db,
            IWlTenantResolver tenantResolver,
            WlPublicLinks links,
            IWlPasswordEmailSender email,
            IPasswordResetAttemptGuard tentativas,
            ILogger<CampoConta> logger)
        {
            _db = db;
            _tenantResolver = tenantResolver;
            _links = links;
            _email = email;
            _tentativas = tentativas;
            _logger = logger;
        }

        public static string Normalizar(string email) => (email ?? string.Empty).ToLowerInvariant().Trim();

        /// <summary>
        /// Se existe WLUsuario_Operador do e-mail nesta afiliada, a tentativa termina nele.
        /// Senão, autentica o Usuario_Operador do Core (CampoLoginDecisao.Resolver).
        /// </summary>
        public async Task<CampoCredencial> ConferirAsync(string email, string senha, int afiliadaId)
        {
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
                && BC.Verify(senha, wl.SenhaHash);
            var senhaCore = core != null && SenhaUsuarioCore.Confere(senha, core.Senha);

            var decisao = CampoLoginDecisao.Resolver(
                wl != null,
                senhaWl,
                wl?.Usuario_OperadorId,
                wl?.Id,
                core != null,
                senhaCore,
                core?.Id ?? 0);

            return new CampoCredencial { Decisao = decisao, Wl = wl, Core = core };
        }

        /// <summary>Afiliadas com operador de campo ativo neste e-mail (WhiteLabel ou Core).</summary>
        public async Task<List<int>> AfiliadasDoEmailAsync(string email, CancellationToken ct = default)
        {
            var wl = await _db.WlUsuariosOperador
                .Where(u => u.Email.Endereco == email && u.StatusExibicao == StatusExibicaoEnum.Ativo)
                .Select(u => u.AfiliadaId)
                .ToListAsync(ct);
            var core = await _db.UsuariosOperador
                .Where(u => u.Email.Endereco == email
                    && u.StatusExibicao == StatusExibicaoEnum.Ativo
                    && u.EmailConfirmado
                    && u.StatusAprovacao == StatusUsuarioEnum.Aprovado)
                .Select(u => u.IdAfiliada)
                .ToListAsync(ct);
            return wl.Concat(core).Distinct().OrderBy(id => id).ToList();
        }

        /// <summary>
        /// Domínio ativo da exibidora e a marca dela. Prefere o domínio do painel
        /// (Tipo Painel); sem domínio ativo a afiliada não tem como receber o app.
        /// </summary>
        public async Task<CampoExibidora> ExibidoraAsync(int afiliadaId, CancellationToken ct = default)
        {
            var hosts = await _db.WlDominios
                .AsNoTracking()
                .Where(d => d.AfiliadaId == afiliadaId
                    && d.Estado == WlDominioEstadoEnum.Active
                    && d.StatusExibicao == StatusExibicaoEnum.Ativo)
                .Select(d => new { d.Host, d.Tipo })
                .ToListAsync(ct);
            var host = hosts
                .OrderBy(d => d.Tipo == WlDominioTipoEnum.Painel ? 0 : 1)
                .ThenBy(d => d.Host, StringComparer.Ordinal)
                .Select(d => d.Host)
                .FirstOrDefault();
            if (host == null)
                return null;

            var marca = await _tenantResolver.ObterBrandingAsync(afiliadaId);
            return new CampoExibidora
            {
                AfiliadaId = afiliadaId,
                Host = host,
                NomeExibicao = string.IsNullOrWhiteSpace(marca?.NomeExibicao) ? host : marca.NomeExibicao,
                LogoUrl = marca?.LogoUrl,
                PrimaryColor = marca?.PrimaryColor
            };
        }

        /// <summary>Envia o link de troca de senha no domínio informado. Silencioso quando não se aplica.</summary>
        public async Task EnviarRecuperacaoAsync(string email, int afiliadaId, string host, CancellationToken ct)
        {
            if (!_tentativas.PermitirTentativa(afiliadaId, email))
                return;

            var usuario = await _db.WlUsuariosOperador.FirstOrDefaultAsync(u =>
                u.Email.Endereco == email
                && u.AfiliadaId == afiliadaId
                && u.StatusExibicao == StatusExibicaoEnum.Ativo
                && u.StatusConvite == StatusConviteWlEnum.Aceito, ct);
            if (usuario == null)
                return;

            var token = usuario.GerarTokenRecuperacao();
            await _db.SaveChangesAsync(ct);
            try
            {
                var marca = (await _tenantResolver.ObterBrandingAsync(afiliadaId))?.NomeExibicao;
                await _email.EnviarRecuperacaoAsync(usuario.Email.Endereco, marca, _links.CampoRecuperacao(host, token, email), ct);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                usuario.InvalidarTokenRecuperacao();
                await _db.SaveChangesAsync(ct);
                _logger.LogError(ex, "Falha ao enviar esqueci senha de campo para a afiliada {AfiliadaId}.", afiliadaId);
            }
        }

        /// <summary>Envia o convite de primeiro acesso no domínio informado. Silencioso quando não se aplica.</summary>
        public async Task EnviarConviteAsync(string email, int afiliadaId, string host, CancellationToken ct)
        {
            if (!_tentativas.PermitirTentativa(afiliadaId, email))
                return;

            var usuario = await _db.WlUsuariosOperador.FirstOrDefaultAsync(u =>
                u.Email.Endereco == email
                && u.AfiliadaId == afiliadaId
                && u.StatusExibicao == StatusExibicaoEnum.Ativo
                && u.StatusConvite == StatusConviteWlEnum.Pendente, ct);
            if (usuario == null)
                return;

            string token;
            try
            {
                token = usuario.GerarTokenConvite();
            }
            catch (InvalidOperationException)
            {
                return;
            }

            await _db.SaveChangesAsync(ct);
            try
            {
                var marca = (await _tenantResolver.ObterBrandingAsync(afiliadaId))?.NomeExibicao;
                await _email.EnviarConviteAsync(usuario.Email.Endereco, marca, _links.CampoPrimeiroAcesso(host, token, email), ct);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.LogError(ex, "Falha ao enviar primeiro acesso de campo para a afiliada {AfiliadaId}.", afiliadaId);
            }
        }
    }
}
