using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Veiculando.WhiteLabel.Api.Configurations;
using Veiculando.WhiteLabel.Api.Services;

namespace Veiculando.WhiteLabel.Api.Controllers;

[ApiController]
[Route(SnapsSupportProxyRoutes.Prefix)]
[Authorize(AuthenticationSchemes = "Bearer", Policy = AuthorizationSetup.SuporteGerenciar)]
public sealed class SnapsSupportProxyController(SnapsSupportProxyService proxy) : ControllerBase
{
    // Sem filtro por verbo no MVC para devolver 404 também para métodos fora da allowlist.
    [Route("{**route}")]
    [RequestSizeLimit(SnapsSupportProxyService.MaxRequestBytes)]
    public Task Forward() => proxy.ForwardAsync(HttpContext, "/" + RouteData.Values["route"]);
}
