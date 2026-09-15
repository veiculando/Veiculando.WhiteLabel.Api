using System;
using System.Data.Entity;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Enums;

namespace Veiculando.WhiteLabel.Api.Middleware;

/// <summary>Revogação persistida: cada endpoint protegido do App valida novamente a sessão e a conta.</summary>
public sealed class AppSessionMiddleware
{
    private readonly RequestDelegate _next;
    public AppSessionMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, VeiculandoDataContext db, ITenantContext tenant)
    {
        var endpoint = context.GetEndpoint();
        if (context.Request.Path.StartsWithSegments("/api/wl/app") &&
            endpoint?.Metadata.GetMetadata<IAllowAnonymous>() == null && context.User.Identity?.IsAuthenticated == true)
        {
            var actor = context.User.FindFirst("WlAnuncianteId")?.Value;
            var jti = context.User.FindFirst("jti")?.Value;
            var now = DateTime.UtcNow;
            if (context.User.FindFirst("WlPerfil")?.Value != "Anunciante" || !int.TryParse(actor, out var userId) || !Guid.TryParse(jti, out var sessionId) ||
                !await db.WlAppSessoes.AnyAsync(s => s.Id == sessionId && s.UsuarioId == userId && s.AfiliadaId == tenant.AfiliadaId &&
                    !s.RevogadaEm.HasValue && s.ExpiraEm > now && s.Usuario.StatusExibicao == StatusExibicaoEnum.Ativo &&
                    s.Usuario.StatusConvite == StatusConviteWlEnum.Aceito, context.RequestAborted))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
        }
        await _next(context);
    }
}
