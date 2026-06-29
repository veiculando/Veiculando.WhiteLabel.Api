using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using Veiculando.Data.Contexts;
using Veiculando.WhiteLabel.Api.Configurations;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;

namespace Veiculando.WhiteLabel.Api.Configurations
{
    public static class DependencyInjectionSetup
    {
        public static void AddDependencyInjectionSetup(this IServiceCollection services, IConfiguration configuration)
        {
            // Contextos de Banco de Dados
            services.AddScoped<WhiteLabelDataContext>();
            services.AddScoped<VeiculandoDataContext>();

            // Tenant
            services.AddScoped<ITenantContext, TenantContext>();

            // Configuração do Seed
            services.Configure<SeedAccountOptions>(configuration.GetSection("SeedAccount"));
            
            // Cache para o JWT do Seed
            services.AddMemoryCache();

            // Client HTTP para o Core
            var coreApiUrl = configuration.GetValue<string>("CoreApiUrl") ?? "https://localhost:44321/";
            services.AddHttpClient<IVeiculandoApiClient, VeiculandoApiClient>(client => 
            {
                client.BaseAddress = new Uri(coreApiUrl);
            });

            // Fonte Injector
            services.AddScoped<IFonteInjector, FonteInjector>();

            // MediatR (referenciando os assemblies de Domain)
            services.AddMediatR(typeof(Veiculando.Domain.Entities.EntityBase));
            // services.AddMediatR(typeof(Veiculando.WhiteLabel.Domain.ValueObjects.SomeStub)); // Ajustar para classe base WL se houver
        }
    }
}

