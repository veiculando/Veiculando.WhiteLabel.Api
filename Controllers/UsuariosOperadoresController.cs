using System;
using System.Data.Entity;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Veiculando.Data.Contexts;
using Veiculando.WhiteLabel.Api.Configurations;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;

namespace Veiculando.WhiteLabel.Api.Controllers
{
    [ApiController]
    [Route("api/wl/usuarios-operadores")]
    [Authorize(Policy = AuthorizationSetup.UsuarioAfiliadaGerenciar)]
    public class UsuariosOperadoresController : ControllerBase
    {
        private readonly VeiculandoDataContext _db;
        private readonly ITenantQueries _tenant;

        public UsuariosOperadoresController(VeiculandoDataContext db, ITenantQueries tenant)
        {
            _db = db;
            _tenant = tenant;
        }

        [HttpPost]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public async Task<IActionResult> Create([FromBody] OperadorCampoCreateDto dto, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(dto?.Nome) || string.IsNullOrWhiteSpace(dto?.Email))
                return BadRequest(new { message = "Nome e email são obrigatórios." });

            var afiliadaId = _tenant.AfiliadaId;
            var email = dto.Email.ToLowerInvariant().Trim();
            var afiliada = await _db.Afiliadas.FirstOrDefaultAsync(a => a.Id == afiliadaId, ct);
            var perfil = await _db.PerfilUsuario.FirstOrDefaultAsync(ct);
            if (afiliada == null || perfil == null)
                return BadRequest(new { message = "A instância não está pronta para cadastrar um operador de campo." });

            var (core, wl) = CriacaoOperadorCampo.Montar(
                afiliada, perfil, dto.Nome.Trim(), email, Guid.NewGuid().ToString("N"),
                dto.Colador, dto.Fiscal, null);

            if (!core.IsValid())
                return BadRequest(core.Notifications);

            _db.UsuariosOperador.Add(core);
            _db.WlUsuariosOperador.Add(wl);
            await _db.SaveChangesAsync(ct);

            return Created(string.Empty, new { id = wl.Id, usuarioOperadorId = core.Id });
        }
    }

    public class OperadorCampoCreateDto
    {
        public string Nome { get; set; }
        public string Email { get; set; }
        public bool Colador { get; set; }
        public bool Fiscal { get; set; }
    }
}
