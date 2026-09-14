using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Veiculando.WhiteLabel.Api.Middleware
{
    public sealed class TenantBindingMiddleware
    {
        private readonly RequestDelegate _next;

        public TenantBindingMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context, ITenantContext tenant)
        {
            var endpoint = context.GetEndpoint();
            var anonimo = endpoint?.Metadata.GetMetadata<IAllowAnonymous>() != null;
            var protegido = endpoint?.Metadata.GetOrderedMetadata<IAuthorizeData>().Any() == true;

            if (!anonimo && protegido && context.User.Identity?.IsAuthenticated == true)
            {
                var claim = context.User.FindFirst("AfiliadaId")?.Value;
                if (!int.TryParse(claim, out var afiliadaId) || afiliadaId != tenant.AfiliadaId)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
            }

            await _next(context);
        }
    }
}
