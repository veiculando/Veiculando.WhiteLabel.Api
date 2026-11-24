using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Veiculando.WhiteLabel.Api.Services;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests
{
    /// <summary>
    /// VEI-SUP-57 (teste no preview, 24/11): o AgenciaHandler do Core grava só a Agencia.
    /// O BFF não vinculava, e a agência cadastrada não aparecia na lista. A resposta do
    /// Core também levava ao navegador a conta de serviço com o hash da senha.
    /// </summary>
    [Collection(DatabaseCollection.Nome)]
    public class AgenciasCadastroVinculoTests
    {
        private const int Tenant = 9363;
        private readonly SqlServerFixture _db;

        public AgenciasCadastroVinculoTests(SqlServerFixture db) => _db = db;

        /// <summary>Formato real do Core em produção, com a conta de serviço dentro.</summary>
        private static HttpResponseMessage RespostaDoCore(int id) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"success\":true,\"data\":{\"result\":{\"id\":" + id + ",\"nome\":\"Nova\",\"bonificacaoVolume\":0.1," +
                "\"usuarioCadastro\":{\"nome\":\"Servico\",\"senha\":\"0e70f975cb8baa348d76644cba10a949\"," +
                "\"permissoes\":[{\"permissao\":{\"codigo\":\"CriarAgencia\"}}]}}}}",
                Encoding.UTF8, "application/json")
        };

        private async Task<(WlApiFactory factory, HttpClient cliente)> EntrarAsync()
        {
            await Seed.AfiliadaAsync(Tenant);
            var email = $"sup57v{Guid.NewGuid():N}"[..20] + "@exemplo.com";
            await Seed.OperadorAsync(Tenant, email, new[] { "ClienteGerenciar" });
            var factory = new WlApiFactory(_db, Tenant);
            return (factory, await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao));
        }

        private static object Agencia(string cnpj) =>
            new { cnpj, nome = "Nova", razaoSocial = "Nova Ltda", cidade = "Sao Paulo", uf = "SP", email = "nova@exemplo.com", telefone = "11944774353" };

        [Fact]
        public async Task Cadastro_vincula_a_agencia_e_nao_devolve_a_conta_de_servico()
        {
            var (factory, cliente) = await EntrarAsync();
            using var _ = factory;
            factory.Core.Responder = r => r.Url.Contains("vincular-afiliada")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"success\":true}") }
                : RespostaDoCore(4242);

            var resposta = await cliente.PostAsJsonAsync("/api/wl/agencias", Agencia(ProspeccaoCarteiraTests.Cnpj(Tenant, 1)));
            var corpo = await resposta.Content.ReadAsStringAsync();

            resposta.StatusCode.Should().Be(HttpStatusCode.OK, corpo);
            factory.Core.Requisicoes.Select(r => r.Url).Should().ContainInOrder(
                "http://core.invalido/api/agencia", "http://core.invalido/api/agencia/4242/vincular-afiliada");
            factory.Core.Requisicoes.Last().Corpo.Should().Contain($"\"IdAfiliada\":{Tenant}");
            corpo.Should().Contain("\"id\":4242").And.NotContain("senha").And.NotContain("usuarioCadastro");
        }

        [Fact]
        public async Task Vinculo_recusado_avisa_que_a_agencia_ficou_cadastrada()
        {
            var (factory, cliente) = await EntrarAsync();
            using var _ = factory;
            factory.Core.Responder = r => r.Url.Contains("vincular-afiliada")
                ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"success\":false}") }
                : RespostaDoCore(4243);

            var resposta = await cliente.PostAsJsonAsync("/api/wl/agencias", Agencia(ProspeccaoCarteiraTests.Cnpj(Tenant, 2)));

            resposta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await resposta.Content.ReadAsStringAsync()).Should().Contain("não foi vinculada");
        }

        [Fact]
        public async Task Cadastro_recusado_pelo_core_nao_tenta_vincular()
        {
            var (factory, cliente) = await EntrarAsync();
            using var _ = factory;
            factory.Core.Responder = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"success\":false,\"data\":[{\"message\":\"Você não tem autorização\"}]}")
            };

            var resposta = await cliente.PostAsJsonAsync("/api/wl/agencias", Agencia(ProspeccaoCarteiraTests.Cnpj(Tenant, 3)));

            resposta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            factory.Core.Requisicoes.Should().ContainSingle();
        }

        [Fact]
        public async Task Sem_telefone_ou_email_responde_400_sem_chamar_o_core()
        {
            var (factory, cliente) = await EntrarAsync();
            using var _ = factory;

            var resposta = await cliente.PostAsJsonAsync("/api/wl/agencias", new
            {
                cnpj = ProspeccaoCarteiraTests.Cnpj(Tenant, 4), nome = "Nova", razaoSocial = "Nova Ltda",
                cidade = "Sao Paulo", uf = "SP", email = "nova@exemplo.com", telefone = ""
            });

            resposta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await resposta.Content.ReadAsStringAsync()).Should().Contain("telefone");
            factory.Core.Requisicoes.Should().BeEmpty();
        }

        [Fact]
        public void Limpeza_tira_dados_de_usuario_em_qualquer_nivel_e_mantem_o_registro()
        {
            var limpo = CoreRespostaSegura.Limpar(
                "{\"data\":{\"result\":{\"id\":1,\"usuarios\":[{\"senha\":\"x\"}],\"itens\":[{\"senha\":\"y\",\"nome\":\"a\"}]}}}");

            limpo.Should().Be("{\"data\":{\"result\":{\"id\":1,\"itens\":[{\"nome\":\"a\"}]}}}");
            CoreRespostaSegura.Limpar("não é json").Should().Be("{}");
            CoreRespostaSegura.IdDoResultado("{\"data\":{\"result\":{\"id\":7}}}").Should().Be(7);
            CoreRespostaSegura.IdDoResultado("{\"sucesso\":true}").Should().BeNull();
        }
    }
}
