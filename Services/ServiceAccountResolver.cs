using System;
using Microsoft.Extensions.Options;
using Veiculando.Domain.Entities;
using Veiculando.Domain.Repositories;
using Veiculando.WhiteLabel.Api.Configurations;
using Veiculando.WhiteLabel.Api.Middleware;

namespace Veiculando.WhiteLabel.Api.Services
{
    public interface IServiceAccountResolver
    {
        /// <summary>
        /// Resolve o <see cref="UsuarioAfiliada"/> do Core que representa a
        /// conta de serviço desta instância WL, validando que ela realmente
        /// pertence ao AfiliadaId resolvido pelo Host (ADR-WL-004).
        /// Lança em vez de devolver null: uma conta de serviço mal configurada
        /// nunca deve degradar silenciosamente para "aprova tudo automaticamente"
        /// (o comportamento de SetAprovado() do Core quando o usuário não é
        /// UsuarioAfiliada).
        /// </summary>
        UsuarioAfiliada Resolve();
    }

    public class ServiceAccountResolver : IServiceAccountResolver
    {
        private readonly IUsuarioAfiliadaRepository _usuarioAfiliadaRepository;
        private readonly ITenantContext _tenantContext;
        private readonly SeedAccountOptions _options;

        public ServiceAccountResolver(
            IUsuarioAfiliadaRepository usuarioAfiliadaRepository,
            ITenantContext tenantContext,
            IOptions<SeedAccountOptions> options)
        {
            _usuarioAfiliadaRepository = usuarioAfiliadaRepository;
            _tenantContext = tenantContext;
            _options = options.Value;
        }

        public UsuarioAfiliada Resolve()
        {
            var usuario = _usuarioAfiliadaRepository.RetornaPorEmail(_options.Email);

            if (usuario == null)
            {
                throw new InvalidOperationException(
                    $"Conta de serviço '{_options.Email}' não existe como UsuarioAfiliada no Core. " +
                    "Sem isso, o Core aprova locais/peças automaticamente (SetAprovado) em vez de " +
                    "enfileirar para aprovação, e a guarda de tenant do handler é pulada (ADR-WL-004).");
            }

            if (usuario.IdAfiliada != _tenantContext.AfiliadaId)
            {
                throw new InvalidOperationException(
                    $"Conta de serviço '{_options.Email}' pertence à Afiliada {usuario.IdAfiliada}, " +
                    $"mas esta instância está configurada para a Afiliada {_tenantContext.AfiliadaId}.");
            }

            return usuario;
        }
    }
}
