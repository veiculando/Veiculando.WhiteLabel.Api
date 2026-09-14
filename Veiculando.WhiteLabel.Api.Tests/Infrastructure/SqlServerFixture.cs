using System;
using System.Data.Entity;
using System.Data.Entity.Infrastructure;
using System.Threading.Tasks;
// System.Data.SqlClient, e nao Microsoft.Data.SqlClient: e o provider que o
// EntityFramework 6.4 usa, ja vem transitivamente e evita duas implementacoes de
// SqlClient no mesmo processo.
using System.Data.SqlClient;
using Testcontainers.MsSql;
using Veiculando.Data.Contexts;
using Veiculando.Shared;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests.Infrastructure
{
    /// <summary>
    /// Sobe um SQL Server real em container e cria o schema uma vez por execucao
    /// da suite.
    /// </summary>
    /// <remarks>
    /// <para><b>Por que SQL Server de verdade e nao DbSet mockado.</b> Os tres bugs
    /// encontrados na revisao da Sprint 9.0 passariam num teste com contexto
    /// falso: <c>String.Split</c> dentro de um <c>Select</c> funciona em
    /// LINQ-to-Objects e so quebra quando o EF tenta traduzir para SQL; e uma
    /// navegacao nao carregada e <c>null</c> apenas quando existe um provider
    /// decidindo o que materializar. Mock nenhum reproduz isso.</para>
    ///
    /// <para><b>Por que o schema vem do modelo e nao das migrations.</b> O repo tem
    /// 98 migrations, e a ultima (<c>BaselineSchema2026</c>) tem <c>Up()</c>
    /// VAZIO — um baseline <c>-IgnoreChanges</c> adicionado no fim de uma
    /// historia que comeca em 2017. Isso e sinal de que o banco real divergiu das
    /// migrations, e o <c>RunMigration</c> confirma: ele aplica
    /// <c>ALTER TABLE ... ADD FonteOrigem</c> direto, fora do controle delas.
    /// Rodar as 98 contra um banco vazio produziria um schema que nao e nem o do
    /// modelo nem o de producao.</para>
    ///
    /// <para>Entao o schema e gerado do proprio modelo EF, via
    /// <c>ObjectContext.CreateDatabaseScript()</c>. O que isso cobre e o que nao
    /// cobre precisa ficar claro: cobre tudo que dependa de o EF traduzir LINQ,
    /// materializar entidades e respeitar FKs e indices do mapeamento — que e
    /// onde os bugs desta sprint moravam. NAO cobre divergencia entre o modelo e
    /// o schema de producao; para isso seria preciso extrair um script do banco
    /// real e versiona-lo, o que esta registrado como pendencia.</para>
    /// </remarks>
    public sealed class SqlServerFixture : IAsyncLifetime
    {
        private const string DatabaseName = "VeiculandoTests";

        private readonly MsSqlContainer _container = new MsSqlBuilder()
            .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
            .Build();

        /// <summary>Connection string apontando para o banco de teste ja com schema.</summary>
        public string ConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            await _container.StartAsync();

            var admin = _container.GetConnectionString();

            await ExecutarAsync(admin, $"IF DB_ID('{DatabaseName}') IS NULL CREATE DATABASE [{DatabaseName}];");

            ConnectionString = new SqlConnectionStringBuilder(admin)
            {
                InitialCatalog = DatabaseName
            }.ConnectionString;

            // VeiculandoDataContext le a connection string de um campo ESTATICO do
            // Veiculando.Shared — nao ha construtor que a receba. Preencher aqui e
            // o unico jeito de apontar o contexto para o container.
            EnvironmentSettings.ConnectionString = ConnectionString;

            await CriarSchemaAsync();
        }

        private async Task CriarSchemaAsync()
        {
            string script;

            using (var contexto = new VeiculandoDataContext())
            {
                script = ((IObjectContextAdapter)contexto).ObjectContext.CreateDatabaseScript();
            }

            await ExecutarAsync(ConnectionString, script);
            await CriarIndicesAsync();
        }

        /// <summary>
        /// Cria os indices que o script do modelo nao emite.
        /// </summary>
        /// <remarks>
        /// <c>CreateDatabaseScript()</c> gera tabelas, chaves e FKs, mas ignora
        /// indices declarados por <c>HasColumnAnnotation("Index", ...)</c> — essas
        /// anotacoes so sao lidas pelo pipeline de Migrations. Sem esta etapa o
        /// banco de teste nao teria <c>UK_WlUsuario_Email_Afiliada</c>, e um teste
        /// de e-mail duplicado passaria por ausencia da constraint em vez de por
        /// acerto do codigo — falso verde no pior lugar possivel.
        ///
        /// <para>A definicao espelha a migration <c>CriacaoWlUsuarioTPT</c>:
        /// unico sobre (Email, AfiliadaId), SEM filtro por status. E justamente a
        /// falta do filtro que reserva para sempre o e-mail de um operador
        /// excluido, comportamento que <c>UsuariosController.Create</c> assume.</para>
        ///
        /// <para>Ao adicionar novo indice anotado no mapeamento, ele precisa ser
        /// repetido aqui — nao ha como derivar isso do modelo automaticamente.</para>
        /// </remarks>
        private async Task CriarIndicesAsync()
        {
            const string indices = @"
CREATE UNIQUE INDEX [UK_WlUsuario_Email_Afiliada]
    ON [dbo].[WL_Usuario] ([Email], [AfiliadaId]);
CREATE UNIQUE INDEX [UK_WlDominio_Host]
    ON [dbo].[WlDominio] ([Host]);
CREATE UNIQUE INDEX [UK_WlConfiguracao_AfiliadaId]
    ON [dbo].[WL_Configuracao] ([AfiliadaId]);";

            await ExecutarAsync(ConnectionString, indices);
        }

        private static async Task ExecutarAsync(string connectionString, string sql)
        {
            await using var conexao = new SqlConnection(connectionString);
            await conexao.OpenAsync();

            await using var comando = conexao.CreateCommand();
            comando.CommandText = sql;
            comando.CommandTimeout = 180;
            await comando.ExecuteNonQueryAsync();
        }

        public async Task DisposeAsync()
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>
    /// Subir o container custa dezenas de segundos; a collection garante que isso
    /// aconteca uma vez para toda a suite, e nao por classe de teste.
    /// </summary>
    [CollectionDefinition(Nome)]
    public sealed class DatabaseCollection : ICollectionFixture<SqlServerFixture>
    {
        public const string Nome = "sqlserver";
    }
}
