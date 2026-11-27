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
        /// <summary>Página do BFF que abre o app de campo pelo scheme (VEI-RD-155).</summary>
        public string CampoPrimeiroAcesso(string token, string email) => Campo(HostDoTenant(), CampoAcesso.PrimeiroAcesso, token, email);
        public string CampoRecuperacao(string token, string email) => Campo(HostDoTenant(), CampoAcesso.AlterarSenha, token, email);

        /// <summary>
        /// Link de campo no domínio de outra exibidora (VEI-RD-181): o pedido central
        /// chega por um Host só e cada e-mail leva o domínio da exibidora do operador.
        /// </summary>
        public string CampoPrimeiroAcesso(string host, string token, string email) => Campo(host, CampoAcesso.PrimeiroAcesso, token, email);
        public string CampoRecuperacao(string host, string token, string email) => Campo(host, CampoAcesso.AlterarSenha, token, email);

        private string Campo(string host, string tipo, string token, string email) =>
            $"{Origem(host)}/api/wl/campo/acesso?tipo={tipo}&{Query(token, email)}";

        private string Montar(string pagina, string token, string email) =>
            $"{Origem(HostDoTenant())}/login/{pagina}?{Query(token, email)}";

        private static string Query(string token, string email) =>
            $"token={Uri.EscapeDataString(token)}&email={Uri.EscapeDataString(email)}";

        private string HostDoTenant()
        {
            if (!_tenant.Resolvido || string.IsNullOrWhiteSpace(_tenant.Host))
                throw new InvalidOperationException("Tenant não resolvido para o link de autenticação.");
            return _tenant.Host;
        }

        private string Origem(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
                throw new InvalidOperationException("Host ausente para o link de autenticação.");

            var configured = _configuration[$"WlPublicOrigins:Hosts:{host}"];
            var value = string.IsNullOrWhiteSpace(configured) ? $"https://{host}" : configured;
            var allowHttp = _environment.IsEnvironment("Preview") &&
                _configuration.GetValue<bool>("WlPublicOrigins:AllowHttpPreview");

            if (!Uri.TryCreate(value, UriKind.Absolute, out var origin) ||
                !string.Equals(origin.IdnHost, host, StringComparison.OrdinalIgnoreCase) ||
                origin.AbsolutePath != "/" || origin.Query.Length > 0 || origin.Fragment.Length > 0 ||
                origin.UserInfo.Length > 0 ||
                (origin.Scheme != Uri.UriSchemeHttps && !(allowHttp && origin.Scheme == Uri.UriSchemeHttp)))
                throw new InvalidOperationException("Origem pública inválida para o tenant. HTTPS é obrigatório fora do preview explícito.");

            return origin.GetLeftPart(UriPartial.Authority);
        }
    }

    /// <summary>Tipos aceitos na página e no deep link de acesso de campo.</summary>
    public static class CampoAcesso
    {
        public const string PrimeiroAcesso = "primeiro-acesso";
        public const string AlterarSenha = "alterar-senha";
        public const string Scheme = "veiculandooperador";

        public static bool Valido(string tipo) => tipo == PrimeiroAcesso || tipo == AlterarSenha;
    }
}
