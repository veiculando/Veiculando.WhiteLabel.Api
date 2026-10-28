using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Veiculando.WhiteLabel.Api.Controllers;
using Veiculando.WhiteLabel.Api.Middleware;

namespace Veiculando.WhiteLabel.Api.Configurations
{
    public sealed class CampoRequirement : IAuthorizationRequirement { }

    // O tenant e scoped e vem do Host validado pelo TenantMiddleware.
    public sealed class CampoAuthorizationHandler : AuthorizationHandler<CampoRequirement>
    {
        private readonly ITenantContext _tenant;

        public CampoAuthorizationHandler(ITenantContext tenant) => _tenant = tenant;

        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, CampoRequirement requirement)
        {
            if (_tenant.Resolvido && _tenant.AfiliadaId > 0
                && int.TryParse(context.User.FindFirst(CampoAuthController.ClaimUsuarioOperadorId)?.Value, out var operadorId)
                && operadorId > 0
                && int.TryParse(context.User.FindFirst("AfiliadaId")?.Value, out var afiliadaId)
                && afiliadaId == _tenant.AfiliadaId)
                context.Succeed(requirement);

            return Task.CompletedTask;
        }
    }
}
