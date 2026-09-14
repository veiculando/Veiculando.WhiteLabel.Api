using System;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Net;
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

        /// <summary>Policies de rate limit, referenciadas pelos controllers.</summary>
        public const string RateLimitLogin = "wl-login";
        public const string RateLimitEscrita = "wl-escrita";
        public const string RateLimitRecuperacaoSenha = "wl-recuperacao-senha";

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddJwtLocalAuthentication(Configuration);
            services.AddDependencyInjectionSetup(Configuration);

            services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders =
                    ForwardedHeaders.XForwardedFor |
                    ForwardedHeaders.XForwardedProto |
                    ForwardedHeaders.XForwardedHost;

                var proxies = Configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>()
                              ?? Array.Empty<string>();
                foreach (var proxy in proxies)
                {
                    if (IPAddress.TryParse(proxy, out var address))
                        options.KnownProxies.Add(address);
                }
            });

            // Autorização granular por permissão (TP-R2, blocker B3).
            services.AddWlAuthorization();

            // Rate limiting (TP-R2, achado A1).
            //
            // Usa o limitador nativo do ASP.NET Core em vez do AspNetCoreRateLimit
            // que o plano sugeria: o projeto é net8.0 e o recurso está no
            // framework desde o .NET 7, então a dependência externa não se paga.
            //
            // Particionado por IP. Atrás de proxy/CDN o RemoteIpAddress passa a ser
            // o do proxy — nesse cenário é preciso configurar ForwardedHeaders, ou
            // o limite vira global em vez de por cliente.
            services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

                // Login: alvo de força bruta, limite bem mais apertado.
                options.AddPolicy(RateLimitLogin, ParticionarPorIp(limite: 10));

                // Escrita autenticada: protege contra automação abusiva sem
                // atrapalhar o uso normal do painel.
                options.AddPolicy(RateLimitEscrita, ParticionarPorIp(limite: 60));

                // Esqueci-senha/alterar-senha: particionado por Host+IP (e não só
                // IP) porque cada instância WL é um Host distinto atrás do mesmo
                // BFF — um limite só por IP misturaria o consumo de tenants
                // diferentes atrás do mesmo proxy/CDN. É a primeira camada; a
                // segunda, por hash do e-mail e independente de IP, vive em
                // IPasswordResetAttemptGuard.
                options.AddPolicy(RateLimitRecuperacaoSenha, ParticionarPorHostEIp(limite: 5));
            });

            services.AddHealthChecks();
            services.AddControllers();
            services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo { Title = "Veiculando.WhiteLabel.Api", Version = "v1" });
            });
        }

        /// <summary>
        /// Janela fixa de 1 minuto, particionada pelo IP de origem.
        /// </summary>
        private static Func<HttpContext, RateLimitPartition<string>> ParticionarPorIp(int limite)
        {
            return contexto => RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: contexto.Connection.RemoteIpAddress?.ToString() ?? "desconhecido",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limite,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0 // excedeu, recusa na hora: enfileirar só adiaria o 429
                });
        }

        /// <summary>
        /// Janela fixa de 1 minuto, particionada por Host + IP de origem.
        /// </summary>
        private static Func<HttpContext, RateLimitPartition<string>> ParticionarPorHostEIp(int limite)
        {
            return contexto =>
            {
                var host = contexto.Request.Host.Host ?? "desconhecido";
                var ip = contexto.Connection.RemoteIpAddress?.ToString() ?? "desconhecido";

                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: $"{host}|{ip}",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = limite,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    });
            };
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

            app.UseForwardedHeaders();
            // Liveness do processo: não depende de Host/tenant nem consulta dados.
            // Mantém as rotas de negócio atrás da resolução e autorização usuais.
            app.UseHealthChecks("/health");
            app.UseHttpsRedirection();

            app.UseRouting();

            app.UseMiddleware<Veiculando.WhiteLabel.Api.Middleware.TenantMiddleware>();

            // Antes de UseAuthentication: uma rajada em /auth/login deve ser
            // barrada sem custo de validação de token nem de acesso ao banco.
            app.UseRateLimiter();

            app.UseAuthentication();
            app.UseMiddleware<Veiculando.WhiteLabel.Api.Middleware.TenantBindingMiddleware>();
            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
            });
        }
    }
}
