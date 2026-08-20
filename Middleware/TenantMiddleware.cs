using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Veiculando.WhiteLabel.Api.Middleware
{
    public class TenantMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<TenantMiddleware> _logger;

        public TenantMiddleware(RequestDelegate next, ILogger<TenantMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(
            HttpContext context,
            ITenantContext tenantContext,
            IWlTenantResolver resolver)
        {
            WlTenantInfo tenant;
            try
            {
                tenant = await resolver.ResolverAsync(context.Request.Host.Value);
            }
            catch (ArgumentException)
            {
                tenant = null;
            }

            if (tenant == null)
            {
                _logger.LogWarning("Host WhiteLabel desconhecido ou inativo: {Host}", context.Request.Host.Host);
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            tenantContext.Definir(tenant);
            await _next(context);
        }
    }
}
