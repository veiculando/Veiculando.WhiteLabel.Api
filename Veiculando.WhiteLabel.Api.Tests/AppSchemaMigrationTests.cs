using System;
using System.Data.Entity.Infrastructure;
using System.Data.Entity.Migrations;
using System.Data.Entity.Migrations.Infrastructure;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Veiculando.Data.Contexts;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests;

[Collection(DatabaseCollection.Nome)]
public sealed class AppSchemaMigrationTests
{
    private readonly SqlServerFixture _db;
    public AppSchemaMigrationTests(SqlServerFixture db) => _db = db;

    [Fact]
    public async Task Migration_cria_schema_App_a_partir_do_baseline_sem_alterar_tabelas_legadas()
    {
        var target = new SqlConnectionStringBuilder(_db.ConnectionString);
        target.InitialCatalog.Should().Be("VeiculandoTests");
        target.DataSource.Split(',')[0].Should().BeOneOf("127.0.0.1", "localhost");
        var config = new Veiculando.Data.Migrations.Configuration
        {
            TargetDatabase = new DbConnectionInfo(_db.ConnectionString, "System.Data.SqlClient")
        };
        var metadata = typeof(VeiculandoDataContext).Assembly.GetTypes()
            .Where(type => typeof(IMigrationMetadata).IsAssignableFrom(type) && !type.IsAbstract)
            .Select(type => (IMigrationMetadata)Activator.CreateInstance(type)).ToArray();
        var app = metadata.Single(m => m.Id.EndsWith("_AddWlAppIdentidadeSessaoKyc", StringComparison.Ordinal));
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            // Apenas o banco descartável desta fixture: desfaz as seis tabelas
            // geradas pelo modelo para testar o Up real, não um falso verde do modelo.
            command.CommandText = @"
DROP TABLE dbo.WL_AppDocumento;
DROP TABLE dbo.WL_AppKycEvento;
DROP TABLE dbo.WL_AppOnboarding;
DROP TABLE dbo.WL_AppSessao;
DROP TABLE dbo.WL_AppIdentidade;
DROP TABLE dbo.WL_AppAcessoSolicitacao;
CREATE TABLE dbo.__MigrationHistory (
 MigrationId nvarchar(150) NOT NULL, ContextKey nvarchar(300) NOT NULL,
 Model varbinary(max) NOT NULL, ProductVersion nvarchar(32) NOT NULL,
 CONSTRAINT PK_dbo_MigrationHistory PRIMARY KEY (MigrationId, ContextKey));";
            await command.ExecuteNonQueryAsync();
        }
        foreach (var baseline in metadata.Where(m => string.CompareOrdinal(m.Id, "202608261858519_PrimeiroAcessoWlUsuario") <= 0))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT dbo.__MigrationHistory (MigrationId,ContextKey,Model,ProductVersion) VALUES (@id,@context,@model,'6.4.4')";
            command.Parameters.AddWithValue("@id", baseline.Id);
            command.Parameters.AddWithValue("@context", config.ContextKey);
            command.Parameters.AddWithValue("@model", Convert.FromBase64String(baseline.Target));
            await command.ExecuteNonQueryAsync();
        }
        var migrator = new DbMigrator(config);
        migrator.GetPendingMigrations().Should().Contain(app.Id);
        migrator.Update(app.Id);
        migrator.GetDatabaseMigrations().Should().Contain(app.Id);
        await using var verify = connection.CreateCommand();
        verify.CommandText = "SELECT COUNT(*) FROM sys.tables WHERE name IN ('WL_AppIdentidade','WL_AppSessao','WL_AppOnboarding','WL_AppKycEvento','WL_AppDocumento','WL_AppAcessoSolicitacao')";
        Convert.ToInt32(await verify.ExecuteScalarAsync()).Should().Be(6);
        verify.CommandText = "SELECT COUNT(*) FROM sys.indexes WHERE name IN ('UK_AppOnboarding_Usuario','UK_AppAcesso_UsuarioDocumento') AND is_unique=1";
        Convert.ToInt32(await verify.ExecuteScalarAsync()).Should().Be(2);
        verify.CommandText = "SELECT max_length FROM sys.columns WHERE object_id=OBJECT_ID('dbo.WL_AppOnboarding') AND name='DadosJson'";
        Convert.ToInt32(await verify.ExecuteScalarAsync()).Should().Be(-1);
    }
}
