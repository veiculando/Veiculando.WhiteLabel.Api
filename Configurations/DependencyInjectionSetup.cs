using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using Veiculando.Data.Contexts;
using Veiculando.Shared;
using Veiculando.WhiteLabel.Api.Configurations;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;

namespace Veiculando.WhiteLabel.Api.Configurations
{
    public static class DependencyInjectionSetup
    {
        public static void AddDependencyInjectionSetup(this IServiceCollection services, IConfiguration configuration)
        {
            // Connection string do core (TP-R2, Tarefa 6).
            //
            // `VeiculandoDataContext` tem construtor sem parâmetros e resolve a
            // conexão por `EnvironmentSettings.ConnectionString` — um campo
            // ESTÁTICO do Veiculando.Shared, não injetado. Nenhum ponto do BFF o
            // preenchia: o campo ficava em string vazia e a primeira consulta ao
            // banco falhava em runtime. Não era um problema de documentação, como
            // o plano supunha; era configuração ausente.
            //
            // Preenchido a partir de `ConnectionStrings:Veiculando`, que em
            // produção deve vir de `ConnectionStrings__Veiculando` no ambiente —
            // a string tem credenciais e não pode ser versionada.
            var connectionString = configuration.GetConnectionString("Veiculando");

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    "Connection string 'Veiculando' não configurada. Defina a variável de " +
                    "ambiente ConnectionStrings__Veiculando apontando para o banco do core. " +
                    "O valor NÃO deve ser versionado no appsettings.json.");
            }

            // Atribuição a estático: é o contrato do core, não uma escolha daqui.
            // Feito uma vez na composição da aplicação, antes de qualquer request.
            EnvironmentSettings.ConnectionString = connectionString;

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

