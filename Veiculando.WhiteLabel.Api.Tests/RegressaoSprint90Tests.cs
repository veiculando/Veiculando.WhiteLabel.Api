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
    /// Trava os defeitos encontrados na revisao da Sprint 9.0.
    /// </summary>
    /// <remarks>
    /// Cada teste aqui falhava antes da correcao correspondente. Sao o motivo de
    /// a suite existir contra SQL Server real: nenhum deles quebra com DbSet
    /// mockado.
    /// </remarks>
    [Collection(DatabaseCollection.Nome)]
    public class RegressaoSprint90Tests
    {
        private readonly SqlServerFixture _db;

        public RegressaoSprint90Tests(SqlServerFixture db) => _db = db;

        /// <summary>
        /// `PermissoesRaw.Split(...)` estava dentro do `Select` traduzido para SQL.
        /// O EF6 nao traduz `String.Split`: a query lancava NotSupportedException e
        /// a listagem de operadores nao carregava nunca — 500 em toda chamada.
        ///
        /// Um teste com DbSet mockado passaria: em LINQ-to-Objects, `Split`
        /// funciona.
        /// </summary>
        [Fact]
        public async Task GetAll_de_usuarios_nao_estoura_traduzindo_Split_para_SQL()
        {
            const int afiliada = 7200;
            var email = "listagem@exemplo.com";

            await Seed.OperadorAsync(afiliada, email,
                new[] { "UsuarioAfiliadaGerenciar", "PecaGerenciar" });

            using var factory = new WlApiFactory(_db, afiliada);
            using var client = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);

            var resposta = await client.GetAsync("/api/wl/usuarios");

            resposta.StatusCode.Should().Be(HttpStatusCode.OK,
                "String.Split dentro do Select fazia o EF6 lancar NotSupportedException");

            var usuarios = await resposta.Content.ReadFromJsonAsync<UsuarioDto[]>();
            usuarios.Should().ContainSingle(u => u.Email == email)
                .Which.Permissoes.Should().BeEquivalentTo(
                    new[] { "UsuarioAfiliadaGerenciar", "PecaGerenciar" },
                    "as permissoes precisam vir divididas, nao como string bruta");
        }

        /// <summary>
        /// O contexto do core tem LazyLoadingEnabled = false. Um `p.Local.IdAfiliada`
        /// no WHERE vira JOIN no SQL mas NAO popula a navegacao, entao `peca.Local`
        /// chegava null e `peca.Local.Codigo` na projecao estourava
        /// NullReferenceException — 500 em todo detalhe de peca.
        /// </summary>
        [Fact]
        public async Task GetById_de_peca_carrega_o_Local_e_nao_estoura_NRE()
        {
            const int afiliada = 7201;
            var email = "detalhe-peca@exemplo.com";

            await Seed.OperadorAsync(afiliada, email, new[] { "PecaGerenciar" });
            var localId = await Seed.LocalAsync(afiliada, "LOC7201");
            var pecaId = await Seed.PecaAsync(localId, "PEC7201");

            using var factory = new WlApiFactory(_db, afiliada);
            using var client = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);

            var resposta = await client.GetAsync($"/api/wl/pecas/{pecaId}");

            resposta.StatusCode.Should().Be(HttpStatusCode.OK,
                "sem Include(p => p.Local) a projecao estourava NullReferenceException");

            var peca = await resposta.Content.ReadFromJsonAsync<PecaDto>();
            peca!.LocalCodigo.Should().Be("LOC7201",
                "o codigo do local vem da navegacao que precisa ter sido carregada");
            peca.CodigoInterno.Should().Be("INT-PEC7201");
            peca.IdTipoSuporte.Should().Be(1);
            peca.Formato.Should().BeEquivalentTo(new PecaFormatoDto(9, 3, 0));
            peca.Via.Should().BeEquivalentTo(new PecaViaDto(0, 2, 60, 0));
            peca.PeriodicidadePadrao.Should().Be(0);
            peca.IdsSubstratoTipo.Should().NotBeNull();
        }

        /// <summary>
        /// Pecas criadas pela Exibidora nascem aguardando aprovacao. Se a lista e
        /// o detalhe aceitarem apenas Ativo, o registro desaparece logo depois do
        /// cadastro e nao pode ser revisado nem editado pelo operador.
        /// </summary>
        [Fact]
        public async Task Peca_pendente_aparece_na_lista_e_pode_ser_aberta_para_edicao()
        {
            const int afiliada = 7204;
            var email = "peca-pendente@exemplo.com";

            await Seed.OperadorAsync(afiliada, email, new[] { "PecaGerenciar" });
            var localId = await Seed.LocalAsync(afiliada, "LOC7204");
            var pecaId = await Seed.PecaAsync(
                localId,
                "PEC7204",
                Seed.StatusExibicaoLocal.AprovacaoPendente);

            using var factory = new WlApiFactory(_db, afiliada);
            using var client = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);

            var lista = await client.GetFromJsonAsync<PecaDto[]>("/api/wl/pecas");
            lista.Should().ContainSingle(p => p.Id == pecaId)
                .Which.StatusExibicao.Should().Be(2);

            var detalhe = await client.GetAsync($"/api/wl/pecas/{pecaId}");
            detalhe.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        /// <summary>
        /// O detalhe do local projeta Cidade e UF a partir de navegacoes. Sem
        /// Include elas voltavam null, e o `?.` na projecao escondia isso: o
        /// formulario de edicao abria com a cidade em branco e salvava por cima.
        /// </summary>
        [Fact]
        public async Task GetById_de_local_devolve_cidade_e_uf()
        {
            const int afiliada = 7202;
            var email = "detalhe-local@exemplo.com";

            await Seed.OperadorAsync(afiliada, email, new[] { "PecaGerenciar" });
            var localId = await Seed.LocalAsync(afiliada, "LOC7202");

            using var factory = new WlApiFactory(_db, afiliada);
            using var client = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);

            var resposta = await client.GetAsync($"/api/wl/locais/{localId}");

            resposta.StatusCode.Should().Be(HttpStatusCode.OK);

            var local = await resposta.Content.ReadFromJsonAsync<LocalDetalheDto>();
            local!.Cidade.Should().NotBeNullOrEmpty("sem Include(l => l.Cidade.Estado) vinha null");
            local.UF.Should().Be("SP");
        }

        [Fact]
        public async Task Demografia_de_local_tem_endpoints_reais_e_arrays_nunca_nulos()
        {
            const int afiliada = 7205;
            var email = "demografia@exemplo.com";

            await Seed.OperadorAsync(afiliada, email, new[] { "PecaGerenciar" });
            var localId = await Seed.LocalAsync(afiliada, "LOC7205");

            using var factory = new WlApiFactory(_db, afiliada);
            using var client = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);

            var vazio = await client.GetFromJsonAsync<LocalPublicoDto>($"/api/wl/locais/{localId}/publico");
            vazio!.Audiencia.Should().BeNull();
            vazio.FaixaEtaria.Should().BeEmpty();
            vazio.FaixaRenda.Should().BeEmpty();
            vazio.PerfisPsicograficos.Should().BeEmpty();
            vazio.Segmentos.Should().BeEmpty();
            vazio.PoiCategorias.Should().BeEmpty();

            var resposta = await client.PutAsJsonAsync($"/api/wl/locais/{localId}/publico", new
            {
                Audiencia = (int?)null,
                TipoMedicao = (int?)null,
                Fonte = (string)null,
                Genero = 0,
                FaixaEtaria = new[] { 1, 2 },
                FaixaRenda = System.Array.Empty<int>(),
                PerfisPsicograficos = System.Array.Empty<int>(),
                Segmentos = System.Array.Empty<int>(),
                PoiCategorias = System.Array.Empty<int>()
            });

            resposta.StatusCode.Should().Be(HttpStatusCode.OK);
            var chamada = factory.Core.Requisicoes.Should().ContainSingle().Which;
            chamada.Url.Should().EndWith("api/local/publico");

            using var corpo = JsonDocument.Parse(chamada.Corpo);
            corpo.RootElement.GetProperty("IdLocal").GetInt32().Should().Be(localId);
            corpo.RootElement.GetProperty("FaixaEtaria").GetArrayLength().Should().Be(2);
            corpo.RootElement.GetProperty("FaixaRenda").GetArrayLength().Should().Be(0);
        }

        /// <summary>
        /// A exclusao de operador e soft (StatusExibicao = Deletado), mas
        /// UK_WlUsuario_Email_Afiliada e unico sobre (Email, AfiliadaId) SEM filtro.
        /// Como o Create checava duplicidade so entre Ativo, recriar um excluido
        /// passava na validacao e estourava violacao de constraint no SaveChanges —
        /// o operador via 500 sem explicacao.
        /// </summary>
        [Fact]
        public async Task Recriar_operador_excluido_devolve_400_e_nao_500()
        {
            const int afiliada = 7203;
            var admin = "admin-recriar@exemplo.com";
            var alvo = "reaproveitado@exemplo.com";

            await Seed.OperadorAsync(afiliada, admin, new[] { "UsuarioAfiliadaGerenciar" });
            var alvoId = await Seed.OperadorAsync(afiliada, alvo);

            using var factory = new WlApiFactory(_db, afiliada);
            using var client = await factory.ClienteAutenticadoAsync(admin, Seed.SenhaPadrao);

            (await client.DeleteAsync($"/api/wl/usuarios/{alvoId}"))
                .StatusCode.Should().Be(HttpStatusCode.NoContent);

            var recriacao = await client.PostAsJsonAsync("/api/wl/usuarios", new
            {
                Nome = "Reaproveitado",
                Email = alvo,
                Senha = "SenhaDeTeste123",
            });

            recriacao.StatusCode.Should().Be(HttpStatusCode.BadRequest,
                "o e-mail de um operador excluido permanece reservado pelo indice unico");
        }

        private sealed record UsuarioDto(int Id, string Nome, string Email, string[] Permissoes);
        private sealed record PecaDto(
            int Id,
            string Codigo,
            string CodigoInterno,
            int IdLocal,
            string LocalCodigo,
            int IdTipoSuporte,
            int PeriodicidadePadrao,
            decimal ValorPadrao,
            PecaFormatoDto Formato,
            PecaViaDto Via,
            int[] IdsSubstratoTipo,
            int StatusExibicao);
        private sealed record PecaFormatoDto(decimal Largura, decimal Altura, int Juncao);
        private sealed record PecaViaDto(int ViaTipo, int Faixas, int Velociade, int Pedestre);
        private sealed record LocalDetalheDto(int Id, string Codigo, string Descricao, string Cidade, string UF);
        private sealed record LocalPublicoDto(
            int? Audiencia,
            int? TipoMedicao,
            string Fonte,
            int Genero,
            int[] FaixaEtaria,
            int[] FaixaRenda,
            int[] PerfisPsicograficos,
            int[] Segmentos,
            int[] PoiCategorias);
    }
}
