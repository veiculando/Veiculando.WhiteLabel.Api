using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Repositories;
using Veiculando.Data.Repositories;
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

            // Repositórios do core. Reusar as consultas de lá evita reimplementar
            // regra que já existe — o dashboard, por exemplo, usa
            // ILocalRepository.CountAprovacaoPendente em vez de repetir o COUNT.
            services.AddScoped<ILocalRepository, LocalRepository>();

            // Tenant
            services.AddScoped<ITenantContext, TenantContext>();
            services.AddScoped<IWlTenantResolver, WlTenantResolver>();

            // Superfície de consulta já recortada pela afiliada da instância.
            // Os controllers consultam por aqui em vez de tocar os DbSets crus: o
            // filtro de tenant deixa de depender de cada endpoint lembrar de
            // escrevê-lo. Ver ITenantQueries.
            services.AddScoped<ITenantQueries, TenantQueries>();

            // Conta de servico resolvida por tenant. IConfiguration pode ser
            // abastecida pelo Azure Key Vault; nenhum segredo e mantido no banco.
            services.AddScoped<ISeedAccountResolver, SeedAccountResolver>();

            // Cache para o JWT do Seed
            services.AddMemoryCache();

            // Recuperação de senha: API key e remetente do SendGrid vêm de
            // Key Vault/environment (WlPasswordEmail__SendGridApiKey,
            // WlPasswordEmail__FromEmail) — nunca do appsettings versionado.
            services.AddOptions<WlPasswordEmailOptions>()
                .Bind(configuration.GetSection("WlPasswordEmail"))
                .Validate(o => o.ConviteValidadeHoras >= 1 && o.ConviteValidadeHoras <= 168,
                    "ConviteValidadeHoras deve estar entre 1 e 168.")
                .ValidateOnStart();
            services.AddScoped<Services.IWlPasswordEmailSender, Services.SendGridWlPasswordEmailSender>();
            services.AddScoped<WlPublicLinks>();

            // Segunda camada de limite do esqueci-senha, por hash do e-mail e
            // independente de IP (ver PasswordResetAttemptGuard). Singleton: o
            // estado é o próprio IMemoryCache, compartilhado entre requisições.
            services.AddSingleton<IPasswordResetAttemptGuard, PasswordResetAttemptGuard>();

            // Client HTTP para o Core
            var coreApiUrl = configuration.GetValue<string>("CoreApiUrl") ?? "https://localhost:44321/";
            services.AddHttpClient<IVeiculandoApiClient, VeiculandoApiClient>(client => 
            {
                client.BaseAddress = new Uri(coreApiUrl);
            });

            // Cadastro de Local/Peça delegado ao core (ver CoreCadastroService)
            services.AddScoped<ICoreCadastroService, CoreCadastroService>();

            // Validação de arquivos (magic bytes + tamanho).
            services.AddSingleton<IFileValidationService, FileValidationService>();
            services.AddSingleton<IWlUploadStorage, AzureWlUploadStorage>();
            services.AddScoped<WlUploadPipeline>();
            services.AddScoped<WlUploadReferences>();
            services.AddHostedService<WlUploadReconciler>();

            // Não há filtro de sanitização de entrada por lista de padrões, e a
            // ausência é deliberada.
            //
            // Havia um `InputSanitizationFilter` registrado aqui e aplicado via
            // [ServiceFilter] nos controllers de escrita. Ele lia
            // `context.ActionArguments` e chamava `param.Value?.ToString()` —
            // que, num objeto complexo, devolve o NOME DO TIPO
            // ("...WlUsuarioCreateDto"), não o conteúdo dos campos. Ou seja:
            // nenhum corpo de request era inspecionado. Só parâmetros primitivos
            // de rota e query passavam pela lista.
            //
            // Fazê-lo varrer o grafo do objeto resolveria a inspeção e criaria
            // problema pior: a lista continha ";" e "--", então um endereço como
            // "Av. Paulista, 1000; loja 2" passaria a ser recusado com 400.
            //
            // As duas ameaças que a lista mirava já têm defesa real e específica:
            //  - SQL injection: todas as consultas do BFF são via EF (LINQ ou
            //    parâmetros), nunca concatenação de SQL.
            //  - XSS: tratado na SAÍDA, no Angular, que escapa por padrão em
            //    interpolação e property binding. O Exibidora não usa innerHTML,
            //    DomSanitizer nem bypassSecurityTrust em lugar nenhum — não há
            //    ponto de escape para reintroduzir o risco.
            //
            // Validação de entrada continua existindo onde é específica e não gera
            // falso positivo: whitelist de permissões (WlPermissoesValidas), tamanho
            // mínimo de senha, magic bytes de arquivo e as regras do domínio.
        }
    }
}

