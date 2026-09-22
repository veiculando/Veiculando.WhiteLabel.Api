using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Veiculando.Data.Contexts;

namespace Veiculando.WhiteLabel.Api.Services
{
    /// <summary>
    /// Política de e-mail corporativo no cadastro público — VEI-RD-82 (PRD §5.14).
    /// </summary>
    /// <remarks>
    /// <para><b>A política é aplicada no BFF, não no Angular</b> (PRD §6.3). O critério
    /// §8.16 é explícito: com a config ativa, o servidor rejeita provedor público
    /// MESMO que o frontend seja contornado. O teste de aceite chama a API direto.</para>
    ///
    /// <para><b>Uma lista, um dono.</b> Os domínios estavam escritos inline no
    /// <c>AppRegistrationController</c> e a tela de configuração precisa exibir a mesma
    /// lista. Duas cópias divergem: a tela mostraria seis domínios enquanto o servidor
    /// barraria outros cinco, e ninguém notaria até um cadastro legítimo ser recusado.
    /// A lista vive aqui, o servidor barra por ela e a tela a recebe do
    /// <c>GET /api/wl/config/cadastro-acesso</c> — o frontend nunca a define.</para>
    ///
    /// <para><b>Seis domínios, não quatro.</b> O PRD §5.14 cita <c>gmail.com</c>,
    /// <c>hotmail.com</c>, <c>outlook.com</c> e <c>yahoo.com</c>; o frame 287:12843
    /// acrescenta <c>live.com</c> e <c>icloud.com</c>. Os seis já estavam no código do
    /// cadastro do app antes deste card — o Figma documentou o que o servidor fazia.</para>
    /// </remarks>
    public interface IWlPoliticaEmailCorporativo
    {
        IReadOnlyList<string> DominiosBloqueados { get; }

        /// <summary>
        /// Diz se o e-mail é aceito no cadastro público desta afiliada. Config inativa
        /// aceita qualquer domínio — é o default de afiliada nova, para não passar a
        /// recusar cadastros que funcionavam antes do deploy.
        /// </summary>
        Task<bool> PermiteAsync(int afiliadaId, string email, CancellationToken ct);

        /// <summary>Estado atual da exigência para a afiliada.</summary>
        Task<bool> ExigeEmailCorporativoAsync(int afiliadaId, CancellationToken ct);

        /// <summary>Só o teste de domínio, sem consultar o banco.</summary>
        bool DominioBloqueado(string email);
    }

    public sealed class WlPoliticaEmailCorporativo : IWlPoliticaEmailCorporativo
    {
        /// <summary>Campo auditado em <c>AfiliadaConfiguracaoHistorico</c>.</summary>
        public const string CampoExigirEmailCorporativo = "ExigirEmailCorporativoNoCadastro";

        private static readonly string[] Dominios =
        {
            "gmail.com", "hotmail.com", "outlook.com", "yahoo.com", "live.com", "icloud.com"
        };

        private readonly VeiculandoDataContext _db;

        public WlPoliticaEmailCorporativo(VeiculandoDataContext db) => _db = db;

        public IReadOnlyList<string> DominiosBloqueados => Dominios;

        public async Task<bool> ExigeEmailCorporativoAsync(int afiliadaId, CancellationToken ct)
        {
            var config = await _db.AfiliadaConfiguracoes.AsNoTracking()
                .FirstOrDefaultAsync(c => c.IdAfiliada == afiliadaId, ct);

            // Afiliada sem linha de configuração ainda não optou por nada, e o default
            // da entidade é false. Tratar a ausência como "exige" transformaria o deploy
            // numa mudança de comportamento para todo mundo de uma vez.
            return config?.ExigirEmailCorporativoNoCadastro ?? false;
        }

        public async Task<bool> PermiteAsync(int afiliadaId, string email, CancellationToken ct)
        {
            if (!await ExigeEmailCorporativoAsync(afiliadaId, ct))
                return true;

            return !DominioBloqueado(email);
        }

        public bool DominioBloqueado(string email)
        {
            if (string.IsNullOrWhiteSpace(email)) return false;

            // Normaliza antes de comparar: "  Fulano@GMAIL.COM " tem de bater com
            // "gmail.com", senão a guarda é contornável só com uma maiúscula.
            var partes = email.Trim().ToLowerInvariant().Split('@');
            if (partes.Length != 2) return false;

            var dominio = partes[1].Trim();
            return Dominios.Contains(dominio, StringComparer.Ordinal);
        }
    }
}
