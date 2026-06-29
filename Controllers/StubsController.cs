using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Veiculando.WhiteLabel.Api.Controllers
{
    [ApiController]
    [Route("api/wl/[controller]")]
    [Authorize]
    public class LocaisController : ControllerBase
    {
        [HttpGet]
        public IActionResult Get()
        {
            return StatusCode(501, "Not Implemented Yet");
        }
    }

    [ApiController]
    [Route("api/wl/[controller]")]
    [Authorize]
    public class PecasController : ControllerBase
    {
        [HttpGet]
        public IActionResult Get()
        {
            return StatusCode(501, "Not Implemented Yet");
        }
    }

    [ApiController]
    [Route("api/wl/[controller]")]
    [Authorize]
    public class PedidosController : ControllerBase
    {
        [HttpGet]
        public IActionResult Get()
        {
            return StatusCode(501, "Not Implemented Yet");
        }
    }

    [ApiController]
    [Route("api/wl/[controller]")]
    [Authorize]
    public class CampanhasController : ControllerBase
    {
        [HttpGet]
        public IActionResult Get()
        {
            return StatusCode(501, "Not Implemented Yet");
        }
    }

    [ApiController]
    [Route("api/wl/[controller]")]
    [Authorize]
    public class ClientesController : ControllerBase
    {
        [HttpGet]
        public IActionResult Get()
        {
            return StatusCode(501, "Not Implemented Yet");
        }
    }

    [ApiController]
    [Route("api/wl/[controller]")]
    [AllowAnonymous]
    public class ConfigController : ControllerBase
    {
        [HttpGet("branding")]
        public IActionResult GetBranding()
        {
            // Lê do environment conforme ADR-WL-005
            var branding = Environment.GetEnvironmentVariable("WL_BRANDING_JSON");
            if (string.IsNullOrEmpty(branding))
            {
                return Ok(new { primaryColor = "#000000", logoUrl = "" });
            }
            return Content(branding, "application/json");
        }
    }
}
