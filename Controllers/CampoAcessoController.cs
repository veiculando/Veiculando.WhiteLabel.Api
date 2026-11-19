using System;
using System.Net;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;

namespace Veiculando.WhiteLabel.Api.Controllers
{
    /// <summary>
    /// Página do link dos e-mails de campo (VEI-RD-155a). Tenta abrir o app pelo
    /// scheme <c>veiculandooperador://</c>; se ele não abrir, mostra o domínio para
    /// digitar no app e os links das lojas. Universal links ficam fora (peer review I3).
    /// </summary>
    [ApiController]
    [Route("api/wl/campo/acesso")]
    public class CampoAcessoController : ControllerBase
    {
        private readonly ITenantContext _tenant;
        private readonly IWlTenantResolver _tenantResolver;
        private readonly IConfiguration _configuration;

        public CampoAcessoController(ITenantContext tenant, IWlTenantResolver tenantResolver, IConfiguration configuration)
        {
            _tenant = tenant;
            _tenantResolver = tenantResolver;
            _configuration = configuration;
        }

        [AllowAnonymous]
        [HttpGet]
        public async Task<IActionResult> Get([FromQuery] string tipo, [FromQuery] string token, [FromQuery] string email)
        {
            // O token vai na URL: nada de cache nem de Referer para terceiros.
            Response.Headers["Cache-Control"] = "no-store";
            Response.Headers["Referrer-Policy"] = "no-referrer";
            Response.Headers["X-Content-Type-Options"] = "nosniff";

            var marca = (await _tenantResolver.ObterBrandingAsync(_tenant.AfiliadaId))?.NomeExibicao;
            marca = string.IsNullOrWhiteSpace(marca) ? "Veiculando" : marca.Trim();

            if (!CampoAcesso.Valido(tipo) || string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(email))
                return Pagina(400, marca, null, null);

            var link = $"{CampoAcesso.Scheme}://acesso?host={Uri.EscapeDataString(_tenant.Host)}&tipo={tipo}" +
                $"&token={Uri.EscapeDataString(token.Trim())}&email={Uri.EscapeDataString(email.Trim())}";
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            return Pagina(200, marca, link, nonce);
        }

        private ContentResult Pagina(int status, string marca, string link, string nonce)
        {
            var script = nonce == null ? "'none'" : $"'nonce-{nonce}'";
            Response.Headers["Content-Security-Policy"] =
                $"default-src 'none'; style-src 'unsafe-inline'; script-src {script}; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

            var h = WebUtility.HtmlEncode(marca);
            var host = WebUtility.HtmlEncode(_tenant.Host);
            string corpo;
            if (link == null)
            {
                corpo = "<h1>Link inválido</h1><p>Este link está incompleto. Peça um novo pelo app, em Esqueci senha ou Primeiro acesso.</p>";
            }
            else
            {
                var href = WebUtility.HtmlEncode(link);
                corpo =
                    $"<h1>{h}</h1>" +
                    $"<p><a class=\"botao\" href=\"{href}\">Abrir no app</a></p>" +
                    "<p>Se o app não abrir, instale o app do operador e, quando ele pedir o domínio da exibidora, digite:</p>" +
                    $"<p class=\"dominio\">{host}</p>" +
                    Lojas() +
                    "<p class=\"nota\">Depois de instalar, volte a este e-mail e toque no link de novo.</p>" +
                    $"<script nonce=\"{nonce}\">location.href={System.Text.Json.JsonSerializer.Serialize(link)};</script>";
            }

            var html =
                "<!doctype html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\">" +
                "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
                $"<title>{h}</title><style>" +
                "body{font-family:system-ui,sans-serif;max-width:28rem;margin:2rem auto;padding:0 1rem;color:#1a1a1a;background:#fff}" +
                ".botao{display:inline-block;padding:.75rem 1.5rem;background:#1a1a1a;color:#fff;text-decoration:none;border-radius:6px}" +
                ".dominio{font-size:1.25rem;font-weight:600;word-break:break-all}.nota{color:#555;font-size:.9rem}" +
                "</style></head><body>" + corpo + "</body></html>";

            return new ContentResult { StatusCode = status, ContentType = "text/html; charset=utf-8", Content = html };
        }

        private string Lojas()
        {
            var play = LojaSegura("CampoApp:PlayStoreUrl");
            var apple = LojaSegura("CampoApp:AppStoreUrl");
            if (play == null && apple == null)
                return string.Empty;
            var itens = (play == null ? "" : $"<li><a href=\"{play}\">Google Play</a></li>") +
                (apple == null ? "" : $"<li><a href=\"{apple}\">App Store</a></li>");
            return $"<ul>{itens}</ul>";
        }

        private string LojaSegura(string chave)
        {
            var valor = _configuration[chave];
            return Uri.TryCreate(valor, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
                ? WebUtility.HtmlEncode(uri.AbsoluteUri)
                : null;
        }
    }
}
