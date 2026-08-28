using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Veiculando.WhiteLabel.Api.Middleware;

namespace Veiculando.WhiteLabel.Api.Services
{
    /// <summary>Links de autenticação usam o domínio cadastrado, nunca headers de redirecionamento.</summary>
    public sealed class WlPublicLinks
    {
        private readonly ITenantContext _tenant;
        private readonly IConfiguration _configuration;
        private readonly IHostEnvironment _environment;

        public WlPublicLinks(ITenantContext tenant, IConfiguration configuration, IHostEnvironment environment)
        {
            _tenant = tenant;
            _configuration = configuration;
            _environment = environment;
        }

        public string Convite(string token, string email) => Montar("primeiro-acesso", token, email);
        public string Recuperacao(string token, string email) => Montar("alterar-senha", token, email);

        private string Montar(string pagina, string token, string email)
        {
            if (!_tenant.Resolvido || string.IsNullOrWhiteSpace(_tenant.Host))
                throw new InvalidOperationException("Tenant não resolvido para o link de autenticação.");

            var configured = _configuration[$"WlPublicOrigins:Hosts:{_tenant.Host}"];
            var value = string.IsNullOrWhiteSpace(configured) ? $"https://{_tenant.Host}" : configured;
            var allowHttp = _environment.IsEnvironment("Preview") &&
                _configuration.GetValue<bool>("WlPublicOrigins:AllowHttpPreview");

            if (!Uri.TryCreate(value, UriKind.Absolute, out var origin) ||
                !string.Equals(origin.IdnHost, _tenant.Host, StringComparison.OrdinalIgnoreCase) ||
                origin.AbsolutePath != "/" || origin.Query.Length > 0 || origin.Fragment.Length > 0 ||
                origin.UserInfo.Length > 0 ||
                (origin.Scheme != Uri.UriSchemeHttps && !(allowHttp && origin.Scheme == Uri.UriSchemeHttp)))
                throw new InvalidOperationException("Origem pública inválida para o tenant. HTTPS é obrigatório fora do preview explícito.");

            var query = $"token={Uri.EscapeDataString(token)}&email={Uri.EscapeDataString(email)}";
            return $"{origin.GetLeftPart(UriPartial.Authority)}/login/{pagina}?{query}";
        }
    }
}
