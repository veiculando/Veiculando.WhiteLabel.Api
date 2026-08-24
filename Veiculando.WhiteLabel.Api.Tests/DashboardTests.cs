using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests
{
    /// <summary>
    /// <c>GET /api/wl/dashboard/kpis</c> — TP-B, seção 2: sem receita mockada,
    /// KPIs restantes escopados pela afiliada da instância.
    /// </summary>
    [Collection(DatabaseCollection.Nome)]
    public class DashboardTests
    {
        private const int Afiliada = 7300;
        private readonly SqlServerFixture _db;

        public DashboardTests(SqlServerFixture db) => _db = db;

        [Fact]
        public async Task Kpis_nao_expoe_mais_receita_mensal()
        {
            var afiliadaId = Afiliada;
            var email = "dashboard-sem-receita@exemplo.com";
            await Seed.OperadorAsync(afiliadaId, email);

            using var factory = new WlApiFactory(_db, afiliadaId);
            using var client = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);

            var resposta = await client.GetAsync("/api/wl/dashboard/kpis");
            resposta.StatusCode.Should().Be(HttpStatusCode.OK);

            // Lê o JSON cru em vez de desserializar num DTO: um DTO com a
            // propriedade ausente "passaria" mesmo se o BFF ainda mandasse o
            // campo — o que este teste precisa provar é que o campo NÃO ESTÁ
            // no payload, não que o cliente escolheu ignorá-lo.
            var corpo = await resposta.Content.ReadAsStringAsync();
            using var documento = JsonDocument.Parse(corpo);

            var propriedades = documento.RootElement.EnumerateObject().Select(p => p.Name).ToArray();

            propriedades.Should().NotContain(
                nome => nome.Equals("receitaMensal", System.StringComparison.OrdinalIgnoreCase),
                "o PRD proíbe apresentar zero como se fosse receita real; o campo foi removido, não zerado");

            // Os KPIs que continuam existindo não podem ter sumido junto.
            propriedades.Should().Contain(new[] { "locaisAtivos", "pecasEmExibicao", "pedidosPendentes" });
        }

        [Fact]
        public async Task Kpis_contam_apenas_recursos_da_propria_afiliada()
        {
            var afiliadaA = Afiliada + 1;
            var afiliadaB = Afiliada + 2;
            var emailA = "dashboard-tenant-a@exemplo.com";

            await Seed.OperadorAsync(afiliadaA, emailA);

            var localA1 = await Seed.LocalAsync(afiliadaA, "DA1");
            var localA2 = await Seed.LocalAsync(afiliadaA, "DA2");
            await Seed.PecaAsync(localA1, "PA1");

            var localB = await Seed.LocalAsync(afiliadaB, "DB1");
            await Seed.PecaAsync(localB, "PB1");
            await Seed.PecaAsync(localB, "PB2");

            using var factory = new WlApiFactory(_db, afiliadaA);
            using var client = await factory.ClienteAutenticadoAsync(emailA, Seed.SenhaPadrao);

            var resposta = await client.GetAsync("/api/wl/dashboard/kpis");
            resposta.StatusCode.Should().Be(HttpStatusCode.OK);

            var corpo = await resposta.Content.ReadFromJsonAsync<KpisResposta>();

            // 2 locais e 1 peça em A; a instância nunca deveria enxergar os 2
            // locais/peças de B — se enxergasse, os números batendo com o total
            // combinado (3 locais, 3 peças) denunciaria o vazamento.
            corpo!.LocaisAtivos.Should().Be(2);
            corpo.PecasEmExibicao.Should().Be(1);
        }

        private sealed record KpisResposta(int LocaisAtivos, int PecasEmExibicao, int PedidosPendentes);
    }
}
