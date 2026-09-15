using System.Net;
using System.Net.Http.Headers;
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
            const string email = "inventario-app-11711@exemplo.com";
            await Seed.AnuncianteAsync(afiliada, email);
            var localDaAfiliada = await Seed.LocalAsync(afiliada, "LOC821A");
            await Seed.PecaAsync(localDaAfiliada, "P-APP-821-A");

            var localDeOutroTenant = await Seed.LocalAsync(822, "LOC822A");
            await Seed.PecaAsync(localDeOutroTenant, "P-APP-822-A");

            using var factory = new WlApiFactory(_db, afiliada);
            using var client = factory.ClienteAnonimo();
            var login = await client.PostAsJsonAsync("/api/wl/app/auth/login", new { email, password = Seed.SenhaPadrao });
            login.StatusCode.Should().Be(HttpStatusCode.OK);
            var session = await login.Content.ReadFromJsonAsync<AppSession>();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);

            var resposta = await client.GetAsync("/api/wl/app/inventory?query=821-A&mediaType=Outdoor&minPrice=1000&maxPrice=2000");
            resposta.StatusCode.Should().Be(HttpStatusCode.OK);
            var itens = await resposta.Content.ReadFromJsonAsync<InventoryItem[]>();
            itens.Should().ContainSingle();
            itens[0].Code.Should().Be("P-APP-821-A");
            itens[0].Available.Should().BeTrue();
            itens[0].Price.Should().Be(1500);

            (await client.GetAsync("/api/wl/app/inventory/P-APP-822-A")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await client.GetAsync("/api/wl/app/inventory/P-APP-821-A")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task Operador_nao_pode_consultar_catalogo_do_app()
        {
            const int afiliada = 823;
            const string operador = "operador-inventario-11712@exemplo.com";
            await Seed.OperadorAsync(afiliada, operador);

            using var factory = new WlApiFactory(_db, afiliada);
            using var client = await factory.ClienteAutenticadoAsync(operador, Seed.SenhaPadrao);
            (await client.GetAsync("/api/wl/app/inventory")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        private sealed record AppSession(string Token);
        private sealed record InventoryItem(string Code, decimal Price, bool Available);
    }
}
