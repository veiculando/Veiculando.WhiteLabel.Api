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
