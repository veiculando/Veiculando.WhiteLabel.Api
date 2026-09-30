using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services.Cms;

namespace Veiculando.WhiteLabel.Api.Controllers
{
    /// <summary>
    /// Configuração pública da instância WhiteLabel — consumida pelos frontends
    /// antes do login, para aplicar o branding na própria tela de entrada.
    /// </summary>
    /// <remarks>
    /// Extraído do antigo <c>StubsController</c>, removido no TP-R2. Era o único
    /// controller daquele arquivo com implementação real; os outros
    /// (<c>CampanhasController</c>, <c>ClientesController</c>) só devolviam 501.
    ///
    /// <para>Continua <c>[AllowAnonymous]</c> por necessidade: o branding é
    /// aplicado antes de existir token. Por isso responde apenas cor e logo —
    /// nada aqui pode ser sensível.</para>
    ///
    /// <para><c>cmsHabilitado</c> e <c>cmsSiteUrl</c> seguem a mesma regra: dizem
    /// se o menu de conteúdo aparece e para onde apontam os links de prévia, nunca
    /// a URL do Supabase ou a chave.</para>
    /// </remarks>
    [ApiController]
    [Route("api/wl/config")]
    [AllowAnonymous]
    public class ConfigController : ControllerBase
    {
        private readonly ITenantContext _tenant;
        private readonly IWlTenantResolver _resolver;
        private readonly ICmsHabilitacao _cms;
        private readonly CmsConfiguracao _cmsConfig;

        public ConfigController(ITenantContext tenant, IWlTenantResolver resolver,
            ICmsHabilitacao cms, CmsConfiguracao cmsConfig)
        {
            _tenant = tenant;
            _resolver = resolver;
            _cms = cms;
            _cmsConfig = cmsConfig;
        }

        [HttpGet("branding")]
        public async Task<IActionResult> GetBranding()
        {
            var branding = await _resolver.ObterBrandingAsync(_tenant.AfiliadaId);
            if (branding == null)
                return StatusCode(503, new { message = "Branding WhiteLabel não configurado." });

            var cmsHabilitado = await _cms.HabilitadoAsync(_tenant.AfiliadaId);
            return Ok(branding.ComCms(cmsHabilitado, _cmsConfig.SiteUrl));
        }
    }
}
