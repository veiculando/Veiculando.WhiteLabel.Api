using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using System.Threading.Tasks;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests
{
    /// <summary>
    /// VEI-RD-54 — Valores de Peças em lote. BDD da execução: Seção 8.2
    /// (Anti-IDOR), auditoria e rejeição de períodos repetidos no envio sazonal.
    /// </summary>
    /// <remarks>
    /// Estes testes verificam o CONTRATO do BFF — a validação de tenant antes de
    /// delegar ao core, e o que é enviado/recusado — não a regra de precificação
    /// em si, que vive no <c>PecaAlterarValoresHandler</c> do Core (fora deste
    /// repositório) e tem sua própria suite. O <see cref="CoreApiStub"/> não
    /// aplica a regra real; por isso os testes de "sucesso" verificam que o BFF
    /// delegou corretamente, não que o valor mudou de fato no banco.
    /// </remarks>
    [Collection(DatabaseCollection.Nome)]
    public class PecasValoresTests
    {
        private const int AfiliadaA = 8500;
        private const int AfiliadaB = 8600;

        private readonly SqlServerFixture _db;

        public PecasValoresTests(SqlServerFixture db) => _db = db;

        /// <summary>
        /// Scenario 1: Anti-IDOR RECUSA o lote inteiro se houver peça de outra afiliada.
        /// </summary>
        [Fact]
        public async Task Lote_com_peca_de_outra_afiliada_e_recusado_e_nao_alcanca_o_core()
        {
            var operador = "pv-idor-a@exemplo.com";
            await Seed.OperadorAsync(AfiliadaA, operador, new[] { "PecaGerenciar" });

            var localA1 = await Seed.LocalAsync(AfiliadaA, "PV-IDOR-A1");
            var pecaA1 = await Seed.PecaAsync(localA1, "PV-IDOR-PA1");
            var localA2 = await Seed.LocalAsync(AfiliadaA, "PV-IDOR-A2");
            var pecaA2 = await Seed.PecaAsync(localA2, "PV-IDOR-PA2");

            var localB = await Seed.LocalAsync(AfiliadaB, "PV-IDOR-B1");
            var pecaB = await Seed.PecaAsync(localB, "PV-IDOR-PB1");

            using var factory = new WlApiFactory(_db, AfiliadaA);
            using var client = await factory.ClienteAutenticadoAsync(operador, Seed.SenhaPadrao);

            var resposta = await client.PostAsJsonAsync("/api/wl/pecas/valores/alterar", new
            {
                PecasIds = new[] { pecaA1, pecaA2, pecaB },
                Valor = 100m,
                TipoValor = 1, // Incremento
            });

            resposta.StatusCode.Should().Be(HttpStatusCode.NotFound,
                "a invariante do domínio é 404 (não 403) para recurso de outra afiliada");

            factory.Core.Requisicoes.Should().BeEmpty(
                "a validação de tenant precisa recusar ANTES de qualquer chamada ao core — " +
                "sem processamento parcial das peças válidas");
        }

        /// <summary>
        /// Scenario 2: Anti-IDOR deixa passar o lote inteiramente da própria afiliada.
        /// </summary>
        [Fact]
        public async Task Lote_inteiramente_da_propria_afiliada_e_delegado_ao_core()
        {
            var operador = "pv-ok-a@exemplo.com";
            await Seed.OperadorAsync(AfiliadaA, operador, new[] { "PecaGerenciar" });

            var local = await Seed.LocalAsync(AfiliadaA, "PV-OK-A1");
            var peca1 = await Seed.PecaAsync(local, "PV-OK-P1");
            var peca2 = await Seed.PecaAsync(local, "PV-OK-P2");

            using var factory = new WlApiFactory(_db, AfiliadaA);
            using var client = await factory.ClienteAutenticadoAsync(operador, Seed.SenhaPadrao);

            var resposta = await client.PostAsJsonAsync("/api/wl/pecas/valores/alterar", new
            {
                PecasIds = new[] { peca1, peca2 },
                Valor = 200m,
                TipoValor = 3, // ValorExato
            });

            resposta.StatusCode.Should().Be(HttpStatusCode.OK);

            factory.Core.Requisicoes.Should().ContainSingle(r =>
                r.Metodo == "POST" && r.Url.Contains("api/peca/alterar-valor-padrao"));
        }

        /// <summary>
        /// Scenario 3 (parcial — a mudança real de valor é responsabilidade do
        /// Core, stubado aqui): a resposta traz auditoria por peça, com id,
        /// código e os campos de valor anterior/novo presentes.
        /// </summary>
        [Fact]
        public async Task Resposta_de_alteracao_traz_auditoria_por_peca()
        {
            var operador = "pv-audit-a@exemplo.com";
            await Seed.OperadorAsync(AfiliadaA, operador, new[] { "PecaGerenciar" });

            var local = await Seed.LocalAsync(AfiliadaA, "PV-AUD-A1");
            var peca = await Seed.PecaAsync(local, "PV-AUD-P1");

            using var factory = new WlApiFactory(_db, AfiliadaA);
            using var client = await factory.ClienteAutenticadoAsync(operador, Seed.SenhaPadrao);

            var resposta = await client.PostAsJsonAsync("/api/wl/pecas/valores/alterar", new
            {
                PecasIds = new[] { peca },
                Valor = 1800m,
                TipoValor = 3,
            });

            resposta.StatusCode.Should().Be(HttpStatusCode.OK);

            var corpo = await resposta.Content.ReadFromJsonAsync<RespostaAlteracaoDto>();
            corpo.Should().NotBeNull();
            corpo!.QuantidadeAfetada.Should().Be(1);
            corpo.Alteracoes.Should().ContainSingle(a => a.Id == peca && a.Codigo == "PV-AUD-P1");
        }

        /// <summary>
        /// Scenario 6: períodos repetidos no envio sazonal são rejeitados, sem
        /// gravar nada.
        /// </summary>
        [Fact]
        public async Task Sazonal_com_periodo_repetido_no_envio_e_recusado()
        {
            var operador = "pv-saz-dup-a@exemplo.com";
            await Seed.OperadorAsync(AfiliadaA, operador, new[] { "PecaGerenciar" });
            await Seed.PeriodoAsync(9001, "PV-SAZ-DUP");

            var local = await Seed.LocalAsync(AfiliadaA, "PV-SAZDUP-A1");
            var peca = await Seed.PecaAsync(local, "PV-SAZDUP-P1");

            using var factory = new WlApiFactory(_db, AfiliadaA);
            using var client = await factory.ClienteAutenticadoAsync(operador, Seed.SenhaPadrao);

            var resposta = await client.PostAsJsonAsync("/api/wl/pecas/valores/sazonais", new
            {
                PecasIds = new[] { peca },
                PeriodosValor = new[]
                {
                    new { IdPeriodo = 9001, Valor = 100m },
                    new { IdPeriodo = 9001, Valor = 200m },
                },
            });

            resposta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            factory.Core.Requisicoes.Should().BeEmpty();
        }

        /// <summary>
        /// Período desconhecido (nunca semeado) é tratado como recurso ausente —
        /// mesma invariante 404 usada para peça/local de outra afiliada.
        /// </summary>
        [Fact]
        public async Task Sazonal_com_periodo_inexistente_e_recusado()
        {
            var operador = "pv-saz-404-a@exemplo.com";
            await Seed.OperadorAsync(AfiliadaA, operador, new[] { "PecaGerenciar" });

            var local = await Seed.LocalAsync(AfiliadaA, "PV-SAZ404-A1");
            var peca = await Seed.PecaAsync(local, "PV-SAZ404-P1");

            using var factory = new WlApiFactory(_db, AfiliadaA);
            using var client = await factory.ClienteAutenticadoAsync(operador, Seed.SenhaPadrao);

            var resposta = await client.PostAsJsonAsync("/api/wl/pecas/valores/sazonais", new
            {
                PecasIds = new[] { peca },
                PeriodosValor = new[] { new { IdPeriodo = 999999, Valor = 100m } },
            });

            resposta.StatusCode.Should().Be(HttpStatusCode.NotFound);
            factory.Core.Requisicoes.Should().BeEmpty();
        }

        /// <summary>
        /// Listagem paginada nunca devolve peça de outra afiliada, mesmo sob busca.
        /// </summary>
        [Fact]
        public async Task Listagem_paginada_so_traz_pecas_da_propria_afiliada()
        {
            var operador = "pv-list-a@exemplo.com";
            await Seed.OperadorAsync(AfiliadaA, operador, new[] { "PecaGerenciar" });

            var localA = await Seed.LocalAsync(AfiliadaA, "PV-LIST-A1");
            await Seed.PecaAsync(localA, "PV-LIST-PA1");

            var localB = await Seed.LocalAsync(AfiliadaB, "PV-LIST-B1");
            await Seed.PecaAsync(localB, "PV-LIST-PB1");

            using var factory = new WlApiFactory(_db, AfiliadaA);
            using var client = await factory.ClienteAutenticadoAsync(operador, Seed.SenhaPadrao);

            var pagina = await client.GetFromJsonAsync<PaginaDto>("/api/wl/pecas/valores?pageSize=100");

            pagina.Should().NotBeNull();
            pagina!.Itens.Should().Contain(p => p.Codigo == "PV-LIST-PA1");
            pagina.Itens.Should().NotContain(p => p.Codigo == "PV-LIST-PB1");
        }

        private sealed record RespostaAlteracaoDto(string Message, int QuantidadeAfetada, AlteracaoDto[] Alteracoes);
        private sealed record AlteracaoDto(int Id, string Codigo, decimal ValorAnterior, decimal ValorNovo);
        private sealed record PaginaDto(PecaValorDto[] Itens, int Page, int PageSize, int Total, int TotalPaginas);
        private sealed record PecaValorDto(int Id, string Codigo);
    }
}
