using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests
{
    /// <summary>
    /// PDF de PI entregue pelo BFF — card `c2a44cbc`, cenarios 2 a 5.
    /// </summary>
    /// <remarks>
    /// O desenho anterior nao tinha endpoint: a listagem devolvia
    /// <c>PdfUrl = "{FILE_SERVER_URL}/pedidoinsercao/detalhes/{Id}"</c> e a tela
    /// abria esse link direto. Duas coisas erradas ao mesmo tempo — a rota nao
    /// existe no FileServer (a real e <c>pedido-insercao/pi-exibidora/{codigo}</c>,
    /// por codigo), entao o botao dava 404; e o host do FileServer chegava ao
    /// browser, o que abriria um IDOR assim que alguem "consertasse" a URL, ja
    /// que aquele servico nao tem [Authorize] nem filtro por afiliada.
    /// </remarks>
    [Collection(DatabaseCollection.Nome)]
    public class PedidosInsercaoPdfTests
    {
        private const int AfiliadaA = 8760;
        private const int AfiliadaB = 8761;

        private readonly SqlServerFixture _db;

        public PedidosInsercaoPdfTests(SqlServerFixture db) => _db = db;

        private async Task<(WlApiFactory Factory, HttpClient Client, string Codigo)> PrepararAsync(
            string sufixo, int afiliadaId = AfiliadaA)
        {
            var email = $"pdf-{sufixo}-{afiliadaId}@exemplo.com";
            await Seed.OperadorAsync(afiliadaId, email, new[] { "PedidoInsercaoGerenciar" });

            var localId = await Seed.LocalAsync(afiliadaId, $"L{sufixo}{afiliadaId}");
            var pecaId = await Seed.PecaAsync(localId, $"P{sufixo}{afiliadaId}");

            // Curto de proposito: o Seed monta `PED-RES{codigo}` em Pedido.Codigo,
            // que e nvarchar(20). Um codigo longo aqui falha como truncamento no
            // seed, nao como defeito do endpoint.
            var codigo = $"PX{sufixo}{afiliadaId % 100}";
            await Seed.InsercaoAsync(afiliadaId, codigo, pecaId);

            var factory = new WlApiFactory(_db, afiliadaId);
            var client = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);

            return (factory, client, codigo);
        }

        /// <summary>Cenario 2 — o BFF valida o tenant e consulta a rota real do FileServer.</summary>
        [Fact]
        public async Task Pdf_proprio_e_entregue_pelo_bff_consultando_a_rota_pi_exibidora()
        {
            var (factory, client, codigo) = await PrepararAsync("ok");
            using var _ = factory;
            using var __ = client;

            var resposta = await client.GetAsync($"/api/wl/pedidos-insercao/{codigo}/pdf");

            resposta.StatusCode.Should().Be(HttpStatusCode.OK);
            resposta.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");

            var bytes = await resposta.Content.ReadAsByteArrayAsync();
            bytes.Should().StartWith(new byte[] { 0x25, 0x50, 0x44, 0x46 }); // %PDF

            factory.FileServer.Rotas.Should().ContainSingle()
                .Which.Should().Contain($"pedido-insercao/pi-exibidora/{codigo}");
        }

        /// <summary>
        /// Cenario 3 — o 404 acontece ANTES da chamada remota.
        /// </summary>
        /// <remarks>
        /// A assercao que importa e <c>Rotas.Should().BeEmpty()</c>. Um 404
        /// devolvido depois de ja ter buscado o arquivo protegeria a resposta e
        /// nao o dado: o FileServer entrega a quem pedir, entao a unica barreira
        /// real e nao chegar la.
        /// </remarks>
        [Fact]
        public async Task Pdf_de_outro_tenant_da_404_e_nao_chama_o_fileserver()
        {
            var (factoryB, clientB, codigoB) = await PrepararAsync("alheio", AfiliadaB);
            using var _ = factoryB;
            using var __ = clientB;

            var (factoryA, clientA, _) = await PrepararAsync("proprio", AfiliadaA);
            using var ___ = factoryA;
            using var ____ = clientA;

            var resposta = await clientA.GetAsync($"/api/wl/pedidos-insercao/{codigoB}/pdf");

            resposta.StatusCode.Should().Be(HttpStatusCode.NotFound);
            factoryA.FileServer.Rotas.Should().BeEmpty();
        }

        /// <summary>Cenario 5 — falha da origem vira 502 sem vazar host nem credencial.</summary>
        [Fact]
        public async Task Falha_do_fileserver_vira_502_sem_expor_o_servico_interno()
        {
            var (factory, client, codigo) = await PrepararAsync("falha");
            using var _ = factory;
            using var __ = client;

            factory.FileServer.Responder = _ => throw new HttpRequestException(
                "Connection refused (fileserver.interno.veiculando:5001)");

            var resposta = await client.GetAsync($"/api/wl/pedidos-insercao/{codigo}/pdf");

            resposta.StatusCode.Should().Be(HttpStatusCode.BadGateway);

            var corpo = await resposta.Content.ReadAsStringAsync();
            corpo.Should().NotContainEquivalentOf("fileserver");
            corpo.Should().NotContainEquivalentOf("5001");
        }

        /// <summary>Origem sem o documento e 404, nao 502 — sao causas diferentes.</summary>
        [Fact]
        public async Task Pdf_ausente_na_origem_devolve_404()
        {
            var (factory, client, codigo) = await PrepararAsync("ausente");
            using var _ = factory;
            using var __ = client;

            factory.FileServer.Responder = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

            var resposta = await client.GetAsync($"/api/wl/pedidos-insercao/{codigo}/pdf");

            resposta.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        /// <summary>
        /// Cenario 4 — nem a listagem nem o detalhe entregam URL do FileServer.
        /// </summary>
        [Fact]
        public async Task Listagem_e_detalhe_nao_expoem_url_do_fileserver()
        {
            var (factory, client, codigo) = await PrepararAsync("vaza");
            using var _ = factory;
            using var __ = client;

            var listagem = await (await client.GetAsync("/api/wl/pedidos-insercao"))
                .Content.ReadAsStringAsync();
            var detalhe = await (await client.GetAsync($"/api/wl/pedidos-insercao/{codigo}"))
                .Content.ReadAsStringAsync();

            foreach (var corpo in new[] { listagem, detalhe })
            {
                corpo.Should().NotContainEquivalentOf("pdfUrl");
                corpo.Should().NotContainEquivalentOf("fileserver");
                corpo.Should().NotContainEquivalentOf("pedidoinsercao/detalhes");
            }
        }

        /// <summary>A rota herda a policy do controller, como as demais do modulo.</summary>
        [Fact]
        public async Task Pdf_exige_a_permissao_de_pedido_de_insercao()
        {
            var afiliadaId = AfiliadaA;
            var email = $"pdf-sem-permissao-{afiliadaId}@exemplo.com";
            await Seed.OperadorAsync(afiliadaId, email, new[] { "Checking" });

            var (factory, dono, codigo) = await PrepararAsync("perm");
            using var _ = factory;
            using var __ = dono;

            using var client = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);

            var resposta = await client.GetAsync($"/api/wl/pedidos-insercao/{codigo}/pdf");

            resposta.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            factory.FileServer.Rotas.Should().BeEmpty();
        }
    }
}
