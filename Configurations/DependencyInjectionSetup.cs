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
            // Contexto de Banco de Dados.
            //
            // Um único contexto, por decisão da ADR-WL-005 revisada: WL_Usuario
            // vive no banco central e não há DbContext WhiteLabel separado. O
            // registro de `WhiteLabelDataContext` que existia aqui apontava para
            // uma classe removida no TP-R0 (ela declarava `name=WLCnnStr`, uma
            // connection string que não estava definida em lugar nenhum) e
            // deixava o BFF sem compilar.
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

            // Cadastro de Local/Peça delegado ao core (ver CoreCadastroService)
            services.AddScoped<ICoreCadastroService, CoreCadastroService>();

            // Validação de Arquivos e Sanitização de Entrada
            services.AddSingleton<IFileValidationService, FileValidationService>();
            services.AddScoped<InputSanitizationFilter>();
        }
    }
}

