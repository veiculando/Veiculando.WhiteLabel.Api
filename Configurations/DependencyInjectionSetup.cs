using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using Veiculando.Data.Contexts;
using Veiculando.Infra.IoC;
using Veiculando.WhiteLabel.Api.Configurations;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;
using Veiculando.WhiteLabel.Api.Validation;

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

            // Client HTTP para o FileServer (TP-C §2) — rede interna, nunca exposto ao navegador.
            var fileServerUrl = configuration.GetValue<string>("FileServerUrl") ?? "https://localhost:44322/";
            services.AddHttpClient<IFileServerClient, FileServerClient>(client =>
            {
                client.BaseAddress = new Uri(fileServerUrl);
            });

            // Fonte Injector
            services.AddScoped<IFonteInjector, FonteInjector>();

            // Resolução da conta de serviço (ADR-WL-004) e validação de
            // resposta de reserva (TP-C §1) — pré-Core, testáveis isoladamente.
            services.AddScoped<IServiceAccountResolver, ServiceAccountResolver>();
            services.AddScoped<IPedidoReservaRespostaValidator, PedidoReservaRespostaValidator>();

            // Repositórios, MediatR (handlers) e serviços do Core.
            //
            // O BFF referencia o Core via ProjectReference (não HTTP) para o
            // caminho de leitura e para delegar comandos como
            // PedidoReservaRespostaCommand em processo. Sem este wiring,
            // qualquer handler do Core resolvido por IMediator falha em
            // runtime por falta de repositório (ex.: IPedidoReservaRepository,
            // IUsuarioAfiliadaRepository) — o registro ficava só declarado em
            // Veiculando.Infra.IoC, nunca chamado a partir daqui.
            DataContextDependencyInjector.RegisterServices(services);
            CommandHandlerDependencyInjector.RegisterServices(services);
            ServiceDependencyInjector.RegisterServices(services);
        }
    }
}

