using System;
using Microsoft.Extensions.Configuration;
using Veiculando.WhiteLabel.Api.Middleware;

namespace Veiculando.WhiteLabel.Api.Configurations
{
    public class SeedAccountOptions
    {
        public string Email { get; set; }
        public string Password { get; set; }
    }

    public interface ISeedAccountResolver
    {
        SeedAccountOptions Resolve();
    }

    /// <summary>
    /// Resolve a conta de servico do tenant atual. A origem e IConfiguration para
    /// permitir que os valores sejam fornecidos pelo Azure Key Vault sem que o
    /// BFF conheca ou registre o segredo.
    /// </summary>
    public sealed class SeedAccountResolver : ISeedAccountResolver
    {
        private readonly IConfiguration _configuration;
        private readonly ITenantContext _tenant;

        public SeedAccountResolver(IConfiguration configuration, ITenantContext tenant)
        {
            _configuration = configuration;
            _tenant = tenant;
        }

        public SeedAccountOptions Resolve()
        {
            if (!_tenant.Resolvido || _tenant.AfiliadaId <= 0)
                throw new InvalidOperationException("Tenant nao resolvido para a conta de servico.");

            var prefixo = $"SeedAccounts:{_tenant.AfiliadaId}";
            var options = new SeedAccountOptions
            {
                Email = _configuration[$"{prefixo}:Email"],
                Password = _configuration[$"{prefixo}:Password"]
            };

            if (string.IsNullOrWhiteSpace(options.Email) ||
                string.IsNullOrWhiteSpace(options.Password))
            {
                throw new InvalidOperationException(
                    $"Conta de servico nao configurada para a afiliada {_tenant.AfiliadaId}.");
            }

            return options;
        }
    }
}
