using System;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities.WhiteLabel;
using Veiculando.Domain.Enums;

namespace Veiculando.WhiteLabel.Api.Middleware
{
    public sealed class WlTenantInfo
    {
        public int AfiliadaId { get; set; }
        public string Host { get; set; }
        public WlDominioTipoEnum Tipo { get; set; }
    }

    public sealed class WlBrandingPublico
    {
        public string NomeExibicao { get; set; }
        public string LogoUrl { get; set; }
        public string FaviconUrl { get; set; }
        public string PrimaryColor { get; set; }
        public string SecondaryColor { get; set; }
        public string AccentColor { get; set; }
        public string FooterText { get; set; }
        public string SeoTitle { get; set; }
        public string SeoDescription { get; set; }
    }

    public interface IWlTenantResolver
    {
        Task<WlTenantInfo> ResolverAsync(string host);
        Task<WlBrandingPublico> ObterBrandingAsync(int afiliadaId);
        void InvalidarDominio(string host);
        void InvalidarBranding(int afiliadaId);
    }

    public sealed class WlTenantResolver : IWlTenantResolver
    {
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
        private readonly VeiculandoDataContext _db;
        private readonly IMemoryCache _cache;

        public WlTenantResolver(VeiculandoDataContext db, IMemoryCache cache)
        {
            _db = db;
            _cache = cache;
        }

        public async Task<WlTenantInfo> ResolverAsync(string host)
        {
            var normalizado = WlHostNormalizer.Normalizar(host);
            var key = $"WlDominio:{normalizado}";
            if (_cache.TryGetValue(key, out WlTenantInfo cached)) return cached;

            var tenant = await _db.WlDominios
                .AsNoTracking()
                .Where(x => x.Host == normalizado
                         && x.Estado == WlDominioEstadoEnum.Active
                         && x.StatusExibicao == StatusExibicaoEnum.Ativo)
                .Select(x => new WlTenantInfo
                {
                    AfiliadaId = x.AfiliadaId,
                    Host = x.Host,
                    Tipo = x.Tipo
                })
                .SingleOrDefaultAsync();

            if (tenant != null) _cache.Set(key, tenant, CacheDuration);
            return tenant;
        }

        public async Task<WlBrandingPublico> ObterBrandingAsync(int afiliadaId)
        {
            if (afiliadaId <= 0) throw new ArgumentOutOfRangeException(nameof(afiliadaId));
            var key = $"WlBranding:{afiliadaId}";
            if (_cache.TryGetValue(key, out WlBrandingPublico cached)) return cached;

            var branding = await _db.WlConfiguracoes
                .AsNoTracking()
                .Where(x => x.AfiliadaId == afiliadaId
                         && x.StatusExibicao == StatusExibicaoEnum.Ativo)
                .Select(x => new WlBrandingPublico
                {
                    NomeExibicao = x.NomeExibicao,
                    LogoUrl = x.LogoUrl,
                    FaviconUrl = x.FaviconUrl,
                    PrimaryColor = x.PrimaryColor,
                    SecondaryColor = x.SecondaryColor,
                    AccentColor = x.AccentColor,
                    FooterText = x.FooterText,
                    SeoTitle = x.SeoTitle,
                    SeoDescription = x.SeoDescription
                })
                .SingleOrDefaultAsync();

            if (branding != null) _cache.Set(key, branding, CacheDuration);
            return branding;
        }

        public void InvalidarDominio(string host) =>
            _cache.Remove($"WlDominio:{WlHostNormalizer.Normalizar(host)}");

        public void InvalidarBranding(int afiliadaId) =>
            _cache.Remove($"WlBranding:{afiliadaId}");
    }
}
