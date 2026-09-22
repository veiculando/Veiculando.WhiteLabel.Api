using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Veiculando.Infra.Security;

namespace Veiculando.WhiteLabel.Api.Services
{
    /// <summary>
    /// Assina referências de curta duração — a URL temporária de documento de KYC
    /// (VEI-RD-81) e o session token de prospecção (VEI-RD-83).
    /// </summary>
    /// <remarks>
    /// <para><b>Por que não SAS do Azure.</b> O container de uploads é privado e
    /// <see cref="AzureWlUploadStorage"/> nunca devolve SAS nem chave à UI — de
    /// propósito. Emitir uma SAS aqui abriria um caminho para o blob que não passa
    /// pelo BFF, e portanto não passa pela barreira de afiliada. O que se assina é
    /// o direito de pedir o arquivo AO BFF; o blob continua inalcançável de fora.</para>
    ///
    /// <para><b>Por que uma chave derivada por propósito.</b> O JWT do painel é
    /// validado com <c>ValidateAudience = false</c> (AuthenticationSetup): qualquer
    /// token assinado com o segredo do painel é aceito por ele. Se o token de
    /// prospecção usasse esse mesmo segredo, a barreira de audience de VEI-RD-83
    /// dependeria inteiramente de alguém lembrar de ligar a validação de audience —
    /// e um token de prospecção reapresentado no painel passaria. Derivando uma chave
    /// por propósito (HMAC do segredo com o rótulo), a separação deixa de ser uma
    /// configuração e passa a ser aritmética: o painel não consegue validar um token
    /// de prospecção nem que queira, porque não foi assinado com a chave dele.</para>
    ///
    /// <para><b>O que o token NÃO carrega.</b> Nada sensível: só propósito, recurso,
    /// afiliada, expiração e um nonce. O conteúdo do documento e a chave do blob nunca
    /// entram no token nem em log (PRD §7).</para>
    /// </remarks>
    public interface IWlLinkTemporario
    {
        /// <summary>Assina um token de curta duração e devolve também o instante de expiração.</summary>
        WlTokenTemporario Assinar(string proposito, string recurso, int afiliadaId, TimeSpan ttl);

        /// <summary>
        /// Valida assinatura, propósito, recurso, afiliada e expiração. Devolve o
        /// <c>jti</c> (nonce) para quem precisa de uso único, ou <c>null</c> se inválido.
        /// </summary>
        string Validar(string token, string proposito, string recurso, int afiliadaId);
    }

    public sealed record WlTokenTemporario(string Token, DateTimeOffset ExpiraEm, string Jti);

    public sealed class WlLinkTemporario : IWlLinkTemporario
    {
        /// <summary>Documento de KYC — VEI-RD-81 task d.</summary>
        public const string PropositoDocumentoKyc = "kyc-documento";

        /// <summary>Sessão de prospecção — VEI-RD-83. Audience própria, distinta do painel.</summary>
        public const string PropositoProspeccao = "prospeccao-sessao";

        private readonly byte[] _segredo;

        public WlLinkTemporario(IOptions<JwtSettings> jwtSettings)
        {
            var secret = jwtSettings?.Value?.Secret;
            if (string.IsNullOrWhiteSpace(secret))
                throw new InvalidOperationException("JwtSettings:Secret não configurado.");

            _segredo = Encoding.UTF8.GetBytes(secret);
        }

        public WlTokenTemporario Assinar(string proposito, string recurso, int afiliadaId, TimeSpan ttl)
        {
            if (string.IsNullOrWhiteSpace(proposito)) throw new ArgumentNullException(nameof(proposito));
            if (ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ttl));

            var expiraEm = DateTimeOffset.UtcNow.Add(ttl);
            var jti = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            var payload = Payload(proposito, recurso, afiliadaId, expiraEm.ToUnixTimeSeconds(), jti);
            var assinatura = Base64Url(Assinatura(proposito, payload));

            return new WlTokenTemporario($"{Base64Url(Encoding.UTF8.GetBytes(payload))}.{assinatura}", expiraEm, jti);
        }

        public string Validar(string token, string proposito, string recurso, int afiliadaId)
        {
            if (string.IsNullOrWhiteSpace(token)) return null;

            var partes = token.Split('.');
            if (partes.Length != 2) return null;

            string payload;
            byte[] assinaturaRecebida;
            try
            {
                payload = Encoding.UTF8.GetString(DeBase64Url(partes[0]));
                assinaturaRecebida = DeBase64Url(partes[1]);
            }
            catch (FormatException)
            {
                return null;
            }

            // Comparação em tempo constante: comparar assinaturas com == vaza, pelo
            // tempo de resposta, quantos bytes iniciais o atacante acertou.
            if (!CryptographicOperations.FixedTimeEquals(assinaturaRecebida, Assinatura(proposito, payload)))
                return null;

            var campos = payload.Split('|');
            if (campos.Length != 5) return null;

            if (!string.Equals(campos[0], proposito, StringComparison.Ordinal)) return null;
            if (!string.Equals(campos[1], recurso ?? string.Empty, StringComparison.Ordinal)) return null;
            if (!int.TryParse(campos[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tenant)
                || tenant != afiliadaId) return null;
            if (!long.TryParse(campos[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var exp)) return null;
            if (DateTimeOffset.FromUnixTimeSeconds(exp) <= DateTimeOffset.UtcNow) return null;

            return campos[4];
        }

        private static string Payload(string proposito, string recurso, int afiliadaId, long exp, string jti) =>
            string.Join('|', proposito, recurso ?? string.Empty,
                afiliadaId.ToString(CultureInfo.InvariantCulture),
                exp.ToString(CultureInfo.InvariantCulture), jti);

        private byte[] Assinatura(string proposito, string payload)
        {
            using var hmac = new HMACSHA256(ChaveDoProposito(proposito));
            return hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        }

        // A chave de cada propósito é derivada do segredo base. Dois propósitos nunca
        // compartilham chave, então um token não é reaproveitável fora do seu contexto.
        private byte[] ChaveDoProposito(string proposito)
        {
            using var hmac = new HMACSHA256(_segredo);
            return hmac.ComputeHash(Encoding.UTF8.GetBytes($"veiculando-wl/{proposito}"));
        }

        private static string Base64Url(byte[] dados) =>
            Convert.ToBase64String(dados).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static byte[] DeBase64Url(string valor)
        {
            var normalizado = valor.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(normalizado.PadRight(
                normalizado.Length + (4 - normalizado.Length % 4) % 4, '='));
        }
    }
}
