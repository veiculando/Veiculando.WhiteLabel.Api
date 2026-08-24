using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;

namespace Veiculando.WhiteLabel.Api.Middleware
{
    /// <summary>
    /// Segunda camada de limite para o fluxo de recuperação de senha,
    /// independente de IP.
    /// </summary>
    /// <remarks>
    /// O rate limit nativo do ASP.NET Core (<c>Startup.RateLimitRecuperacaoSenha</c>)
    /// particiona por Host+IP: protege contra uma máquina batendo o endpoint, mas
    /// não contra um atacante distribuído mirando UM e-mail específico a partir de
    /// IPs diferentes. Esta guarda soma essa segunda dimensão, particionada pelo
    /// HASH do e-mail — nunca o e-mail em texto puro — dentro do tenant.
    /// </remarks>
    public interface IPasswordResetAttemptGuard
    {
        /// <summary>
        /// Registra uma tentativa para (afiliada, e-mail) e devolve <c>false</c>
        /// quando o limite da janela já foi atingido.
        /// </summary>
        bool PermitirTentativa(int afiliadaId, string email);
    }

    public sealed class PasswordResetAttemptGuard : IPasswordResetAttemptGuard
    {
        private const int LimitePorJanela = 3;
        private static readonly TimeSpan Janela = TimeSpan.FromMinutes(15);

        private readonly IMemoryCache _cache;

        public PasswordResetAttemptGuard(IMemoryCache cache)
        {
            _cache = cache;
        }

        public bool PermitirTentativa(int afiliadaId, string email)
        {
            var chave = ChaveDe(afiliadaId, email);

            var contador = _cache.GetOrCreate(chave, entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = Janela;
                return new Contador();
            });

            lock (contador)
            {
                if (contador.Valor >= LimitePorJanela) return false;
                contador.Valor++;
                return true;
            }
        }

        /// <summary>
        /// SHA-256 de afiliada+e-mail normalizado — a chave de cache nunca guarda
        /// o e-mail em texto puro.
        /// </summary>
        private static string ChaveDe(int afiliadaId, string email)
        {
            var normalizado = $"{afiliadaId}:{(email ?? string.Empty).Trim().ToLowerInvariant()}";
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(normalizado));
            return "PwReset:" + Convert.ToHexString(hash);
        }

        private sealed class Contador
        {
            public int Valor;
        }
    }
}
