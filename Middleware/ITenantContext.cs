using Veiculando.Domain.Enums;

namespace Veiculando.WhiteLabel.Api.Middleware
{
    public interface ITenantContext
    {
        int AfiliadaId { get; }
        string Host { get; }
        WlDominioTipoEnum Tipo { get; }
        bool Resolvido { get; }
        void Definir(WlTenantInfo tenant);
    }

    public class TenantContext : ITenantContext
    {
        public int AfiliadaId { get; private set; }
        public string Host { get; private set; }
        public WlDominioTipoEnum Tipo { get; private set; }
        public bool Resolvido { get; private set; }

        public void Definir(WlTenantInfo tenant)
        {
            AfiliadaId = tenant.AfiliadaId;
            Host = tenant.Host;
            Tipo = tenant.Tipo;
            Resolvido = true;
        }
    }
}
