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
    public class ResolucaoHostTests
    {
        private readonly SqlServerFixture _db;

        public ResolucaoHostTests(SqlServerFixture db) => _db = db;

        [Fact]
        public async Task Host_ativo_resolve_tenant_e_ignora_header_legado()
        {
            const int afiliada = 9101;
            const string host = "portal-a.teste";
            var email = "host-a@exemplo.com";
            await Seed.OperadorAsync(afiliada, email);
            await Seed.DominioAsync(afiliada, host, ativo: true);

            using var factory = new WlApiFactory(_db, afiliada, host);
            using var client = factory.ClienteAnonimo();
            client.DefaultRequestHeaders.Add("X-Tenant-AfiliadaId", "9999");

            var resposta = await client.PostAsJsonAsync("/api/wl/auth/login",
                new { Email = email, Senha = Seed.SenhaPadrao });

            resposta.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task Host_desconhecido_retorna_404_antes_do_controller()
        {
            using var factory = new WlApiFactory(_db, 9102, "conhecido.teste");
            using var client = factory.ClienteAnonimo("desconhecido.teste");

            var resposta = await client.GetAsync("/api/wl/config/branding");

            resposta.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Fact]
        public async Task Jwt_de_outro_tenant_retorna_401()
        {
            const int afiliadaA = 9103;
            const int afiliadaB = 9104;
            var email = "jwt-a@exemplo.com";
            await Seed.OperadorAsync(afiliadaA, email);
            await Seed.DominioAsync(afiliadaA, "jwt-a.teste", ativo: true);
            await Seed.DominioAsync(afiliadaB, "jwt-b.teste", ativo: true);

            using var factoryA = new WlApiFactory(_db, afiliadaA, "jwt-a.teste");
            var token = await factoryA.ObterTokenParaTesteAsync(email, Seed.SenhaPadrao);

            using var factoryB = new WlApiFactory(_db, afiliadaB, "jwt-b.teste");
            using var clientB = factoryB.ClienteAnonimo();
            clientB.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var resposta = await clientB.GetAsync("/api/wl/auth/me");

            resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task Branding_publico_vem_do_tenant_resolvido()
        {
            const int afiliada = 9105;
            const string host = "branding-a.teste";
            await Seed.DominioAsync(afiliada, host, ativo: true);
            await Seed.BrandingAsync(afiliada, "Marca A", "https://cdn.teste/a.svg", "#112233");

            using var factory = new WlApiFactory(_db, afiliada, host);
            using var client = factory.ClienteAnonimo();

            var resposta = await client.GetFromJsonAsync<BrandingResposta>("/api/wl/config/branding");

            resposta.Should().NotBeNull();
            resposta!.NomeExibicao.Should().Be("Marca A");
            resposta.PrimaryColor.Should().Be("#112233");
        }

        private sealed record BrandingResposta(string NomeExibicao, string LogoUrl, string PrimaryColor);
    }
}
