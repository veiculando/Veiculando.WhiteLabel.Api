using System.Collections.Generic;
using System.Linq;
using System.Net;
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
    /// Ordem de Serviço — VEI-RD-88. Cobre o caminho feliz (gerar, listar, ver
    /// detalhe, histórico e PDF) e as duas barreiras de servidor que o card
    /// exige: peça de outra afiliada é recusada, e OS de outra afiliada é 404.
    /// </summary>
    [Collection(DatabaseCollection.Nome)]
    public class OrdensServicoTests
    {
        private const int Afiliada = 8950;
        private readonly SqlServerFixture _db;

        public OrdensServicoTests(SqlServerFixture db) => _db = db;

        private async Task<(WlApiFactory Factory, HttpClient Client, int PecaId)> PrepararAsync(
            string sufixo, int afiliadaId = Afiliada)
        {
            var email = $"os-{sufixo}-{afiliadaId}@exemplo.com";
            await Seed.OperadorAsync(afiliadaId, email, new[] { "PecaGerenciar" });

            // LocalAsync cria a Afiliada (FK de UsuarioAfiliada) antes de
            // ServicoCoreAsync tentar vincular a conta de serviço a ela.
            var localId = await Seed.LocalAsync(afiliadaId, $"L{sufixo}");
            var pecaId = await Seed.PecaAsync(localId, $"P{sufixo}");
            // Garante o Periodo Id=1 (mesmo grafo de apoio compartilhado do Seed).
            await Seed.ReservaAsync(afiliadaId, $"R{sufixo}", pecaId);
            await Seed.ServicoCoreAsync(afiliadaId);

            var factory = new WlApiFactory(_db, afiliadaId);
            var client = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);

            return (factory, client, pecaId);
        }

        [Fact]
        public async Task Gera_lista_detalha_e_traz_historico_da_os()
        {
            var (factory, client, pecaId) = await PrepararAsync("ger");
            using var _ = factory;
            using var __ = client;

            var criar = await client.PostAsJsonAsync("/api/wl/ordens-servico",
                new { IdPeriodo = 1, IdPecas = new[] { pecaId } });

            criar.StatusCode.Should().Be(HttpStatusCode.Created,
                await criar.Content.ReadAsStringAsync());

            var criada = await criar.Content.ReadFromJsonAsync<JsonElement>();
            var id = criada.GetProperty("id").GetInt32();
            criada.GetProperty("numeroFormatado").GetString().Should().StartWith("OS #");

            var listagem = await client.GetFromJsonAsync<PaginaDto<OsListDto>>("/api/wl/ordens-servico");
            listagem!.Itens.Should().Contain(o => o.Id == id && o.PecasCount == 1);

            var detalheResp = await client.GetAsync($"/api/wl/ordens-servico/{id}");
            detalheResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var detalhe = await detalheResp.Content.ReadFromJsonAsync<JsonElement>();
            detalhe.GetProperty("status").GetString().Should().Be("Aberta");
            detalhe.GetProperty("responsavel").ValueKind.Should().Be(JsonValueKind.Null);

            var pecas = detalhe.GetProperty("pecas").EnumerateArray().ToList();
            pecas.Should().ContainSingle();
            pecas[0].GetProperty("statusColagem").GetString().Should().Be("Pendente");
            pecas[0].GetProperty("dataColagem").ValueKind.Should().Be(JsonValueKind.Null);

            var historicoInline = detalhe.GetProperty("historico").EnumerateArray().ToList();
            historicoInline.Should().NotBeEmpty("o construtor de OrdemServico já registra o evento de criação");

            var historicoResp = await client.GetAsync($"/api/wl/ordens-servico/{id}/historico");
            historicoResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var historico = await historicoResp.Content.ReadFromJsonAsync<JsonElement>();
            historico.EnumerateArray().Should().NotBeEmpty();

            var pdfResp = await client.GetAsync($"/api/wl/ordens-servico/{id}/pdf");
            pdfResp.StatusCode.Should().Be(HttpStatusCode.OK);
            pdfResp.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
            var bytes = await pdfResp.Content.ReadAsByteArrayAsync();
            bytes.Should().StartWith(new byte[] { 0x25, 0x50, 0x44, 0x46 }); // %PDF
        }

        [Fact]
        public async Task Recusa_peca_de_outra_afiliada_com_400()
        {
            const int outra = 8951;

            var localDeB = await Seed.LocalAsync(outra, "LOSB");
            var pecaDeB = await Seed.PecaAsync(localDeB, "POSB");
            await Seed.ReservaAsync(outra, "ROSB", pecaDeB);

            var (factory, client, _) = await PrepararAsync("iso");
            using var _f = factory;
            using var _c = client;

            var resposta = await client.PostAsJsonAsync("/api/wl/ordens-servico",
                new { IdPeriodo = 1, IdPecas = new[] { pecaDeB } });

            resposta.StatusCode.Should().Be(HttpStatusCode.BadRequest,
                "a peça pertence a outra afiliada e _tenant.Pecas não a devolve");
        }

        [Fact]
        public async Task Os_de_outra_afiliada_devolve_404()
        {
            var (factoryB, clientB, pecaB) = await PrepararAsync("alheia", 8952);
            using var _b = factoryB;
            using var __b = clientB;

            var criar = await clientB.PostAsJsonAsync("/api/wl/ordens-servico",
                new { IdPeriodo = 1, IdPecas = new[] { pecaB } });
            var criada = await criar.Content.ReadFromJsonAsync<JsonElement>();
            var idAlheio = criada.GetProperty("id").GetInt32();

            var (factoryA, clientA, _) = await PrepararAsync("propria");
            using var _a = factoryA;
            using var __a = clientA;

            (await clientA.GetAsync($"/api/wl/ordens-servico/{idAlheio}"))
                .StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await clientA.GetAsync($"/api/wl/ordens-servico/{idAlheio}/pdf"))
                .StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await clientA.GetAsync($"/api/wl/ordens-servico/{idAlheio}/historico"))
                .StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        private sealed record OsListDto(int Id, string NumeroFormatado, int PecasCount, string Status);
    }
}
