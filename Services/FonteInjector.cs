using Veiculando.Domain.Entities.Interfaces;
using Veiculando.Domain.Enums;
using Veiculando.WhiteLabel.Api.Middleware;

namespace Veiculando.WhiteLabel.Api.Services
{
    public interface IFonteInjector
    {
        void InjectFonte<T>(T entity) where T : class, IOrigemRastreavel;
    }

    public class FonteInjector : IFonteInjector
    {
        private readonly ITenantContext _tenantContext;

        public FonteInjector(ITenantContext tenantContext)
        {
            _tenantContext = tenantContext;
        }

        public void InjectFonte<T>(T entity) where T : class, IOrigemRastreavel
        {
            if (entity == null) return;

            var agenciaId = _tenantContext.AfiliadaId;
            entity.RegistrarOrigem(FonteOrigemEnum.WhiteLabel, agenciaId, null);
        }
    }
}
