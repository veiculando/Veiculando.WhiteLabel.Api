using System;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Veiculando.WhiteLabel.Api.Services.Cms;

namespace Veiculando.WhiteLabel.Api.Controllers.Cms;

/// <summary>
/// Recusa com 413 <c>{ message }</c> o POST/PUT cujo <c>Content-Length</c> passa de
/// <see cref="CmsLimites.CorpoMultipart"/>, antes de ler o form.
/// </summary>
/// <remarks>
/// <para>O <c>[RequestSizeLimit]</c> continua nas actions: no Kestrel ele corta o
/// corpo sem <c>Content-Length</c> (chunked). Mas o erro dele é um 413 sem corpo, e
/// o do <c>[RequestFormLimits]</c> vira 400 de model binding. Este filtro roda
/// antes do binding e dá ao front o 413 com a mensagem do contrato.</para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class CmsLimiteCorpoAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        if (context.HttpContext.Request.ContentLength > CmsLimites.CorpoMultipart)
        {
            context.Result = new ObjectResult(new CmsErroDto
            {
                Message = $"O envio passa do limite de {CmsLimites.CorpoMultipart / (1024 * 1024)} MB.",
            })
            {
                StatusCode = StatusCodes.Status413PayloadTooLarge,
            };
        }
    }

    public void OnResourceExecuted(ResourceExecutedContext context) { }
}
