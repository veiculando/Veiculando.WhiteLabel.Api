using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Veiculando.WhiteLabel.Api.Services.Cms;

namespace Veiculando.WhiteLabel.Api.Middleware
{
    /// <summary>
    /// Responde 404 em <c>/api/wl/cms/*</c> quando o módulo está desligado para a
    /// afiliada da requisição.
    /// </summary>
    /// <remarks>
    /// <para><b>Por que middleware, e não filtro MVC.</b> O filtro roda depois da
    /// autorização: um operador sem <c>ConteudoGerenciar</c> receberia 403, e o 403
    /// confirma que o módulo existe. O critério é 404 com o módulo desligado, com ou
    /// sem permissão. Por isso o <c>Startup</c> registra este middleware depois do
    /// <c>TenantMiddleware</c> (precisa da afiliada) e antes de
    /// <c>UseAuthentication</c>/<c>UseAuthorization</c>.</para>
    /// </remarks>
    public sealed class CmsModuloMiddleware
    {
        public const string Prefixo = "/api/wl/cms";

        private readonly RequestDelegate _next;

        public CmsModuloMiddleware(RequestDelegate next) => _next = next;

        public async Task InvokeAsync(HttpContext context, ITenantContext tenant, ICmsHabilitacao cms)
        {
            if (!tenant.Resolvido || !await cms.HabilitadoAsync(tenant.AfiliadaId))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsJsonAsync(new { message = "Recurso não encontrado." });
                return;
            }

            await _next(context);
        }
    }
}
