using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests
{
    [Collection(DatabaseCollection.Nome)]
    public class AppInventoryTests
    {
        private readonly SqlServerFixture _db;
        public AppInventoryTests(SqlServerFixture db) => _db = db;

        [Fact]
        public async Task Anunciante_ve_apenas_pecas_ativas_do_proprio_tenant_e_pode_filtrar()
        {
            const int afiliada = 821;
            var localDaAfiliada = await Seed.LocalAsync(afiliada, "LOC821A");
            await Seed.PecaAsync(localDaAfiliada, "P-APP-821-A");

            var localDeOutroTenant = await Seed.LocalAsync(822, "LOC822A");
            await Seed.PecaAsync(localDeOutroTenant, "P-APP-822-A");

            using var factory = new WlApiFactory(_db, afiliada);
            using var client = factory.ClienteAnonimo();
            var resposta = await client.GetAsync("/api/wl/app/inventory?query=821-A&mediaType=Outdoor&minPrice=1000&maxPrice=2000");
            resposta.StatusCode.Should().Be(HttpStatusCode.OK);
            var itens = await resposta.Content.ReadFromJsonAsync<InventoryItem[]>();
            itens.Should().ContainSingle();
            itens[0].Code.Should().Be("P-APP-821-A");
            itens[0].Available.Should().BeTrue();
            itens[0].Price.Should().Be(1500);

            var semCorrespondencia = await client.GetFromJsonAsync<InventoryItem[]>("/api/wl/app/inventory?query=nenhuma-peca-assim");
            semCorrespondencia.Should().BeEmpty("o termo digitado deve chegar ao filtro do servidor");

            (await client.GetAsync("/api/wl/app/inventory/P-APP-822-A")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await client.GetAsync("/api/wl/app/inventory/P-APP-821-A")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task Visitante_anonimo_pode_consultar_catalogo_do_app()
        {
            const int afiliada = 823;
            using var factory = new WlApiFactory(_db, afiliada);
            using var client = factory.ClienteAnonimo();
            (await client.GetAsync("/api/wl/app/inventory")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        private sealed record InventoryItem(string Code, decimal Price, bool Available);
    }
}
