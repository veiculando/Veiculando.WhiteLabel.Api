using System.Linq;
using System.Net.Http.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests
{
    /// <summary>
    /// Campanhas (Sprint 10.5 BE-4): peças alocadas com valor no detalhe, e o
    /// recorte por exibidora dentro de um Pedido compartilhado.
    /// </summary>
    /// <remarks>
    /// Um Pedido é por cidade/período e junta peças de várias exibidoras — cada
    /// uma com o seu PedidoReserva. O cenário aqui é exatamente esse: um pedido
    /// com uma peça de A e uma de B, visto por A.
    /// </remarks>
    [Collection(DatabaseCollection.Nome)]
    public class CampanhasDetalheTests
    {
        private readonly SqlServerFixture _db;

        public CampanhasDetalheTests(SqlServerFixture db) => _db = db;

        [Fact]
        public async Task Detalhe_e_listagem_mostram_so_as_pecas_e_valores_desta_exibidora()
        {
            const int a = 9530, b = 9531;
            var pecaA = await Seed.PecaAsync(await Seed.LocalAsync(a, "L9530"), "P9530");
            var pecaB = await Seed.PecaAsync(await Seed.LocalAsync(b, "L9531"), "P9531");
            await Seed.ReservaAsync(a, "R9530", pecaA, pecaIdExtra: pecaB);

            const string email = "camp-det@exemplo.com";
            await Seed.OperadorAsync(a, email, new[] { "ClienteGerenciar" });
            using var factory = new WlApiFactory(_db, a);
            using var client = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);

            // O seed usa a campanha de Id 1 para todas as reservas.
            var detalhe = JObject.Parse(await client.GetStringAsync("/api/wl/campanhas/1"));
            var pedidos = detalhe["pedidos"]!.ToList();
            var itens = pedidos.SelectMany(p => p["itens"]!).ToList();

            itens.Select(i => (string)i["peca"]!["codigo"]!).Should().Equal("P9530");
            ((decimal)itens.Single()["valorBruto"]!).Should().Be(1000m);
            pedidos.Sum(p => (int)p["pecas"]!).Should().Be(1, "a peça de B não entra na contagem de A");
            pedidos.Sum(p => (decimal)p["valor"]!).Should().Be(1000m, "nem no valor");

            var pagina = await client.GetFromJsonAsync<PaginaDto<CampanhaDto>>("/api/wl/campanhas?pageSize=100");
            var linha = pagina!.Itens.Single(c => c.Id == 1);
            linha.Pecas.Should().Be(1);
            linha.ValorTotal.Should().Be(1000m);
        }

        private sealed record CampanhaDto(int Id, int Pecas, decimal ValorTotal);
    }
}
