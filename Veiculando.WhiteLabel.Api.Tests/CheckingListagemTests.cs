using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests
{
    /// <summary>
    /// Listagem de Checking (VEI-RD-91) — as 4 colunas numéricas pedidas pelo
    /// Figma: Itens PI / Check. / Aprov. / Receb.
    /// </summary>
    [Collection(DatabaseCollection.Nome)]
    public class CheckingListagemTests
    {
        private const int Afiliada = 8980;
        private readonly SqlServerFixture _db;

        public CheckingListagemTests(SqlServerFixture db) => _db = db;

        [Fact]
        public async Task Listagem_traz_itens_pi_checados_aprovados_e_recebidos()
        {
            var email = "chk-list@exemplo.com";
            await Seed.OperadorAsync(Afiliada, email, new[] { "CheckingGerenciar", "PedidoInsercaoGerenciar" });

            var localId = await Seed.LocalAsync(Afiliada, "LCKL");
            var pecaId = await Seed.PecaAsync(localId, "PCKL");
            var codigo = "PICKL";
            await Seed.InsercaoAsync(Afiliada, codigo, pecaId);
            // Depois de InsercaoAsync: ele cria o grafo de apoio (PerfilUsuario,
            // Usuario base, Afiliada) do qual ServicoCoreAsync depende via FK.
            await Seed.ServicoCoreAsync(Afiliada);

            using var factory = new WlApiFactory(_db, Afiliada);
            using var client = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);

            var itensResp = await client.GetFromJsonAsync<JsonElement>($"/api/wl/checking/pi/{codigo}/itens");
            var idItem = itensResp[0].GetProperty("idPedidoItem").GetInt32();

            using var conteudo = new MultipartFormDataContent();
            var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46 };
            var arquivo = new ByteArrayContent(jpeg);
            arquivo.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            conteudo.Add(arquivo, "foto", "comprovacao.jpg");
            (await client.PostAsync($"/api/wl/checking/enviar-foto/{idItem}", conteudo))
                .EnsureSuccessStatusCode();

            var pagina = await client.GetFromJsonAsync<PaginaDto<JsonElement>>("/api/wl/checking");
            pagina.Should().NotBeNull();
            pagina!.Itens.Should().ContainSingle(c => c.GetProperty("piCodigo").GetString() == codigo);

            var linha = pagina.Itens.First(c => c.GetProperty("piCodigo").GetString() == codigo);
            linha.GetProperty("itensPi").GetInt32().Should().Be(1, "a PI tem 1 item de peça");
            linha.GetProperty("itensChecados").GetInt32().Should().Be(1, "o item já tem CheckingItem (uma foto enviada)");
            // Sem avaliação, e sem geolocalização real (o pipeline de upload sempre
            // grava (0,0) — "não inventar EXIF/geolocalização"), o item cai em
            // ErroGeolocalizacao, não em Recebido/Aprovado.
            linha.GetProperty("itensAprovados").GetInt32().Should().Be(0);
            linha.GetProperty("itensRecebidos").GetInt32().Should().Be(0);
        }
    }
}
