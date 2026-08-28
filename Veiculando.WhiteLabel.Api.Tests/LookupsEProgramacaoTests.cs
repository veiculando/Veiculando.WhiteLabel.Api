using System.Net.Http.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests
{
    [Collection(DatabaseCollection.Nome)]
    public class LookupsEProgramacaoTests
    {
        private const int Afiliada = 8900;
        private readonly SqlServerFixture _db;

        public LookupsEProgramacaoTests(SqlServerFixture db) => _db = db;

        /// <summary>
        /// O lookup de cidades e derivado do inventario da propria exibidora — nao
        /// e a lista de cidades do Brasil. Uma cidade onde so outra afiliada tem
        /// local nao pode aparecer, senao o formulario de cadastro revelaria onde
        /// a concorrencia opera.
        /// </summary>
        [Fact]
        public async Task Lookup_de_cidades_reflete_apenas_o_inventario_proprio()
        {
            var email = "lookup-cid@exemplo.com";
            await Seed.OperadorAsync(Afiliada, email, new[] { "PecaGerenciar" });
            await Seed.LocalAsync(Afiliada, "LCID-A");

            using var factory = new WlApiFactory(_db, Afiliada);
            using var client = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);

            var cidades = await client.GetFromJsonAsync<CidadeDto[]>("/api/wl/lookups/cidades");

            cidades.Should().NotBeNull().And.NotBeEmpty();
            cidades!.Should().OnlyContain(c => c.Sigla == "SP");
        }

        [Fact]
        public async Task Lookup_de_periodos_lista_bi_semanas_ativas()
        {
            var email = "lookup-per@exemplo.com";
            await Seed.OperadorAsync(Afiliada, email);

            var localId = await Seed.LocalAsync(Afiliada, "LPER-A");
            var pecaId = await Seed.PecaAsync(localId, "PPER-A");
            await Seed.ReservaAsync(Afiliada, "RPER-A", pecaId);

            using var factory = new WlApiFactory(_db, Afiliada);
            using var client = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);

            var periodos = await client.GetFromJsonAsync<PeriodoDto[]>("/api/wl/lookups/periodos");

            periodos.Should().NotBeNull().And.NotBeEmpty();
        }

        [Fact]
        public async Task Grade_de_programacao_traz_status_por_peca_e_periodo()
        {
            var email = "prog@exemplo.com";
            await Seed.OperadorAsync(Afiliada, email, new[] { "PecaGerenciar" });

            var localId = await Seed.LocalAsync(Afiliada, "LPROG-A");
            var pecaId = await Seed.PecaAsync(localId, "PPROG-A");
            await Seed.ReservaAsync(Afiliada, "RPROG-A", pecaId);
            await Seed.StatusProgramacaoAsync(pecaId);

            using var factory = new WlApiFactory(_db, Afiliada);
            using var client = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);

            var resposta = await client.PostAsJsonAsync("/api/wl/programacao/listar",
                new { IdPeriodo = (int?)null, IdLocal = (int?)null });

            resposta.EnsureSuccessStatusCode();

            var pagina = await resposta.Content.ReadFromJsonAsync<PaginaDto<GradeDto>>();
            pagina.Should().NotBeNull();
            pagina!.Itens.Should().Contain(g => g.PecaCodigo == "PPROG-A");
        }

        /// <summary>
        /// A grade e montada a partir de PecaPeriodoStatus filtrando por
        /// `pps.Peca.Local.IdAfiliada`. Se esse filtro cair, a disponibilidade do
        /// inventario alheio vaza.
        /// </summary>
        [Fact]
        public async Task Grade_nao_traz_pecas_de_outra_afiliada()
        {
            const int outra = 9100;

            var email = "prog-iso@exemplo.com";
            await Seed.OperadorAsync(Afiliada, email, new[] { "PecaGerenciar" });

            var localDeB = await Seed.LocalAsync(outra, "LPRG-B");
            var pecaDeB = await Seed.PecaAsync(localDeB, "PPRG-B");
            await Seed.ReservaAsync(outra, "RPRG-B", pecaDeB);
            await Seed.StatusProgramacaoAsync(pecaDeB);

            using var factory = new WlApiFactory(_db, Afiliada);
            using var client = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);

            var resposta = await client.PostAsJsonAsync("/api/wl/programacao/listar",
                new { IdPeriodo = (int?)null, IdLocal = (int?)null });

            resposta.EnsureSuccessStatusCode();
            var pagina = await resposta.Content.ReadFromJsonAsync<PaginaDto<GradeDto>>();

            pagina.Should().NotBeNull();
            pagina!.Itens.Should().NotContain(g => g.PecaCodigo == "PPRG-B");
        }

        private sealed record CidadeDto(int Id, string Nome, string Sigla);
        private sealed record PeriodoDto(int Id, string Nome);
        private sealed record GradeDto(int PecaId, string PecaCodigo, int LocalId, string LocalCodigo, int PeriodoId, string Status);
    }
}
