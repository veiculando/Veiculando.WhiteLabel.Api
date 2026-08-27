using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;
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

        [Theory]
        [InlineData("GET", "/api/wl/usuarios")]
        [InlineData("POST", "/api/wl/usuarios/1/reenviar-convite")]
        [InlineData("GET", "/api/wl/locais")]
        [InlineData("GET", "/api/wl/pecas")]
        [InlineData("GET", "/api/wl/checking/pis-autorizadas")]
        [InlineData("GET", "/api/wl/pedidos-reserva")]
        [InlineData("GET", "/api/wl/pedidos-insercao")]
        [InlineData("POST", "/api/wl/programacao/listar")]
        public async Task Modulos_exigem_a_policy_correspondente_tambem_nas_leituras(
            string metodo,
            string caminho)
        {
            var afiliada = Afiliada + 20 + caminho.Length;
            var email = $"sem-claim-{caminho.Length}@exemplo.com";
            await Seed.OperadorAsync(afiliada, email, new string[0]);

            using var factory = new WlApiFactory(_db, afiliada);
            using var client = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);
            using var request = new HttpRequestMessage(new HttpMethod(metodo), caminho);
            if (metodo == "POST")
                request.Content = JsonContent.Create(new { });

            var resposta = await client.SendAsync(request);

            resposta.StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "esconder o menu no frontend nao substitui autorizacao server-side");
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

        [Fact]
        public async Task Criacao_de_operador_envia_convite_e_primeiro_acesso_define_senha_uma_unica_vez()
        {
            var afiliada = Afiliada + 80;
            var adminEmail = "admin-convite@exemplo.com";
            var convidadoEmail = "convidado@exemplo.com";
            await Seed.OperadorAsync(afiliada, adminEmail, new[] { "UsuarioAfiliadaGerenciar" });

            using var factory = new WlApiFactory(_db, afiliada);
            using var admin = await factory.ClienteAutenticadoAsync(adminEmail, Seed.SenhaPadrao);
            var criacao = await admin.PostAsJsonAsync("/api/wl/usuarios", new
            {
                Nome = "Operador Convidado",
                Email = convidadoEmail,
                Permissoes = new[] { "Checking" }
            });

            criacao.StatusCode.Should().Be(HttpStatusCode.Created);
            factory.EmailSender.Convites.Should().ContainSingle();
            var convite = factory.EmailSender.Convites.Single();
            convite.DestinatarioEmail.Should().Be(convidadoEmail);
            convite.LinkPrimeiroAcesso.Should().StartWith($"https://{factory.Host}/login/primeiro-acesso?");

            using var anonimo = factory.ClienteAnonimo();
            var loginPendente = await anonimo.PostAsJsonAsync("/api/wl/auth/login",
                new { Email = convidadoEmail, Senha = "NovaSenha123" });
            loginPendente.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            var query = QueryHelpers.ParseQuery(new System.Uri(convite.LinkPrimeiroAcesso).Query);
            var token = query["token"].ToString();
            var aceite = await anonimo.PostAsJsonAsync("/api/wl/auth/primeiro-acesso", new
            {
                Email = convidadoEmail,
                Token = token,
                NovaSenha = "NovaSenha123"
            });
            aceite.StatusCode.Should().Be(HttpStatusCode.OK);

            var replay = await anonimo.PostAsJsonAsync("/api/wl/auth/primeiro-acesso", new
            {
                Email = convidadoEmail,
                Token = token,
                NovaSenha = "OutraSenha123"
            });
            replay.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            var loginAceito = await anonimo.PostAsJsonAsync("/api/wl/auth/login",
                new { Email = convidadoEmail, Senha = "NovaSenha123" });
            loginAceito.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task Falha_no_envio_preserva_operador_pendente_para_reenvio()
        {
            var afiliada = Afiliada + 81;
            var adminEmail = "admin-falha-convite@exemplo.com";
            var convidadoEmail = "nao-persistir@exemplo.com";
            await Seed.OperadorAsync(afiliada, adminEmail, new[] { "UsuarioAfiliadaGerenciar" });

            using var factory = new WlApiFactory(_db, afiliada);
            factory.EmailSender.FalharProximoEnvio = true;
            using var admin = await factory.ClienteAutenticadoAsync(adminEmail, Seed.SenhaPadrao);

            var criacao = await admin.PostAsJsonAsync("/api/wl/usuarios", new
            {
                Nome = "Não Persistir",
                Email = convidadoEmail
            });

            criacao.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

            var listagem = await admin.GetFromJsonAsync<UsuarioLista[]>("/api/wl/usuarios");
            var pendente = listagem.Single(u => u.Email == convidadoEmail);
            pendente.StatusConvite.Should().Be("Pendente");
            var reenvio = await admin.PostAsJsonAsync($"/api/wl/usuarios/{pendente.Id}/reenviar-convite", new { });
            reenvio.StatusCode.Should().Be(HttpStatusCode.OK);
            factory.EmailSender.Convites.Should().ContainSingle();
        }

        private sealed record LoginResposta(string Token, int ExpiresInMinutes, string Nome, string Email, string[] Permissoes);
        private sealed record MeResposta(int Id, string Nome, string Email, string[] Permissoes);
        private sealed record UsuarioLista(int Id, string Email, string StatusConvite);
    }
}
