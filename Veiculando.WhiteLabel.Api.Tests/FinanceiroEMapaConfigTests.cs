using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests
{
    /// <summary>
    /// HF-2 (D2) — os endpoints que ficaram órfãos na branch do plano 73a7245b:
    /// <c>dashboard/kpis-financeiro</c>, <c>relatorios/resumo</c> e
    /// <c>lookups/mapa-config</c>. Valores de peças está em <see cref="PecasValoresTests"/>.
    /// </summary>
    /// <remarks>
    /// Lê o JSON cru, com os nomes em camelCase que a Exibidora consome
    /// (<c>DashboardFinanceiroKpis</c>, <c>RelatorioResumo</c> e <c>MapaConfig</c>
    /// em <c>wl.models.ts</c>). Um DTO C# esconderia uma divergência de nome.
    /// </remarks>
    [Collection(DatabaseCollection.Nome)]
    public class FinanceiroEMapaConfigTests
    {
        // Pares próprios por teste: o banco é compartilhado, e uma inserção
        // semeada por outro teste na mesma afiliada e período mudaria as contagens.
        private const int AfiliadaA = 9200;
        private const int ResumoA = 9220, ResumoB = 9221;
        private const int KpiA = 9230, KpiB = 9231;
        private const int PeriodoDasInsercoes = 1;
        private const int PeriodoInexistente = 987654;

        private static readonly string[] Financeiro = { "FinanceiroVisualizar" };

        private readonly SqlServerFixture _db;

        public FinanceiroEMapaConfigTests(SqlServerFixture db) => _db = db;

        private static async Task<JsonElement> JsonAsync(System.Net.Http.HttpResponseMessage resposta)
        {
            using var doc = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync());
            return doc.RootElement.Clone();
        }

        /// <summary>
        /// Uma inserção na afiliada A e outra na B, no mesmo período. Devolve o
        /// e-mail do operador de A.
        /// </summary>
        private static async Task<string> SemearDuasAfiliadasAsync(string prefixo, int afiliadaA, int afiliadaB)
        {
            var operadorA = $"{prefixo}-a@exemplo.com";
            await Seed.OperadorAsync(afiliadaA, operadorA, Financeiro);

            var pecaA = await Seed.PecaAsync(await Seed.LocalAsync(afiliadaA, $"{prefixo}-LA"), $"{prefixo}-PA");
            await Seed.InsercaoAsync(afiliadaA, $"{prefixo}-PIA", pecaA);

            var pecaB = await Seed.PecaAsync(await Seed.LocalAsync(afiliadaB, $"{prefixo}-LB"), $"{prefixo}-PB");
            await Seed.InsercaoAsync(afiliadaB, $"{prefixo}-PIB", pecaB);

            return operadorA;
        }

        [Fact]
        public async Task Resumo_traz_valores_reais_so_da_propria_afiliada_e_receita_indisponivel_nao_zero()
        {
            var operador = await SemearDuasAfiliadasAsync("HF2-RES", ResumoA, ResumoB);

            using var factory = new WlApiFactory(_db, ResumoA);
            using var client = await factory.ClienteAutenticadoAsync(operador, Seed.SenhaPadrao);

            var resposta = await client.GetAsync($"/api/wl/relatorios/resumo?periodoId={PeriodoDasInsercoes}");
            resposta.StatusCode.Should().Be(HttpStatusCode.OK);
            var corpo = await JsonAsync(resposta);

            corpo.GetProperty("periodoId").GetInt32().Should().Be(PeriodoDasInsercoes);
            corpo.GetProperty("pedidosInsercao").GetProperty("total").GetInt32().Should().Be(1,
                "a inserção da afiliada B no mesmo período não pode entrar no resumo de A");
            corpo.GetProperty("valorLiquidoVeiculacaoTotal").GetDecimal().Should().Be(1000m,
                "o valor vem do PedidoInsercao semeado, não de um zero de placeholder");

            var porStatus = corpo.GetProperty("pedidosInsercao").GetProperty("porStatus").EnumerateArray().Single();
            porStatus.GetProperty("status").GetString().Should().NotBeNullOrEmpty();
            porStatus.GetProperty("quantidade").GetInt32().Should().Be(1);
            porStatus.GetProperty("valorLiquido").GetDecimal().Should().Be(1000m);

            corpo.GetProperty("reservas").GetProperty("total").ValueKind.Should().Be(JsonValueKind.Number);
            corpo.GetProperty("pecasVeiculadas").ValueKind.Should().Be(JsonValueKind.Number);

            var faturamento = corpo.GetProperty("faturamentoPrevisto");
            faturamento.GetProperty("disponivel").GetBoolean().Should().BeFalse();
            faturamento.GetProperty("valor").ValueKind.Should().Be(JsonValueKind.Null,
                "sem fórmula definida a receita é nula, nunca R$ 0,00");
        }

        [Fact]
        public async Task Resumo_de_periodo_inexistente_e_404_e_nao_um_resumo_zerado()
        {
            var operador = "hf2-res-404@exemplo.com";
            await Seed.OperadorAsync(AfiliadaA, operador, Financeiro);

            using var factory = new WlApiFactory(_db, AfiliadaA);
            using var client = await factory.ClienteAutenticadoAsync(operador, Seed.SenhaPadrao);

            (await client.GetAsync($"/api/wl/relatorios/resumo?periodoId={PeriodoInexistente}"))
                .StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Fact]
        public async Task Resumo_exige_FinanceiroVisualizar()
        {
            var operador = "hf2-res-sem-perm@exemplo.com";
            await Seed.OperadorAsync(AfiliadaA, operador, new[] { "PecaGerenciar" });

            using var factory = new WlApiFactory(_db, AfiliadaA);
            using var client = await factory.ClienteAutenticadoAsync(operador, Seed.SenhaPadrao);

            (await client.GetAsync($"/api/wl/relatorios/resumo?periodoId={PeriodoDasInsercoes}"))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        [Fact]
        public async Task KpisFinanceiro_traz_o_contrato_da_Exibidora_escopado_pela_afiliada()
        {
            var operador = await SemearDuasAfiliadasAsync("HF2-KPI", KpiA, KpiB);

            using var factory = new WlApiFactory(_db, KpiA);
            using var client = await factory.ClienteAutenticadoAsync(operador, Seed.SenhaPadrao);

            var resposta = await client.GetAsync($"/api/wl/dashboard/kpis-financeiro?periodoId={PeriodoDasInsercoes}");
            resposta.StatusCode.Should().Be(HttpStatusCode.OK);
            var corpo = await JsonAsync(resposta);

            var periodo = corpo.GetProperty("periodo");
            periodo.GetProperty("id").GetInt32().Should().Be(PeriodoDasInsercoes);
            foreach (var campo in new[] { "nome", "dataInicio", "dataFim", "vigente" })
                periodo.TryGetProperty(campo, out _).Should().BeTrue($"periodo.{campo} é lido pela Exibidora");

            var faturamento = corpo.GetProperty("faturamentoPrevisto");
            faturamento.GetProperty("disponivel").GetBoolean().Should().BeFalse();
            faturamento.GetProperty("valor").ValueKind.Should().Be(JsonValueKind.Null);
            faturamento.TryGetProperty("variacaoPercentualVsCicloAnterior", out _).Should().BeTrue();

            var ocupacao = corpo.GetProperty("taxaOcupacaoOOH");
            foreach (var campo in new[] { "disponivel", "formulaProvisoria", "percentual", "pecasOcupadas", "pecasAtivas" })
                ocupacao.TryGetProperty(campo, out _).Should().BeTrue($"taxaOcupacaoOOH.{campo} é lido pela Exibidora");

            corpo.GetProperty("campanhasAtivas").ValueKind.Should().Be(JsonValueKind.Number);

            corpo.GetProperty("demandasPendentes").GetProperty("pedidosInsercao").GetInt32().Should().Be(1,
                "a inserção pendente da afiliada B não pode aparecer no dashboard de A");
        }

        [Fact]
        public async Task KpisFinanceiro_exige_FinanceiroVisualizar()
        {
            var operador = "hf2-kpi-sem-perm@exemplo.com";
            await Seed.OperadorAsync(AfiliadaA, operador, new[] { "PecaGerenciar" });

            using var factory = new WlApiFactory(_db, AfiliadaA);
            using var client = await factory.ClienteAutenticadoAsync(operador, Seed.SenhaPadrao);

            (await client.GetAsync($"/api/wl/dashboard/kpis-financeiro?periodoId={PeriodoDasInsercoes}"))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        [Fact]
        public async Task MapaConfig_autenticado_responde_com_a_chave_em_camelCase()
        {
            var operador = "hf2-mapa@exemplo.com";
            await Seed.OperadorAsync(AfiliadaA, operador);

            using var factory = new WlApiFactory(_db, AfiliadaA);
            using var client = await factory.ClienteAutenticadoAsync(operador, Seed.SenhaPadrao);

            var resposta = await client.GetAsync("/api/wl/lookups/mapa-config");
            resposta.StatusCode.Should().Be(HttpStatusCode.OK);

            var corpo = await JsonAsync(resposta);
            corpo.EnumerateObject().Select(p => p.Name).Should().Equal(new[] { "googleMapsApiKey" });
            corpo.GetProperty("googleMapsApiKey").ValueKind.Should().Be(JsonValueKind.Null,
                "sem chave configurada o wizard cai para coordenadas manuais, e string vazia não pode passar por chave");
        }

        [Fact]
        public async Task MapaConfig_anonimo_e_401()
        {
            using var factory = new WlApiFactory(_db, AfiliadaA);
            using var client = factory.ClienteAnonimo();

            (await client.GetAsync("/api/wl/lookups/mapa-config"))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
    }
}
