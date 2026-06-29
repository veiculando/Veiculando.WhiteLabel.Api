using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
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

        public async Task InvokeAsync(HttpContext context, ITenantContext tenantContext, IConfiguration configuration)
        {
            // Resolve o AfiliadaId da instância (via environment variable ou appsettings)
            var configAfiliadaId = configuration.GetValue<int>("WL:AfiliadaId");
            
            if (configAfiliadaId == 0)
            {
                _logger.LogWarning("WL:AfiliadaId não configurado corretamente neste ambiente.");
            }

            // Lê o header enviado pelo frontend
            var headerValue = context.Request.Headers["X-Tenant-AfiliadaId"].ToString();
            
            if (int.TryParse(headerValue, out var headerAfiliadaId))
            {
                if (headerAfiliadaId != configAfiliadaId)
                {
                    _logger.LogWarning($"Divergência de Tenant detectada. Header enviou {headerAfiliadaId}, mas a instância está configurada para {configAfiliadaId}. Forçando valor da instância.");
                }
            }

            // O servidor sempre é a fonte de verdade para a instância WhiteLabel (ADR-WL-005)
            tenantContext.SetAfiliadaId(configAfiliadaId);

            await _next(context);
        }
    }
}
