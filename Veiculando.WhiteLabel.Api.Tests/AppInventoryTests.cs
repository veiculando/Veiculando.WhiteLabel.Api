using System;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Veiculando.Data.Contexts;
using Veiculando.WhiteLabel.Api.Services;
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

            var filtros = await client.GetFromJsonAsync<InventoryFilters>("/api/wl/app/inventory/filters");
            filtros.MediaTypes.Should().Contain("Outdoor", "as opções vêm do catálogo completo e não da busca atual");

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

        [Fact]
        public async Task Periodo_real_filtra_periodicidade_sem_exigir_login()
        {
            const int afiliada = 826;
            var localId = await Seed.LocalAsync(afiliada, "LOC826A");
            var pecaId = await Seed.PecaAsync(localId, "P-APP-826-A");
            using (var ctx = new VeiculandoDataContext())
            {
                await ctx.Database.ExecuteSqlCommandAsync("UPDATE Peca SET Periodicidade = 2 WHERE Id = @p0", pecaId);
                await ctx.Database.ExecuteSqlCommandAsync(@"
IF NOT EXISTS (SELECT 1 FROM Periodo WHERE Id = 82601)
BEGIN
    SET IDENTITY_INSERT Periodo ON;
    INSERT INTO Periodo (Id, Codigo, Periodicidade, DataInicio, DataFim, StatusExibicao)
    VALUES (82601, 'P-826-BI', 2, GETDATE(), DATEADD(day, 14, GETDATE()), 1);
    SET IDENTITY_INSERT Periodo OFF;
END");
            }

            using var factory = new WlApiFactory(_db, afiliada);
            using var client = factory.ClienteAnonimo();
            var filtros = await client.GetFromJsonAsync<InventoryFilters>("/api/wl/app/inventory/filters");
            filtros.Periods.Should().ContainSingle(p => p.Code == "P-826-BI");
            var itens = await client.GetFromJsonAsync<InventoryItem[]>("/api/wl/app/inventory?periodCode=P-826-BI");
            itens.Should().ContainSingle(p => p.Code == "P-APP-826-A");
            (await client.GetAsync("/api/wl/app/inventory?periodCode=INVALIDO")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Fact]
        public async Task Foto_publicada_e_entregue_pelo_BFF_sem_expor_storage_privado()
        {
            const int afiliada = 824;
            var localId = await Seed.LocalAsync(afiliada, "LOC824A");
            var pecaId = await Seed.PecaAsync(localId, "P-APP-824-A");
            var name = $"wl-{new string('a', 32)}.jpg";
            using (var ctx = new VeiculandoDataContext())
                await ctx.Database.ExecuteSqlCommandAsync("UPDATE Peca SET Foto = @p0 WHERE Id = @p1", name, pecaId);

            using var factory = new WlApiFactory(_db, afiliada);
            var key = $"tenant-{afiliada}/pecas/{pecaId}/{name}";
            var bytes = new byte[] { 0xff, 0xd8, 0xff, 0xd9 };
            factory.Uploads.Files[key] = (new WlStoredFile(key, name, "image/jpeg", bytes.Length, "test", DateTimeOffset.UtcNow), bytes, false);
            using var client = factory.ClienteAnonimo();
            var item = await client.GetFromJsonAsync<InventoryItem>("/api/wl/app/inventory/P-APP-824-A");
            item.ImageUrl.Should().Be("/api/wl/app/inventory/P-APP-824-A/photo");
            item.TablePrice.Should().BeGreaterThan(item.Price);
            item.Road.Should().NotBeNull();

            var photo = await client.GetAsync(item.ImageUrl);
            photo.StatusCode.Should().Be(HttpStatusCode.OK);
            photo.Content.Headers.ContentType.MediaType.Should().Be("image/jpeg");
            (await photo.Content.ReadAsByteArrayAsync()).Should().Equal(bytes);

            using var otherFactory = new WlApiFactory(_db, 825);
            using var otherClient = otherFactory.ClienteAnonimo();
            (await otherClient.GetAsync("/api/wl/app/inventory/P-APP-824-A/photo")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        private sealed record InventoryItem(string Code, decimal Price, bool Available, string ImageUrl, decimal TablePrice, object Road);
        private sealed record InventoryFilters(string[] MediaTypes, InventoryPeriod[] Periods);
        private sealed record InventoryPeriod(string Code);
    }
}
