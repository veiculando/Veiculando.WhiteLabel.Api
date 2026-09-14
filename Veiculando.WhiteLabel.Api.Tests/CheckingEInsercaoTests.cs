using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests
{
    /// <summary>
    /// Checking de veiculacao e pedidos de insercao.
    /// </summary>
    [Collection(DatabaseCollection.Nome)]
    public class CheckingEInsercaoTests
    {
        private const int Afiliada = 8700;
        private readonly SqlServerFixture _db;

        public CheckingEInsercaoTests(SqlServerFixture db) => _db = db;

        private async Task<(WlApiFactory, HttpClient, string)> PrepararAsync(string sufixo, int afiliadaId = Afiliada)
        {
            var email = $"pi-{sufixo}@exemplo.com";
            await Seed.OperadorAsync(afiliadaId, email, new[] { "Checking", "PedidoInsercaoGerenciar" });

            var localId = await Seed.LocalAsync(afiliadaId, $"L{sufixo}");
            var pecaId = await Seed.PecaAsync(localId, $"P{sufixo}");
            var codigo = $"PI{sufixo}";
            await Seed.InsercaoAsync(afiliadaId, codigo, pecaId);

            var factory = new WlApiFactory(_db, afiliadaId);
            var client = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);

            return (factory, client, codigo);
        }

        [Fact]
        public async Task Lista_pis_autorizadas_da_afiliada()
        {
            var (factory, client, codigo) = await PrepararAsync("aut");
            using var _ = factory;
            using var __ = client;

            var pis = await client.GetFromJsonAsync<PiDto[]>("/api/wl/checking/pis-autorizadas");

            pis.Should().NotBeNull();
            pis!.Should().Contain(p => p.Codigo == codigo);
        }

        /// <summary>
        /// Endpoint adicionado no TP-R4. Sem ele a segunda tela do checking era
        /// inalcancavel: o GetPiByCodigo devolve apenas ItensCount, e o upload e
        /// enderecado por idItemPI — o frontend nao tinha como descobrir esses ids.
        /// </summary>
        [Fact]
        public async Task Itens_da_pi_trazem_os_ids_necessarios_para_o_upload()
        {
            var (factory, client, codigo) = await PrepararAsync("itn");
            using var _ = factory;
            using var __ = client;

            var itens = await client.GetFromJsonAsync<ItemPiDto[]>($"/api/wl/checking/pi/{codigo}/itens");

            itens.Should().NotBeNull().And.NotBeEmpty();
            itens![0].IdPedidoItem.Should().BeGreaterThan(0);
            itens[0].LocalCodigo.Should().Be("Litn");
        }

        /// <summary>
        /// O endpoint valida o arquivo mas NAO persiste — o armazenamento e escopo
        /// do TP-2. Respondia 200 "recebida e validada com sucesso" e descartava o
        /// arquivo, o que num fluxo de checking e pior que falhar: a foto e a
        /// evidencia de que a insercao aconteceu.
        /// </summary>
        [Fact]
        public async Task Envio_de_foto_persiste_e_permite_listar_e_baixar_apos_nova_requisicao()
        {
            var (factory, client, codigo) = await PrepararAsync("fot");
            await Seed.ServicoCoreAsync(Afiliada);
            using var _ = factory;
            using var __ = client;

            var itens = await client.GetFromJsonAsync<ItemPiDto[]>($"/api/wl/checking/pi/{codigo}/itens");
            var idItem = itens![0].IdPedidoItem;

            using var conteudo = new MultipartFormDataContent();
            var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46 };
            var arquivo = new ByteArrayContent(jpeg);
            arquivo.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            conteudo.Add(arquivo, "foto", "comprovacao.jpg");

            var resposta = await client.PostAsync($"/api/wl/checking/enviar-foto/{idItem}", conteudo);

            resposta.StatusCode.Should().Be(HttpStatusCode.OK);
            factory.Uploads.Files.Should().ContainSingle();
            var fotos = await client.GetFromJsonAsync<System.Text.Json.JsonElement[]>($"/api/wl/checking/item/{idItem}/fotos");
            fotos.Should().ContainSingle();
            var url = fotos[0].GetProperty("downloadUrl").GetString();
            (await client.GetByteArrayAsync(url)).Should().Equal(jpeg);
            // Outra requisição percorre os registros EF6 gravados, não o objeto ainda rastreado.
            var recarregado = await client.GetFromJsonAsync<System.Text.Json.JsonElement[]>($"/api/wl/checking/item/{idItem}/fotos");
            recarregado.Should().HaveCount(1);
            using var segundo = new MultipartFormDataContent();
            var novaFoto = new ByteArrayContent(jpeg);
            novaFoto.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            segundo.Add(novaFoto, "foto", "segunda.jpg");
            (await client.PostAsync($"/api/wl/checking/enviar-foto/{idItem}", segundo)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await client.GetFromJsonAsync<System.Text.Json.JsonElement[]>($"/api/wl/checking/item/{idItem}/fotos")).Should().HaveCount(2);
            using var anonimo = factory.ClienteAnonimo();
            (await anonimo.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        /// <summary>
        /// A validacao de arquivo roda ANTES do 501, entao arquivo invalido
        /// continua sendo recusado com mensagem especifica.
        /// </summary>
        [Fact]
        public async Task Arquivo_com_tipo_invalido_e_recusado_com_400()
        {
            var (factory, client, codigo) = await PrepararAsync("inv");
            using var _ = factory;
            using var __ = client;

            var itens = await client.GetFromJsonAsync<ItemPiDto[]>($"/api/wl/checking/pi/{codigo}/itens");
            var idItem = itens![0].IdPedidoItem;

            using var conteudo = new MultipartFormDataContent();
            // Nao e JPG/PNG/PDF: os magic bytes nao batem com nenhum permitido.
            var texto = new ByteArrayContent(Encoding.UTF8.GetBytes("isto nao e uma imagem"));
            conteudo.Add(texto, "foto", "malicioso.jpg");

            var resposta = await client.PostAsync($"/api/wl/checking/enviar-foto/{idItem}", conteudo);

            resposta.StatusCode.Should().Be(HttpStatusCode.BadRequest,
                "a extensao .jpg nao basta — o FileValidationService confere magic bytes");
        }

        [Fact]
        public async Task Pi_de_outra_afiliada_devolve_404()
        {
            const int outra = 8800;

            var email = "pi-iso@exemplo.com";
            await Seed.OperadorAsync(Afiliada, email, new[] { "Checking" });

            var localDeB = await Seed.LocalAsync(outra, "Liso");
            var pecaDeB = await Seed.PecaAsync(localDeB, "Piso");
            await Seed.InsercaoAsync(outra, "PIiso", pecaDeB);

            using var factory = new WlApiFactory(_db, Afiliada);
            using var client = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);

            var resposta = await client.GetAsync("/api/wl/checking/pi/PIiso");

            resposta.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        /// <summary>
        /// GetByCodigo acessa pi.Pedido.Campanha.Agencia — sem Include, `pi.Pedido`
        /// vinha null e a projecao estourava NRE. O `?.` protegia o ultimo nivel,
        /// nao o primeiro.
        /// </summary>
        [Fact]
        public async Task Detalhe_de_pi_carrega_pedido_e_campanha()
        {
            var (factory, client, codigo) = await PrepararAsync("det");
            using var _ = factory;
            using var __ = client;

            var resposta = await client.GetAsync($"/api/wl/pedidos-insercao/{codigo}");

            resposta.StatusCode.Should().Be(HttpStatusCode.OK,
                "sem os Includes a projecao estourava NullReferenceException");

            var pi = await resposta.Content.ReadFromJsonAsync<PiDetalheDto>();
            pi!.Codigo.Should().Be(codigo);
            pi.Anunciante.Should().Be("Cliente Teste", "vem de Pedido.Campanha.Cliente");
            pi.Agencia.Should().Be("Agencia Teste", "vem de Pedido.Campanha.Agencia");

            // A assercao sobre PdfUrl saiu daqui de proposito. Ela fixava o
            // defeito: o campo publicava o host do FileServer no payload, e a
            // rota que ele montava (`/pedidoinsercao/detalhes/{Id}`) nem existe
            // naquele servico. O PDF agora sai por `{codigo}/pdf` neste mesmo
            // controller — ver PedidosInsercaoPdfTests.
        }

        [Fact]
        public async Task Listagem_de_pis_traz_anunciante_e_agencia()
        {
            var (factory, client, codigo) = await PrepararAsync("lst");
            using var _ = factory;
            using var __ = client;

            var pagina = await client.GetFromJsonAsync<PaginaDto<PiDetalheDto>>("/api/wl/pedidos-insercao");

            pagina.Should().NotBeNull();
            pagina!.Itens.Should().ContainSingle(p => p.Codigo == codigo)
                .Which.Anunciante.Should().Be("Cliente Teste");
        }

        private sealed record PiDto(int Id, string Codigo);
        private sealed record ItemPiDto(int IdPedidoItem, int IdPedidoInsercao, string Status, string PecaCodigo, string LocalCodigo);
        private sealed record PiDetalheDto(int Id, string Codigo, string Status, string Agencia, string Anunciante);
    }
}
