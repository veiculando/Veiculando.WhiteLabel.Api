using System.Linq;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Veiculando.Data.Contexts;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests
{
    /// <summary>
    /// Lacunas de listagem apontadas no assurance da Sprint 10.0 (HF-5):
    /// D9 (Locais com suporte/formato/valor), D8 (PIs com cidade, período e
    /// filtro de período) e D14 (Check out com o período da PI).
    /// </summary>
    [Collection(DatabaseCollection.Nome)]
    public class ListagensLacunasHf5Tests
    {
        private const int Afiliada = 9310;
        private const int PeriodoOutro = 931001;
        private readonly SqlServerFixture _db;

        public ListagensLacunasHf5Tests(SqlServerFixture db) => _db = db;

        [Fact]
        public async Task Locais_trazem_suporte_formato_e_valor_da_primeira_peca_e_null_sem_peca()
        {
            var email = "hf5-locais@exemplo.com";
            await Seed.OperadorAsync(Afiliada, email, new[] { "PecaGerenciar" });

            var comPeca = await Seed.LocalAsync(Afiliada, "HF5LCOM");
            await Seed.PecaAsync(comPeca, "HF5PCOM");
            var semPeca = await Seed.LocalAsync(Afiliada, "HF5LSEM");

            using var factory = new WlApiFactory(_db, Afiliada);
            using var client = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);

            var locais = await client.GetFromJsonAsync<JsonElement[]>("/api/wl/locais");

            var linha = locais!.Single(l => l.GetProperty("id").GetInt32() == comPeca);
            linha.GetProperty("suporte").GetString().Should().Be("Outdoor");
            linha.GetProperty("formatoDimensao").GetString().Should().NotBeNullOrWhiteSpace();
            linha.GetProperty("valorPadrao").GetDecimal().Should().Be(1500m);
            linha.TryGetProperty("periodicidade", out _).Should().BeTrue();

            // Seed sem logradouro: a coluna cai na descrição no front, então
            // aqui tem de ser null, não ", ".
            linha.GetProperty("endereco").ValueKind.Should().Be(JsonValueKind.Null);

            var vazio = locais!.Single(l => l.GetProperty("id").GetInt32() == semPeca);
            vazio.GetProperty("suporte").ValueKind.Should().Be(JsonValueKind.Null);
            vazio.GetProperty("formatoDimensao").ValueKind.Should().Be(JsonValueKind.Null);
            vazio.GetProperty("valorPadrao").ValueKind.Should().Be(JsonValueKind.Null);
        }

        [Fact]
        public async Task PIs_trazem_cidade_e_periodo_e_filtram_por_periodoId()
        {
            var email = "hf5-pis@exemplo.com";
            await Seed.OperadorAsync(Afiliada, email, new[] { "PedidoInsercaoGerenciar" });

            var local = await Seed.LocalAsync(Afiliada, "HF5LPI");
            var peca = await Seed.PecaAsync(local, "HF5PPI");
            await Seed.InsercaoAsync(Afiliada, "HF5-PI-A", peca);
            var outraId = await Seed.InsercaoAsync(Afiliada, "HF5-PI-B", peca);

            // A segunda PI passa para outro período: é o que o filtro tem de separar.
            await Seed.PeriodoAsync(PeriodoOutro, "HF5P");
            string cidadeEsperada;
            using (var ctx = new VeiculandoDataContext())
            {
                await ctx.Database.ExecuteSqlCommandAsync($@"
UPDATE PedidoItem SET IdPeriodo = {PeriodoOutro}
WHERE Id IN (SELECT IdPedidoItem FROM PedidoInsercaoItem WHERE IdPedidoInsercao = {outraId});");
                cidadeEsperada = await ctx.Database
                    .SqlQuery<string>("SELECT Nome FROM Cidade WHERE Id = 1")
                    .SingleAsync();
            }

            using var factory = new WlApiFactory(_db, Afiliada);
            using var client = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);

            var todas = await client.GetFromJsonAsync<JsonElement>("/api/wl/pedidos-insercao?pageSize=100");
            var linhas = todas.GetProperty("itens").EnumerateArray().ToList();
            var linhaA = linhas.Single(l => l.GetProperty("codigo").GetString() == "HF5-PI-A");

            linhaA.GetProperty("cidade").GetString().Should().Be(cidadeEsperada);
            linhaA.GetProperty("qtdCidades").GetInt32().Should().Be(1);
            var periodo = linhaA.GetProperty("periodo");
            periodo.GetProperty("id").GetInt32().Should().Be(1);
            periodo.GetProperty("rotulo").GetString().Should().NotBeNullOrWhiteSpace();
            periodo.GetProperty("dataInicio").GetDateTime()
                .Should().BeBefore(periodo.GetProperty("dataFim").GetDateTime());
            periodo.GetProperty("quantidade").GetInt32().Should().Be(1);

            var filtrada = await client.GetFromJsonAsync<JsonElement>(
                $"/api/wl/pedidos-insercao?pageSize=100&periodoId={PeriodoOutro}");
            var codigos = filtrada.GetProperty("itens").EnumerateArray()
                .Select(l => l.GetProperty("codigo").GetString())
                .ToList();

            codigos.Should().Contain("HF5-PI-B").And.NotContain("HF5-PI-A");
            filtrada.GetProperty("resumo").GetProperty("totalPIs").GetInt32()
                .Should().Be(codigos.Count, "o resumo sai do mesmo conjunto filtrado");
        }

        [Fact]
        public async Task Checking_traz_o_periodo_de_veiculacao_da_PI()
        {
            var email = "hf5-chk@exemplo.com";
            await Seed.OperadorAsync(Afiliada, email, new[] { "CheckingGerenciar", "PedidoInsercaoGerenciar" });

            var local = await Seed.LocalAsync(Afiliada, "HF5LCK");
            var peca = await Seed.PecaAsync(local, "HF5PCK");
            var codigo = "HF5-PI-CK";
            await Seed.InsercaoAsync(Afiliada, codigo, peca);
            await Seed.ServicoCoreAsync(Afiliada);

            using var factory = new WlApiFactory(_db, Afiliada);
            using var client = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);

            // O checking nasce com a primeira foto, como em CheckingListagemTests.
            var itensResp = await client.GetFromJsonAsync<JsonElement>($"/api/wl/checking/pi/{codigo}/itens");
            var idItem = itensResp[0].GetProperty("idPedidoItem").GetInt32();
            using var conteudo = new System.Net.Http.MultipartFormDataContent();
            var arquivo = new System.Net.Http.ByteArrayContent(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46 });
            arquivo.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            conteudo.Add(arquivo, "foto", "comprovacao.jpg");
            (await client.PostAsync($"/api/wl/checking/enviar-foto/{idItem}", conteudo)).EnsureSuccessStatusCode();

            var pagina = await client.GetFromJsonAsync<PaginaDto<JsonElement>>("/api/wl/checking?pageSize=100");
            var linha = pagina!.Itens.Single(c => c.GetProperty("piCodigo").GetString() == codigo);

            var periodo = linha.GetProperty("periodo");
            periodo.ValueKind.Should().Be(JsonValueKind.Object);
            periodo.GetProperty("dataInicio").GetDateTime()
                .Should().BeBefore(periodo.GetProperty("dataFim").GetDateTime());
        }
    }
}
