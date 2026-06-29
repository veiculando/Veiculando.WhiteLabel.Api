namespace Veiculando.WhiteLabel.Api.Middleware
{
    public interface ITenantContext
    {
        int AfiliadaId { get; }
        void SetAfiliadaId(int afiliadaId);
    }

    public class TenantContext : ITenantContext
    {
        public int AfiliadaId { get; private set; }

        public void SetAfiliadaId(int afiliadaId)
        {
            AfiliadaId = afiliadaId;
        }
    }
}
