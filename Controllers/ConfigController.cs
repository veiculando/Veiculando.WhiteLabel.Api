using System;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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
    /// </remarks>
    [ApiController]
    [Route("api/wl/config")]
    [AllowAnonymous]
    public class ConfigController : ControllerBase
    {
        [HttpGet("branding")]
        public IActionResult GetBranding()
        {
            var branding = Environment.GetEnvironmentVariable("WL_BRANDING_JSON");
            if (string.IsNullOrEmpty(branding))
            {
                return Ok(new { primaryColor = "#000000", logoUrl = "" });
            }
            return Content(branding, "application/json");
        }
    }
}
