using System;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Veiculando.Data.Contexts;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests
{
    /// <summary>
    /// VEI-SUP-57: em produção a Agencia de venda direta da Aurum está gravada com
    /// máscara ('04.889.908/0001-59'). A busca por CNPJ comparava só dígitos, não
    /// achava a agência, liberava o cadastro e o Core recusava com 400.
    /// </summary>
    [Collection(DatabaseCollection.Nome)]
    public class AgenciasCnpjMascaradoTests
    {
        private const int Tenant = 9362;
        private readonly SqlServerFixture _db;

        public AgenciasCnpjMascaradoTests(SqlServerFixture db) => _db = db;

        private static string Mascara(string c) => $"{c[..2]}.{c[2..5]}.{c[5..8]}/{c[8..12]}-{c[12..]}";

        [Fact]
        public async Task Cnpj_gravado_com_mascara_e_encontrado_e_nao_e_cadastrado_de_novo()
        {
            await Seed.AfiliadaAsync(Tenant);
            var cnpj = ProspeccaoCarteiraTests.Cnpj(Tenant, 57);
            using (var ctx = new VeiculandoDataContext())
            {
                await ctx.Database.ExecuteSqlCommandAsync(@"
INSERT INTO Agencia (Nome, Cnpj, Email, AvaliacaoMedia, BonificacaoVolume, DataCadastro, DataAtualizacao, StatusExibicao)
VALUES ('Agencia Mascarada', @p0, 'mascarada@exemplo.com', 0, 0, GETDATE(), GETDATE(), 1);", Mascara(cnpj));
            }

            var email = $"sup57{Guid.NewGuid():N}"[..20] + "@exemplo.com";
            await Seed.OperadorAsync(Tenant, email, new[] { "ClienteGerenciar" });
            using var factory = new WlApiFactory(_db, Tenant);
            var cliente = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);

            var busca = await cliente.GetAsync($"/api/wl/agencias/por-cnpj/{cnpj}");
            busca.StatusCode.Should().Be(HttpStatusCode.OK, await busca.Content.ReadAsStringAsync());

            var cadastro = await cliente.PostAsJsonAsync("/api/wl/agencias", new
            {
                cnpj, nome = "Outra", razaoSocial = "Outra Ltda", cidade = "Sao Paulo", uf = "SP"
            });
            cadastro.StatusCode.Should().Be(HttpStatusCode.Conflict, await cadastro.Content.ReadAsStringAsync());
        }
    }
}
