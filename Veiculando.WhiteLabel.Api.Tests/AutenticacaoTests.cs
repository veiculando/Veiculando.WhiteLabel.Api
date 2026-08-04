using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests
{
    /// <summary>
    /// Exercita a factory de ponta a ponta: login real, JWT emitido pelo BFF e
    /// policies de autorizacao aplicadas.
    /// </summary>
    [Collection(DatabaseCollection.Nome)]
    public class AutenticacaoTests
    {
        private const int Afiliada = 7100;
        private readonly SqlServerFixture _db;

        public AutenticacaoTests(SqlServerFixture db) => _db = db;

        [Fact]
        public async Task Login_valido_devolve_token_e_permissoes()
        {
            var email = "login-ok@exemplo.com";
            await Seed.OperadorAsync(Afiliada, email, new[] { "PecaGerenciar", "Checking" });

            using var factory = new WlApiFactory(_db, Afiliada);
            using var client = factory.ClienteAnonimo();

            var resposta = await client.PostAsJsonAsync("/api/wl/auth/login",
                new { Email = email, Senha = Seed.SenhaPadrao });

            resposta.StatusCode.Should().Be(HttpStatusCode.OK);

            var corpo = await resposta.Content.ReadFromJsonAsync<LoginResposta>();
            corpo.Should().NotBeNull();
            corpo!.Token.Should().NotBeNullOrWhiteSpace();
            corpo.Permissoes.Should().BeEquivalentTo("PecaGerenciar", "Checking");
        }

        [Fact]
        public async Task Login_com_senha_errada_devolve_401()
        {
            var email = "senha-errada@exemplo.com";
            await Seed.OperadorAsync(Afiliada + 1, email);

            using var factory = new WlApiFactory(_db, Afiliada + 1);
            using var client = factory.ClienteAnonimo();

            var resposta = await client.PostAsJsonAsync("/api/wl/auth/login",
                new { Email = email, Senha = "senha-que-nao-e-a-dele" });

            resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task Operador_de_outra_afiliada_nao_loga_nesta_instancia()
        {
            var email = "de-outra-casa@exemplo.com";
            await Seed.OperadorAsync(Afiliada + 2, email);

            // A instancia representa a afiliada +3, o operador pertence a +2.
            // O login filtra por AfiliadaId na propria query, entao nao ha token.
            using var factory = new WlApiFactory(_db, Afiliada + 3);
            using var client = factory.ClienteAnonimo();

            var resposta = await client.PostAsJsonAsync("/api/wl/auth/login",
                new { Email = email, Senha = Seed.SenhaPadrao });

            resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task Endpoint_protegido_sem_token_devolve_401()
        {
            using var factory = new WlApiFactory(_db, Afiliada + 4);
            using var client = factory.ClienteAnonimo();

            var resposta = await client.GetAsync("/api/wl/usuarios");

            resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task Operador_sem_a_permissao_recebe_403_no_endpoint_de_escrita()
        {
            var email = "sem-permissao@exemplo.com";
            await Seed.OperadorAsync(Afiliada + 5, email, new string[0]);

            using var factory = new WlApiFactory(_db, Afiliada + 5);
            using var client = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);

            // Autenticado, mas sem UsuarioAfiliadaGerenciar. Antes do TP-R2 os
            // controllers usavam [Authorize] generico e isso passava com 200 —
            // menu escondido no frontend nao protege a API.
            var resposta = await client.PostAsJsonAsync("/api/wl/usuarios", new
            {
                Nome = "Novo",
                Email = "novo@exemplo.com",
                Senha = "SenhaDeTeste123",
            });

            resposta.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        [Fact]
        public async Task Me_devolve_o_operador_autenticado()
        {
            var email = "me@exemplo.com";
            await Seed.OperadorAsync(Afiliada + 6, email, new[] { "Checking" }, nome: "Fulano de Tal");

            using var factory = new WlApiFactory(_db, Afiliada + 6);
            using var client = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);

            var resposta = await client.GetAsync("/api/wl/auth/me");

            resposta.StatusCode.Should().Be(HttpStatusCode.OK);

            var corpo = await resposta.Content.ReadFromJsonAsync<MeResposta>();
            corpo!.Nome.Should().Be("Fulano de Tal");
            corpo.Email.Should().Be(email);
            corpo.Permissoes.Should().BeEquivalentTo("Checking");
        }

        private sealed record LoginResposta(string Token, int ExpiresInMinutes, string Nome, string Email, string[] Permissoes);
        private sealed record MeResposta(int Id, string Nome, string Email, string[] Permissoes);
    }
}
