using Veiculando.Domain.Enums;
using Veiculando.Domain.Entities.Interfaces;
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

            // O middleware resolveu o AfiliadaId da instância e guardou no contexto
            // Como este proxy é WhiteLabel, a fonte da agência equivale ao ID da Afiliada do WL
            var agenciaId = _tenantContext.AfiliadaId;

            // IOrigemRastreavel só expõe os getters (contrato de leitura). RegistrarOrigem é
            // implementado individualmente por cada entidade rastreável do Core (Local, Peca,
            // Campanha, ArteFinal, Pedido) com assinatura idêntica, mas não está na interface —
            // despacho dinâmico evita alterar o contrato do Core a partir do BFF.
            ((dynamic)entity).RegistrarOrigem(FonteOrigemEnum.WhiteLabel, agenciaId, null);
        }
    }
}
