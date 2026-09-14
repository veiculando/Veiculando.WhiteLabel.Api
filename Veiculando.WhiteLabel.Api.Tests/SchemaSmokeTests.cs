using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities.WhiteLabel;
using Veiculando.Domain.Enums;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;
using BC = BCrypt.Net.BCrypt;

namespace Veiculando.WhiteLabel.Api.Tests
{
    /// <summary>
    /// Prova que a fixture entrega um banco utilizavel antes de qualquer outra
    /// suite depender disso.
    /// </summary>
    [Collection(DatabaseCollection.Nome)]
    public class SchemaSmokeTests
    {
        private readonly SqlServerFixture _db;

        public SchemaSmokeTests(SqlServerFixture db) => _db = db;

        [Fact]
        public void Schema_foi_criado_com_as_tabelas_da_hierarquia_wl()
        {
            using var ctx = new VeiculandoDataContext();

            // A hierarquia WlUsuario e TPT: uma tabela por tipo. Se o mapeamento
            // estiver quebrado, isso falha aqui e nao no meio de um teste de API.
            ctx.Database.SqlQuery<int>(
                    "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME IN ('WL_Usuario','WL_UsuarioAfiliada','WL_UsuarioAnunciante')")
                .Single()
                .Should().Be(3, "a hierarquia TPT de WlUsuario precisa das tres tabelas");
        }

        [Fact]
        public async Task Persiste_e_le_um_operador_com_permissoes()
        {
            const int afiliadaId = 9001;
            var email = $"smoke-{afiliadaId}@exemplo.com";

            using (var ctx = new VeiculandoDataContext())
            {
                var operador = new WlUsuarioAfiliada(
                    nome: "Operador Smoke",
                    email: email,
                    senhaHash: BC.HashPassword("SenhaDeTeste123"),
                    afiliadaId: afiliadaId,
                    permissoes: new[] { "PecaGerenciar", "Checking" });

                operador.IsValid().Should().BeTrue(
                    "o construtor nao deveria gerar notificacao: {0}",
                    string.Join(" | ", operador.Notifications.Select(n => n.Message)));

                ctx.WlUsuariosAfiliada.Add(operador);
                await ctx.SaveChangesAsync();
            }

            using (var ctx = new VeiculandoDataContext())
            {
                var lido = await ctx.WlUsuariosAfiliada
                    .AsNoTracking()
                    .FirstOrDefaultAsync(u => u.Email.Endereco == email && u.AfiliadaId == afiliadaId);

                lido.Should().NotBeNull();
                lido!.Nome.Should().Be("Operador Smoke");
                lido.StatusExibicao.Should().Be(StatusExibicaoEnum.Ativo);
                lido.ObterPermissoes().Should().BeEquivalentTo("PecaGerenciar", "Checking");
            }
        }

        [Fact]
        public async Task Indice_unico_de_email_por_afiliada_e_aplicado()
        {
            const int afiliadaId = 9002;
            var email = $"duplicado-{afiliadaId}@exemplo.com";

            using var ctx = new VeiculandoDataContext();

            ctx.WlUsuariosAfiliada.Add(new WlUsuarioAfiliada(
                "Primeiro", email, BC.HashPassword("SenhaDeTeste123"), afiliadaId));
            await ctx.SaveChangesAsync();

            ctx.WlUsuariosAfiliada.Add(new WlUsuarioAfiliada(
                "Segundo", email, BC.HashPassword("SenhaDeTeste123"), afiliadaId));

            // UK_WlUsuario_Email_Afiliada nao tem filtro por status. E exatamente
            // por isso que UsuariosController.Create checa duplicidade em TODOS os
            // status: filtrar por Ativo deixava o cadastro passar na validacao e
            // estourar aqui, virando 500.
            var acao = async () => await ctx.SaveChangesAsync();
            await acao.Should().ThrowAsync<System.Data.Entity.Infrastructure.DbUpdateException>();
        }
    }
}
