using System;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Veiculando.Infra.Security;

namespace Veiculando.WhiteLabel.Api.Configurations
{
    public static class AuthenticationSetup
    {
        public static IServiceCollection AddJwtLocalAuthentication(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            if (services == null)
                throw new ArgumentNullException(nameof(services));

            var tokenSettingsSection = configuration.GetSection("JwtSettings");
            services.Configure<JwtSettings>(tokenSettingsSection);
            var tokenSettings = tokenSettingsSection.Get<JwtSettings>();

            // O segredo NÃO fica versionado (TP-R2). Ele estava no appsettings como
            // "WlCustomSecretKey_ChangeInProd_..." — um placeholder público, com o
            // qual qualquer pessoa com acesso ao repositório forjaria um JWT de
            // operador válido, sem precisar de credencial nenhuma.
            //
            // Deve vir de JwtSettings__Secret no ambiente. A aplicação falha ao
            // subir se não vier: um default silencioso reintroduziria exatamente o
            // problema que esta verificação existe para impedir.
            if (string.IsNullOrWhiteSpace(tokenSettings?.Secret))
            {
                throw new InvalidOperationException(
                    "JwtSettings:Secret não configurado. Defina a variável de ambiente " +
                    "JwtSettings__Secret com um segredo de no mínimo 32 caracteres. " +
                    "O valor NÃO deve ser versionado no appsettings.json.");
            }

            if (tokenSettings.Secret.Length < 32)
            {
                throw new InvalidOperationException(
                    "JwtSettings:Secret precisa ter no mínimo 32 caracteres para HMAC-SHA256 " +
                    $"(atual: {tokenSettings.Secret.Length}).");
            }

            var key = Encoding.ASCII.GetBytes(tokenSettings.Secret);

            services
                .AddAuthentication("Bearer")
                .AddJwtBearer("Bearer", options =>
                {
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        IssuerSigningKey = new SymmetricSecurityKey(key),
                        ValidateIssuer = false, // Em prod, pode-se validar o issuer por ambiente
                        ValidateAudience = false,
                        ValidateIssuerSigningKey = true,
                        ClockSkew = TimeSpan.Zero
                    };
                });

            return services;
        }
    }
}
