using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests
{
    [Collection(DatabaseCollection.Nome)]
    public class CadastroAcessoOperadorWlTests
    {
        private readonly SqlServerFixture _db;

        public CadastroAcessoOperadorWlTests(SqlServerFixture db) => _db = db;

        [Fact]
        public async Task Operador_wl_salva_politica_e_aparece_no_historico_sem_usuario_legado()
        {
            const int afiliadaId = 9361;
            const string nome = "Operador de Politica WL";
            const string email = "politica-wl@exemplo.com";

            await Seed.AfiliadaAsync(afiliadaId);
            await Seed.OperadorAsync(afiliadaId, email,
                new[] { "UsuarioAfiliadaGerenciar" }, nome);

            using var factory = new WlApiFactory(_db, afiliadaId);
            using var client = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);

            var resposta = await client.PutAsJsonAsync("/api/wl/config/cadastro-acesso",
                new { ExigirEmailCorporativoNoCadastro = true });

            resposta.StatusCode.Should().Be(HttpStatusCode.OK);

            using var documento = JsonDocument.Parse(
                await client.GetStringAsync("/api/wl/config/cadastro-acesso"));
            var estado = documento.RootElement;
            estado.GetProperty("exigirEmailCorporativoNoCadastro").GetBoolean().Should().BeTrue();
            var historico = estado.GetProperty("historico");
            historico.GetArrayLength().Should().Be(1);
            historico[0].GetProperty("usuario").GetString().Should().Be(nome);
            historico[0].GetProperty("valorNovo").GetString().Should().Be("Ativo");

            using var politica = JsonDocument.Parse(
                await client.GetStringAsync("/api/wl/app/auth/policy"));
            politica.RootElement.GetProperty("requireCorporateEmail").GetBoolean().Should().BeTrue();

            var cadastroPessoal = await client.PostAsJsonAsync("/api/wl/app/auth/register", new
            {
                Name = "Comprador com email pessoal",
                Email = "comprador.pessoal@gmail.com",
                Password = "SenhaDeTeste123",
                Phone = "11999990000",
                AcceptedTerms = true,
                TermsVersion = "1.0",
                PrivacyVersion = "1.0"
            });
            cadastroPessoal.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
    }
}
