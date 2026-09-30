using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Veiculando.WhiteLabel.Api.Configurations;

namespace Veiculando.WhiteLabel.Api.Tests.Infrastructure
{
    /// <summary>
    /// Rota em <c>api/wl/cms</c> que só existe nos testes, registrada pela
    /// <see cref="WlApiFactory"/> com <c>comSondaCms: true</c>.
    /// </summary>
    /// <remarks>
    /// Serve para medir a ORDEM do pipeline: com o módulo desligado, o
    /// <c>CmsModuloMiddleware</c> precisa responder 404 antes de a autorização
    /// dar 401/403. Sem uma rota real atrás do prefixo, qualquer requisição daria
    /// 404 pelo roteamento e o teste passaria sem provar nada. Exige uma policy
    /// que já existe, para o 401/403 do caminho ligado ser o da autorização de
    /// verdade.
    /// </remarks>
    [ApiController]
    [Route("api/wl/cms/_sonda")]
    [Authorize(Policy = AuthorizationSetup.UsuarioAfiliadaGerenciar)]
    public sealed class CmsSondaController : ControllerBase
    {
        [HttpGet]
        public IActionResult Get() => Ok(new { sonda = true });
    }
}
