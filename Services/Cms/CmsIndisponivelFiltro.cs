using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Veiculando.WhiteLabel.Api.Controllers.Cms;

namespace Veiculando.WhiteLabel.Api.Services.Cms;

/// <summary>
/// Transforma <see cref="CmsIndisponivelException"/> em 503 <c>{ message }</c>.
/// </summary>
/// <remarks>
/// Sem ele, a exceção chegaria ao <c>ErroInternoMiddleware</c> e sairia como
/// 500. O critério do card é outro: Supabase fora do ar ou pausado é 503 com
/// mensagem que o operador entende, e a tela mostra "tente de novo" em vez de
/// "erro interno". Aplicado nos controllers do CMS com
/// <c>[ServiceFilter(typeof(CmsIndisponivelFiltro))]</c>.
/// </remarks>
public sealed class CmsIndisponivelFiltro : IExceptionFilter
{
    /// <summary>Mesma frase da fixture <c>Contratos/cms-erro.json</c>.</summary>
    public const string Mensagem =
        "O serviço de conteúdo está indisponível no momento. Tente novamente em alguns minutos.";

    private readonly ILogger<CmsIndisponivelFiltro> _logger;

    public CmsIndisponivelFiltro(ILogger<CmsIndisponivelFiltro> logger) => _logger = logger;

    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not CmsIndisponivelException ex)
            return;

        _logger.LogWarning("CMS indisponível em {Rota}: {Detalhe}",
            context.HttpContext.Request.Path.Value, ex.Message);

        context.Result = new ObjectResult(new CmsErroDto { Message = Mensagem })
        {
            StatusCode = StatusCodes.Status503ServiceUnavailable,
        };
        context.ExceptionHandled = true;
    }
}
