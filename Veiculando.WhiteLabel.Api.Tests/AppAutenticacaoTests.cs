using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Veiculando.Data.Contexts;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests
{
    [Collection(DatabaseCollection.Nome)]
    public class AppAutenticacaoTests
    {
        private readonly SqlServerFixture _db;
        public AppAutenticacaoTests(SqlServerFixture db) => _db = db;

        [Fact]
        public async Task Anunciante_logado_pode_ler_somente_a_propria_sessao()
        {
            const int afiliada = 811;
            const string email = "anunciante-app-11701@exemplo.com";
            await Seed.AnuncianteAsync(afiliada, email, "12345678000190");

            using var factory = new WlApiFactory(_db, afiliada);
            using var client = factory.ClienteAnonimo();
            var login = await client.PostAsJsonAsync("/api/wl/app/auth/login", new { email, password = Seed.SenhaPadrao });
            login.StatusCode.Should().Be(HttpStatusCode.OK);
            var sessao = await login.Content.ReadFromJsonAsync<AppSession>();
            sessao.Token.Should().NotBeNullOrWhiteSpace();
            sessao.KycStatus.Should().Be("incomplete");
            sessao.AccountType.Should().Be("pj");

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", sessao.Token);
            var me = await client.GetAsync("/api/wl/app/auth/me");
            me.StatusCode.Should().Be(HttpStatusCode.OK);
            (await me.Content.ReadFromJsonAsync<AppMe>()).Email.Should().Be(email);
        }

        [Fact]
        public async Task Credenciais_de_operador_nao_entram_no_App_e_vice_versa()
        {
            const int afiliada = 812;
            const string operador = "operador-app-11702@exemplo.com";
            const string anunciante = "anunciante-app-11702@exemplo.com";
            await Seed.OperadorAsync(afiliada, operador);
            await Seed.AnuncianteAsync(afiliada, anunciante);

            using var factory = new WlApiFactory(_db, afiliada);
            using var client = factory.ClienteAnonimo();
            (await client.PostAsJsonAsync("/api/wl/app/auth/login", new { email = operador, password = Seed.SenhaPadrao })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await client.PostAsJsonAsync("/api/wl/auth/login", new { email = anunciante, senha = Seed.SenhaPadrao })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            using var operadorAutenticado = await factory.ClienteAutenticadoAsync(operador, Seed.SenhaPadrao);
            (await operadorAutenticado.GetAsync("/api/wl/app/auth/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task Host_de_outra_afiliada_nao_aceita_login_nem_token_do_anunciante()
        {
            const int origem = 813;
            const int destino = 814;
            const string email = "anunciante-app-11703@exemplo.com";
            await Seed.AnuncianteAsync(origem, email);

            using var factoryOrigem = new WlApiFactory(_db, origem);
            using var clienteOrigem = factoryOrigem.ClienteAnonimo();
            var login = await clienteOrigem.PostAsJsonAsync("/api/wl/app/auth/login", new { email, password = Seed.SenhaPadrao });
            login.StatusCode.Should().Be(HttpStatusCode.OK);
            var sessao = await login.Content.ReadFromJsonAsync<AppSession>();

            using var factoryDestino = new WlApiFactory(_db, destino);
            using var clienteDestino = factoryDestino.ClienteAnonimo();
            (await clienteDestino.PostAsJsonAsync("/api/wl/app/auth/login", new { email, password = Seed.SenhaPadrao })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            clienteDestino.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", sessao.Token);
            (await clienteDestino.GetAsync("/api/wl/app/auth/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task Somente_anunciante_ativo_do_tenant_pode_renovar_a_sessao()
        {
            const int afiliada = 816;
            const string anunciante = "anunciante-app-11705@exemplo.com";
            const string operador = "operador-app-11705@exemplo.com";
            await Seed.AnuncianteAsync(afiliada, anunciante);
            await Seed.OperadorAsync(afiliada, operador);

            using var factory = new WlApiFactory(_db, afiliada);
            using var client = factory.ClienteAnonimo();
            var login = await client.PostAsJsonAsync("/api/wl/app/auth/login", new { email = anunciante, password = Seed.SenhaPadrao });
            var sessao = await login.Content.ReadFromJsonAsync<AppSession>();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", sessao.Token);

            var refresh = await client.PostAsync("/api/wl/app/auth/refresh", null);
            refresh.StatusCode.Should().Be(HttpStatusCode.OK);
            (await refresh.Content.ReadFromJsonAsync<AppSession>()).Token.Should().NotBe(sessao.Token);
            (await client.GetAsync("/api/wl/app/auth/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            using var operadorAutenticado = await factory.ClienteAutenticadoAsync(operador, Seed.SenhaPadrao);
            (await operadorAutenticado.PostAsync("/api/wl/app/auth/refresh", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task Conta_excluida_perde_acesso_mesmo_com_token_ainda_valido()
        {
            const int afiliada = 815;
            const string email = "anunciante-app-11704@exemplo.com";
            var id = await Seed.AnuncianteAsync(afiliada, email);

            using var factory = new WlApiFactory(_db, afiliada);
            using var client = factory.ClienteAnonimo();
            var login = await client.PostAsJsonAsync("/api/wl/app/auth/login", new { email, password = Seed.SenhaPadrao });
            login.StatusCode.Should().Be(HttpStatusCode.OK);
            var sessao = await login.Content.ReadFromJsonAsync<AppSession>();

            using (var ctx = new VeiculandoDataContext())
            {
                var anunciante = await ctx.WlUsuariosAnunciante.FindAsync(id);
                anunciante.Deletar();
                await ctx.SaveChangesAsync();
            }

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", sessao.Token);
            (await client.GetAsync("/api/wl/app/auth/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await client.PostAsJsonAsync("/api/wl/app/auth/login", new { email, password = Seed.SenhaPadrao })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task Recuperacao_do_anunciante_nao_enumera_conta_e_consume_o_token_uma_vez()
        {
            const int afiliada = 817;
            const string email = "anunciante-app-11706@exemplo.com";
            const string senhaNova = "SenhaNova11706";
            await Seed.AnuncianteAsync(afiliada, email);

            using var factory = new WlApiFactory(_db, afiliada);
            using var client = factory.ClienteAnonimo();
            var existente = await client.PostAsJsonAsync("/api/wl/app/auth/forgot-password", new { email });
            var inexistente = await client.PostAsJsonAsync("/api/wl/app/auth/forgot-password", new { email = "nao-existe-11706@exemplo.com" });
            existente.StatusCode.Should().Be(HttpStatusCode.OK);
            inexistente.StatusCode.Should().Be(HttpStatusCode.OK);
            (await existente.Content.ReadAsStringAsync()).Should().Be(await inexistente.Content.ReadAsStringAsync());

            var envio = factory.EmailSender.Envios.Should().ContainSingle().Which;
            var uri = new System.Uri(envio.LinkReset);
            uri.Host.Should().Be(factory.Host);
            uri.AbsolutePath.Should().Be("/esqueci-senha");
            var query = QueryHelpers.ParseQuery(uri.Query);
            var token = query["token"].ToString();

            var reset = await client.PostAsJsonAsync("/api/wl/app/auth/reset-password", new { email, token, newPassword = senhaNova });
            reset.StatusCode.Should().Be(HttpStatusCode.OK);
            (await client.PostAsJsonAsync("/api/wl/app/auth/login", new { email, password = Seed.SenhaPadrao })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await client.PostAsJsonAsync("/api/wl/app/auth/login", new { email, password = senhaNova })).StatusCode.Should().Be(HttpStatusCode.OK);
            var reusedTokenPassword = "OutraSenha" + 11706;
            (await client.PostAsJsonAsync("/api/wl/app/auth/reset-password", new { email, token, newPassword = reusedTokenPassword })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        private sealed record AppSession(string Token, int ExpiresInMinutes, string Name, string Email, string AccountType, string KycStatus);
        private sealed record AppMe(string Name, string Email, string AccountType, string KycStatus);
    }
}
