using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;
using Veiculando.WhiteLabel.Api.Configurations;

namespace Veiculando.WhiteLabel.Api
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        /// <summary>
        /// Nome da política de CORS aplicada aos frontends WhiteLabel.
        /// </summary>
        private const string CorsFrontendsWl = "FrontendsWl";

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddJwtLocalAuthentication(Configuration);
            services.AddDependencyInjectionSetup(Configuration);

            // Os dois frontends WhiteLabel são CSR (ADR-WL-006): o browser chama o
            // BFF cross-origin diretamente. Sem CORS o preflight falha e NENHUMA
            // tela carrega dados — só o Swagger, que é same-origin, funcionaria.
            // As origens vêm de `WL:AllowedOrigins` para cada instância declarar
            // o próprio domínio; `AllowAnyOrigin` não serviria porque o
            // `Authorization` exige credenciais explicitamente permitidas.
            var origensPermitidas = Configuration.GetSection("WL:AllowedOrigins").Get<string[]>()
                                    ?? new string[0];

            services.AddCors(options =>
            {
                options.AddPolicy(CorsFrontendsWl, policy =>
                {
                    policy.WithOrigins(origensPermitidas)
                          .AllowAnyHeader()
                          .AllowAnyMethod();
                });
            });

            services.AddControllers();
            services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo { Title = "Veiculando.WhiteLabel.Api", Version = "v1" });
            });
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
                app.UseSwagger();
                app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Veiculando.WhiteLabel.Api v1"));
            }

            app.UseHttpsRedirection();

            app.UseRouting();

            // Depois de UseRouting e antes de UseAuthentication/UseAuthorization,
            // como exige a ordem documentada do pipeline: o preflight OPTIONS não
            // carrega Authorization e precisa ser respondido antes de qualquer
            // checagem de autenticação.
            app.UseCors(CorsFrontendsWl);

            app.UseMiddleware<Veiculando.WhiteLabel.Api.Middleware.TenantMiddleware>();

            app.UseAuthentication();
            app.UseAuthorization();


            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
            });
        }
    }
}
