using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests
{
    /// <summary>
    /// Verifica que um operador da exibidora A nunca alcanca recurso da B.
    /// </summary>
    /// <remarks>
    /// <para>Esta e a suite que precisa existir ANTES da refatoracao para
    /// <c>ITenantQueries</c>. Hoje o isolamento depende de cada endpoint lembrar
    /// de filtrar por <c>IdAfiliada</c> na query — disciplina repetida em mais de
    /// uma dezena de lugares. A refatoracao troca disciplina por estrutura, e
    /// estes testes sao a rede que garante que o comportamento nao muda no
    /// caminho.</para>
    ///
    /// <para><b>Como o ataque e modelado.</b> O <c>TenantMiddleware</c> ignora o
    /// header <c>X-Tenant-AfiliadaId</c> e usa sempre a config da instancia, entao
    /// nao adianta forjar header. A superficie real e outra: o operador esta
    /// legitimamente autenticado na instancia da afiliada A e informa o <b>id</b>
    /// de um recurso da B numa rota. E isso que cada teste faz.</para>
    ///
    /// <para><b>404 e nao 403 e o correto aqui.</b> Responder 403 confirmaria que
    /// o id existe em outra exibidora, o que ja e vazamento — o operador poderia
    /// enumerar ids e mapear o inventario alheio. O filtro por afiliada na propria
    /// query faz o registro simplesmente nao existir para quem pergunta.</para>
    /// </remarks>
    [Collection(DatabaseCollection.Nome)]
    public class IsolamentoTenantTests
    {
        private const int AfiliadaA = 8100;
        private const int AfiliadaB = 8200;

        private readonly SqlServerFixture _db;

        public IsolamentoTenantTests(SqlServerFixture db) => _db = db;

        [Fact]
        public async Task Operador_de_A_nao_le_operador_de_B()
        {
            var admin = "iso-admin-a@exemplo.com";
            await Seed.OperadorAsync(AfiliadaA, admin, new[] { "UsuarioAfiliadaGerenciar" });

            var alvoId = await Seed.OperadorAsync(AfiliadaB, "iso-alvo-b@exemplo.com");

            using var factory = new WlApiFactory(_db, AfiliadaA);
            using var client = await factory.ClienteAutenticadoAsync(admin, Seed.SenhaPadrao);

            var resposta = await client.GetAsync($"/api/wl/usuarios/{alvoId}");

            resposta.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Fact]
        public async Task Operador_de_A_nao_edita_operador_de_B()
        {
            var admin = "iso-edit-a@exemplo.com";
            await Seed.OperadorAsync(AfiliadaA, admin, new[] { "UsuarioAfiliadaGerenciar" });

            var alvoId = await Seed.OperadorAsync(AfiliadaB, "iso-edit-b@exemplo.com", nome: "Nome Original");

            using var factory = new WlApiFactory(_db, AfiliadaA);
            using var client = await factory.ClienteAutenticadoAsync(admin, Seed.SenhaPadrao);

            var resposta = await client.PutAsJsonAsync($"/api/wl/usuarios/{alvoId}",
                new { Nome = "Sequestrado" });

            resposta.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Fact]
        public async Task Operador_de_A_nao_exclui_operador_de_B()
        {
            var admin = "iso-del-a@exemplo.com";
            await Seed.OperadorAsync(AfiliadaA, admin, new[] { "UsuarioAfiliadaGerenciar" });

            var alvoId = await Seed.OperadorAsync(AfiliadaB, "iso-del-b@exemplo.com");

            using var factory = new WlApiFactory(_db, AfiliadaA);
            using var client = await factory.ClienteAutenticadoAsync(admin, Seed.SenhaPadrao);

            var resposta = await client.DeleteAsync($"/api/wl/usuarios/{alvoId}");

            resposta.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Fact]
        public async Task Listagem_de_usuarios_so_traz_a_propria_afiliada()
        {
            var admin = "iso-lista-a@exemplo.com";
            await Seed.OperadorAsync(AfiliadaA, admin, new[] { "UsuarioAfiliadaGerenciar" });
            await Seed.OperadorAsync(AfiliadaB, "iso-lista-b@exemplo.com");

            using var factory = new WlApiFactory(_db, AfiliadaA);
            using var client = await factory.ClienteAutenticadoAsync(admin, Seed.SenhaPadrao);

            var usuarios = await client.GetFromJsonAsync<UsuarioDto[]>("/api/wl/usuarios");

            usuarios.Should().NotBeNull();
            usuarios!.Should().OnlyContain(u => !u.Email.EndsWith("-b@exemplo.com"),
                "nenhum operador da afiliada B pode aparecer na listagem de A");
        }

        [Fact]
        public async Task Operador_de_A_nao_le_local_de_B()
        {
            var operador = "iso-local-a@exemplo.com";
            await Seed.OperadorAsync(AfiliadaA, operador, new[] { "PecaGerenciar" });

            var localDeB = await Seed.LocalAsync(AfiliadaB, "LOC-ISO-B");

            using var factory = new WlApiFactory(_db, AfiliadaA);
            using var client = await factory.ClienteAutenticadoAsync(operador, Seed.SenhaPadrao);

            var resposta = await client.GetAsync($"/api/wl/locais/{localDeB}");

            resposta.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Fact]
        public async Task Operador_de_A_nao_exclui_local_de_B()
        {
            var operador = "iso-localdel-a@exemplo.com";
            await Seed.OperadorAsync(AfiliadaA, operador, new[] { "PecaGerenciar" });

            var localDeB = await Seed.LocalAsync(AfiliadaB, "LOC-ISODEL-B");

            using var factory = new WlApiFactory(_db, AfiliadaA);
            using var client = await factory.ClienteAutenticadoAsync(operador, Seed.SenhaPadrao);

            var resposta = await client.DeleteAsync($"/api/wl/locais/{localDeB}");

            resposta.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        /// <summary>
        /// O caso que o comentario do LocaisController descreve como perigoso: o
        /// LocalCadastroHandler do core, no ramo de edicao, chama
        /// `local.SetAfiliada(afiliada)` — editar local de outra exibidora nao
        /// seria recusado, seria TRANSFERIDO. A verificacao de propriedade no BFF
        /// e o que impede o sequestro de inventario.
        /// </summary>
        [Fact]
        public async Task Operador_de_A_nao_transfere_local_de_B_editando()
        {
            var operador = "iso-transfer-a@exemplo.com";
            await Seed.OperadorAsync(AfiliadaA, operador, new[] { "PecaGerenciar" });

            var localDeB = await Seed.LocalAsync(AfiliadaB, "LOC-TRANSF-B");

            using var factory = new WlApiFactory(_db, AfiliadaA);
            using var client = await factory.ClienteAutenticadoAsync(operador, Seed.SenhaPadrao);

            var resposta = await client.PutAsJsonAsync($"/api/wl/locais/{localDeB}", new
            {
                IdCidade = 1,
                Descricao = "Sequestrado",
                Endereco = new { Logradouro = "Rua X", Numero = "1" },
                Geolocalizacao = new { Latitude = -23.5, Longitude = -46.6 },
            });

            resposta.StatusCode.Should().Be(HttpStatusCode.NotFound);

            factory.Core.Requisicoes.Should().BeEmpty(
                "a checagem de propriedade precisa barrar ANTES de encaminhar ao core");
        }

        [Fact]
        public async Task Operador_de_A_nao_le_peca_de_B()
        {
            var operador = "iso-peca-a@exemplo.com";
            await Seed.OperadorAsync(AfiliadaA, operador, new[] { "PecaGerenciar" });

            var localDeB = await Seed.LocalAsync(AfiliadaB, "LOC-PCISO-B");
            var pecaDeB = await Seed.PecaAsync(localDeB, "PEC-ISO-B");

            using var factory = new WlApiFactory(_db, AfiliadaA);
            using var client = await factory.ClienteAutenticadoAsync(operador, Seed.SenhaPadrao);

            var resposta = await client.GetAsync($"/api/wl/pecas/{pecaDeB}");

            resposta.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Fact]
        public async Task Listagem_de_locais_so_traz_a_propria_afiliada()
        {
            var operador = "iso-listalocal-a@exemplo.com";
            await Seed.OperadorAsync(AfiliadaA, operador, new[] { "PecaGerenciar" });

            await Seed.LocalAsync(AfiliadaA, "LOC-MEU-A");
            await Seed.LocalAsync(AfiliadaB, "LOC-ALHEIO-B");

            using var factory = new WlApiFactory(_db, AfiliadaA);
            using var client = await factory.ClienteAutenticadoAsync(operador, Seed.SenhaPadrao);

            var locais = await client.GetFromJsonAsync<LocalDto[]>("/api/wl/locais");

            locais.Should().NotBeNull();
            locais!.Should().Contain(l => l.Codigo == "LOC-MEU-A");
            locais.Should().NotContain(l => l.Codigo == "LOC-ALHEIO-B");
        }

        /// <summary>
        /// O dashboard agrega contagens; se algum COUNT esquecer o filtro, os
        /// numeros de outra exibidora vazam sem que nenhum id seja exposto.
        /// </summary>
        [Fact]
        public async Task Kpis_do_dashboard_contam_apenas_a_propria_afiliada()
        {
            const int afiliadaC = 8300;
            const int afiliadaD = 8400;

            var operador = "iso-kpi-c@exemplo.com";
            await Seed.OperadorAsync(afiliadaC, operador);

            await Seed.LocalAsync(afiliadaC, "LOC-KPI-C1");
            await Seed.LocalAsync(afiliadaD, "LOC-KPI-D1");
            await Seed.LocalAsync(afiliadaD, "LOC-KPI-D2");

            using var factory = new WlApiFactory(_db, afiliadaC);
            using var client = await factory.ClienteAutenticadoAsync(operador, Seed.SenhaPadrao);

            var kpis = await client.GetFromJsonAsync<KpisDto>("/api/wl/dashboard/kpis");

            kpis.Should().NotBeNull();
            kpis!.LocaisAtivos.Should().Be(1, "a afiliada C tem exatamente um local ativo");
        }

        private sealed record UsuarioDto(int Id, string Nome, string Email, string[] Permissoes);
        private sealed record LocalDto(int Id, string Codigo, string Descricao);
        private sealed record KpisDto(int LocaisAtivos, int PecasEmExibicao, int PedidosPendentes);
    }
}
