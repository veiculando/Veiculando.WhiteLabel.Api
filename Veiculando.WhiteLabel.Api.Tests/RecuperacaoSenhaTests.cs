using System;
using System.Data.Entity;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities.WhiteLabel;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests
{
    /// <summary>
    /// Fluxo de recuperação de senha por tenant: <c>POST /api/wl/auth/esqueci-senha</c>
    /// e <c>POST /api/wl/auth/alterar-senha</c>.
    /// </summary>
    [Collection(DatabaseCollection.Nome)]
    public class RecuperacaoSenhaTests
    {
        private const int Afiliada = 7200;
        private const string RespostaGenericaEsperada =
            "Se o e-mail informado estiver cadastrado nesta instância, enviaremos instruções para redefinir a senha.";
        private readonly SqlServerFixture _db;

        public RecuperacaoSenhaTests(SqlServerFixture db) => _db = db;

        [Fact]
        public async Task Fluxo_completo_gera_token_envia_email_e_troca_a_senha()
        {
            var afiliadaId = Afiliada;
            var email = "recuperacao-ok@exemplo.com";
            await Seed.OperadorAsync(afiliadaId, email);
            await Seed.BrandingAsync(afiliadaId, "Marca de Teste", "https://logo.teste/x.png", "#112233");

            using var factory = new WlApiFactory(_db, afiliadaId);
            using var client = factory.ClienteAnonimo();

            var respostaEsqueci = await client.PostAsJsonAsync("/api/wl/auth/esqueci-senha", new { Email = email });
            respostaEsqueci.StatusCode.Should().Be(HttpStatusCode.OK);

            var corpo = await respostaEsqueci.Content.ReadFromJsonAsync<RespostaGenerica>();
            corpo!.Message.Should().Be(RespostaGenericaEsperada);

            factory.EmailSender.Envios.Should().HaveCount(1);
            var envio = factory.EmailSender.Envios.Single();
            envio.DestinatarioEmail.Should().Be(email);
            envio.NomeExibicaoMarca.Should().Be("Marca de Teste", "o template identifica a marca via WL_Configuracao do tenant");

            // A URL de reset usa o WlDominio Active do próprio tenant — nunca uma
            // URL vinda do cliente, que nem participa da requisição.
            var uri = new Uri(envio.LinkReset);
            uri.Host.Should().Be(factory.Host);
            uri.Scheme.Should().Be("https");

            var query = QueryHelpers.ParseQuery(uri.Query);
            var token = query["token"].ToString();
            token.Should().NotBeNullOrWhiteSpace();
            query["email"].ToString().Should().Be(email);

            // O banco guarda só o hash — nunca o token bruto que foi ao e-mail.
            using (var ctx = new VeiculandoDataContext())
            {
                var registro = await ctx.WlUsuariosAfiliada.AsNoTracking()
                    .SingleAsync(u => u.Email.Endereco == email && u.AfiliadaId == afiliadaId);
                registro.TokenRecuperacaoHash.Should().NotBeNullOrWhiteSpace();
                registro.TokenRecuperacaoHash.Should().NotBe(token);
            }

            const string senhaNova = "NovaSenha456";
            var respostaAlterar = await client.PostAsJsonAsync("/api/wl/auth/alterar-senha",
                new { Email = email, Token = token, NovaSenha = senhaNova });
            respostaAlterar.StatusCode.Should().Be(HttpStatusCode.OK);

            // Senha antiga deixa de autenticar, a nova autentica.
            var loginAntigo = await client.PostAsJsonAsync("/api/wl/auth/login", new { Email = email, Senha = Seed.SenhaPadrao });
            loginAntigo.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            var loginNovo = await client.PostAsJsonAsync("/api/wl/auth/login", new { Email = email, Senha = senhaNova });
            loginNovo.StatusCode.Should().Be(HttpStatusCode.OK);

            // Uso único: o mesmo token não serve de novo.
            var segundaTentativa = await client.PostAsJsonAsync("/api/wl/auth/alterar-senha",
                new { Email = email, Token = token, NovaSenha = "OutraSenha789" });
            segundaTentativa.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Fact]
        public async Task Email_inexistente_devolve_a_mesma_resposta_200_e_nao_envia_nada()
        {
            var afiliadaId = Afiliada + 1;
            using var factory = new WlApiFactory(_db, afiliadaId);
            using var client = factory.ClienteAnonimo();

            var resposta = await client.PostAsJsonAsync("/api/wl/auth/esqueci-senha",
                new { Email = "nunca-existiu@exemplo.com" });

            resposta.StatusCode.Should().Be(HttpStatusCode.OK);
            var corpo = await resposta.Content.ReadFromJsonAsync<RespostaGenerica>();
            corpo!.Message.Should().Be(RespostaGenericaEsperada);

            factory.EmailSender.Envios.Should().BeEmpty();
        }

        [Fact]
        public async Task Token_gerado_em_um_tenant_nao_funciona_em_outro_ainda_que_o_email_coincida()
        {
            var afiliadaA = Afiliada + 2;
            var afiliadaB = Afiliada + 3;
            var email = "mesmo-email-duas-instancias@exemplo.com";

            await Seed.OperadorAsync(afiliadaA, email);
            await Seed.OperadorAsync(afiliadaB, email);

            using var factoryA = new WlApiFactory(_db, afiliadaA);
            using var clienteA = factoryA.ClienteAnonimo();

            await clienteA.PostAsJsonAsync("/api/wl/auth/esqueci-senha", new { Email = email });
            var linkA = factoryA.EmailSender.Envios.Single().LinkReset;
            var tokenA = QueryHelpers.ParseQuery(new Uri(linkA).Query)["token"].ToString();

            using var factoryB = new WlApiFactory(_db, afiliadaB);
            using var clienteB = factoryB.ClienteAnonimo();

            var resposta = await clienteB.PostAsJsonAsync("/api/wl/auth/alterar-senha",
                new { Email = email, Token = tokenA, NovaSenha = "SenhaCruzada123" });

            resposta.StatusCode.Should().Be(HttpStatusCode.BadRequest,
                "o token pertence ao registro da afiliada A; o de B nunca teve esse hash gravado");

            // A senha do operador B continua a original.
            var loginB = await clienteB.PostAsJsonAsync("/api/wl/auth/login", new { Email = email, Senha = Seed.SenhaPadrao });
            loginB.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task Falha_no_envio_do_email_invalida_o_token_e_ainda_devolve_a_resposta_generica()
        {
            var afiliadaId = Afiliada + 4;
            var email = "falha-envio@exemplo.com";
            await Seed.OperadorAsync(afiliadaId, email);

            using var factory = new WlApiFactory(_db, afiliadaId);
            factory.EmailSender.FalharProximoEnvio = true;
            using var client = factory.ClienteAnonimo();

            var resposta = await client.PostAsJsonAsync("/api/wl/auth/esqueci-senha", new { Email = email });

            // Não vira 500: a falha de transporte é absorvida e a resposta segue genérica.
            resposta.StatusCode.Should().Be(HttpStatusCode.OK);
            var corpo = await resposta.Content.ReadFromJsonAsync<RespostaGenerica>();
            corpo!.Message.Should().Be(RespostaGenericaEsperada);

            factory.EmailSender.Envios.Should().BeEmpty();

            using var ctx = new VeiculandoDataContext();
            var registro = await ctx.WlUsuariosAfiliada.AsNoTracking()
                .SingleAsync(u => u.Email.Endereco == email && u.AfiliadaId == afiliadaId);
            registro.TokenRecuperacaoHash.Should().BeNull("um token que ninguém recebeu não pode continuar válido");
        }

        [Fact]
        public async Task Limite_por_hash_de_email_bloqueia_apos_tres_tentativas_na_janela()
        {
            var afiliadaId = Afiliada + 5;
            var email = "limite-por-email@exemplo.com";
            await Seed.OperadorAsync(afiliadaId, email);

            using var factory = new WlApiFactory(_db, afiliadaId);
            using var client = factory.ClienteAnonimo();

            for (var i = 0; i < 4; i++)
            {
                var resposta = await client.PostAsJsonAsync("/api/wl/auth/esqueci-senha", new { Email = email });
                resposta.StatusCode.Should().Be(HttpStatusCode.OK,
                    "o limite por e-mail nunca muda o formato da resposta, só suprime o envio");
            }

            // Só as 3 primeiras dispararam e-mail de verdade; a 4ª foi absorvida
            // pela segunda camada (IPasswordResetAttemptGuard, por hash do e-mail).
            factory.EmailSender.Envios.Should().HaveCount(3);
        }

        [Theory]
        [InlineData(null, "token-qualquer", "SenhaValida123")]
        [InlineData("email@exemplo.com", null, "SenhaValida123")]
        [InlineData("email@exemplo.com", "token-qualquer", "curta")]
        public async Task Alterar_senha_valida_campos_obrigatorios_e_tamanho_minimo(string email, string token, string novaSenha)
        {
            using var factory = new WlApiFactory(_db, Afiliada + 6);
            using var client = factory.ClienteAnonimo();

            var resposta = await client.PostAsJsonAsync("/api/wl/auth/alterar-senha",
                new { Email = email, Token = token, NovaSenha = novaSenha });

            resposta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Fact]
        public async Task Alterar_senha_com_token_invalido_devolve_erro_generico_sem_revelar_se_o_email_existe()
        {
            var afiliadaId = Afiliada + 7;
            var email = "existe@exemplo.com";
            await Seed.OperadorAsync(afiliadaId, email);

            using var factory = new WlApiFactory(_db, afiliadaId);
            using var client = factory.ClienteAnonimo();

            var respostaExistente = await client.PostAsJsonAsync("/api/wl/auth/alterar-senha",
                new { Email = email, Token = "token-nunca-emitido", NovaSenha = "SenhaValida123" });

            var respostaInexistente = await client.PostAsJsonAsync("/api/wl/auth/alterar-senha",
                new { Email = "nao-cadastrado@exemplo.com", Token = "token-nunca-emitido", NovaSenha = "SenhaValida123" });

            respostaExistente.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            respostaInexistente.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            var corpoExistente = await respostaExistente.Content.ReadFromJsonAsync<RespostaGenerica>();
            var corpoInexistente = await respostaInexistente.Content.ReadFromJsonAsync<RespostaGenerica>();
            corpoExistente!.Message.Should().Be(corpoInexistente!.Message);
        }

        private sealed record RespostaGenerica(string Message);
    }
}
