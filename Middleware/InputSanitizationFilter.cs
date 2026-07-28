using System;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Veiculando.WhiteLabel.Api.Middleware
{
    public class InputSanitizationFilter : IActionFilter
    {
        private static readonly string[] DangerousPatterns = new[]
        {
            "--", ";", "DROP ", "SELECT ", "INSERT ", "UPDATE ", "DELETE ",
            "EXEC ", "UNION ", "xp_", "<script", "javascript:"
        };

        public void OnActionExecuting(ActionExecutingContext context)
        {
            foreach (var param in context.ActionArguments)
            {
                var valueStr = param.Value?.ToString() ?? string.Empty;
                if (DangerousPatterns.Any(p => valueStr.Contains(p, StringComparison.OrdinalIgnoreCase)))
                {
                    context.Result = new BadRequestObjectResult(new { error = "Parâmetro inválido detectado." });
                    return;
                }
            }
        }

        public void OnActionExecuted(ActionExecutedContext context) { }
    }
}
