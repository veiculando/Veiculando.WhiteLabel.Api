using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests
{
    /// <summary>
    /// Anunciantes (Sprint 10.5 BE-1, test plan cec4eea1). Os cinco testes
    /// obrigatórios do plano, mais investimento histórico e histórico por tenant.
    /// </summary>
    [Collection(DatabaseCollection.Nome)]
    public class AnunciantesTests
    {
        private readonly SqlServerFixture _db;

        public AnunciantesTests(SqlServerFixture db) => _db = db;

        private static readonly string[] Permissao = { "ClienteGerenciar" };

        private async Task<(WlApiFactory Factory, HttpClient Client)> OperadorAsync(int afiliada, string email)
        {
            await Seed.OperadorAsync(afiliada, email, Permissao);
            var factory = new WlApiFactory(_db, afiliada);
            var client = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);
            return (factory, client);
        }

        /// <summary>
        /// Simula o ClienteHandler/AfiliadaClienteVinculoHandler do core: sem isso
        /// o BFF não encontraria o vínculo que o core teria gravado.
        /// </summary>
        private static void CoreGravaAnunciantes(WlApiFactory factory)
        {
            factory.Core.Responder = req =>
            {
                var corpo = JObject.Parse(string.IsNullOrEmpty(req.Corpo) ? "{}" : req.Corpo);

                if (req.Url.EndsWith("/vincular-afiliada"))
                {
                    SeedCadastros.VinculoClienteAsync((int)corpo["IdAfiliada"], (int)corpo["IdCliente"])
                        .GetAwaiter().GetResult();
                }
                else if (req.Url.EndsWith("api/cliente") && (int)corpo["Id"] == 0)
                {
                    var id = SeedCadastros.ClienteAsync(
                        (string)corpo["Cnpj"]["Numero"], (string)corpo["Codigo"],
                        (string)corpo["Nome"], (string)corpo["RazaoSocial"]).GetAwaiter().GetResult();
                    SeedCadastros.VinculoClienteAsync((int)corpo["IdAfiliada"], id).GetAwaiter().GetResult();
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"sucesso\":true}", System.Text.Encoding.UTF8, "application/json")
                };
            };
        }

        [Fact]
        public async Task Anti_IDOR_anunciante_de_outra_afiliada_retorna_404_em_toda_rota_por_id()
        {
            const int a = 9501, b = 9502;
            var alheio = await SeedCadastros.ClienteAsync("10502000000101", "AN0201", "Alheio B");
            await SeedCadastros.VinculoClienteAsync(b, alheio);

            var (factory, client) = await OperadorAsync(a, "anun-idor@exemplo.com");
            using var _ = factory;

            (await client.GetAsync($"/api/wl/anunciantes/{alheio}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await client.PutAsJsonAsync($"/api/wl/anunciantes/{alheio}", new { DescontoNegociado = 0 }))
                .StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await client.PatchAsJsonAsync($"/api/wl/anunciantes/{alheio}/status", new { Ativo = false }))
                .StatusCode.Should().Be(HttpStatusCode.NotFound);

            factory.Core.Requisicoes.Should().BeEmpty("nada de outra afiliada pode chegar ao core");
            (await SeedCadastros.ContarAsync(
                    "SELECT COUNT(*) FROM AfiliadaCliente WHERE IdCliente = @p0 AND Status = 1", alheio))
                .Should().Be(1, "o vínculo de B continua ativo");
        }

        [Fact]
        public async Task Listagem_so_traz_clientes_com_vinculo_nesta_afiliada()
        {
            const int a = 9503, b = 9504;
            var meu = await SeedCadastros.ClienteAsync("10503000000101", "AN0301", "Meu Anunciante");
            var deB = await SeedCadastros.ClienteAsync("10503000000202", "AN0302", "Anunciante de B");
            var semVinculo = await SeedCadastros.ClienteAsync("10503000000303", "AN0303", "Sem Vinculo");
            await SeedCadastros.VinculoClienteAsync(a, meu);
            await SeedCadastros.VinculoClienteAsync(b, deB);

            var (factory, client) = await OperadorAsync(a, "anun-lista@exemplo.com");
            using var _ = factory;

            var pagina = await client.GetFromJsonAsync<PaginaDto<AnuncianteDto>>("/api/wl/anunciantes?pageSize=100");

            pagina!.Itens.Select(i => i.Id).Should().Equal(meu);
            pagina.Total.Should().Be(1);
            semVinculo.Should().NotBe(meu);
        }

        [Fact]
        public async Task Cnpj_existente_cria_so_o_vinculo_e_nunca_um_segundo_cliente()
        {
            const int a = 9505, b = 9506;
            const string cnpj = "10505000000101";
            var existente = await SeedCadastros.ClienteAsync(cnpj, "AN0501", "Ja Existe em B");
            await SeedCadastros.VinculoClienteAsync(b, existente);
            var segmento = await SeedCadastros.SegmentoAsync("Seg 10505");

            var (factory, client) = await OperadorAsync(a, "anun-cnpj@exemplo.com");
            using var _ = factory;
            CoreGravaAnunciantes(factory);

            var resposta = await client.PostAsJsonAsync("/api/wl/anunciantes", new
            {
                Cnpj = "10.505.000/0001-01",
                RazaoSocial = "Nome Diferente Que Nao Deve Sobrescrever",
                SegmentoId = segmento,
                Contato = "Ana",
                Email = "ana@exemplo.com"
            });

            resposta.StatusCode.Should().Be(HttpStatusCode.OK);
            var corpo = await resposta.Content.ReadFromJsonAsync<CadastroDto>();
            corpo!.Id.Should().Be(existente);
            corpo.Criado.Should().BeFalse();

            (await SeedCadastros.ContarAsync("SELECT COUNT(*) FROM Cliente WHERE Cnpj = @p0", cnpj)).Should().Be(1);
            factory.Core.Requisicoes.Should().ContainSingle()
                .Which.Url.Should().EndWith($"api/cliente/{existente}/vincular-afiliada");

            var detalhe = await client.GetFromJsonAsync<AnuncianteDto>($"/api/wl/anunciantes/{existente}");
            detalhe!.RazaoSocial.Should().Be("Ja Existe em B Comercio Ltda", "o Cliente compartilhado não é reescrito");
            detalhe.Segmento!.Id.Should().Be(segmento);
            detalhe.Contato.Should().Be("Ana");
            detalhe.Email.Should().Be("ana@exemplo.com");

            // Segunda tentativa: já é desta exibidora — 409 com o id, sem novo vínculo.
            var repetida = await client.PostAsJsonAsync("/api/wl/anunciantes", new { Cnpj = cnpj, RazaoSocial = "x" });
            repetida.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await SeedCadastros.ContarAsync("SELECT COUNT(*) FROM AfiliadaCliente WHERE IdCliente = @p0", existente))
                .Should().Be(2, "um vínculo em B e um em A");
        }

        [Fact]
        public async Task Cnpj_novo_cria_pelo_core_e_grava_dados_comerciais_no_vinculo()
        {
            const int a = 9507;
            var (factory, client) = await OperadorAsync(a, "anun-novo@exemplo.com");
            using var _ = factory;
            CoreGravaAnunciantes(factory);

            var resposta = await client.PostAsJsonAsync("/api/wl/anunciantes", new
            {
                Cnpj = "10507000000101",
                RazaoSocial = "Novo Anunciante Comercio Ltda",
                NomeFantasia = "Novo Anunciante",
                Cidade = "Sao Paulo",
                Uf = "SP",
                Contato = "Bruno",
                Email = "Contato@NovoAnunciante.com",
                Codigo = "HACK01"
            });

            resposta.StatusCode.Should().Be(HttpStatusCode.Created);
            var criado = await resposta.Content.ReadFromJsonAsync<CadastroDto>();
            criado!.Criado.Should().BeTrue();

            var enviado = JObject.Parse(factory.Core.Requisicoes.Single().Corpo);
            ((string)enviado["Nome"]).Should().Be("Novo Anunciante");
            ((int)enviado["IdAfiliada"]).Should().Be(a, "a afiliada vem do servidor");
            ((string)enviado["Codigo"]).Should().NotBe("HACK01", "código é gerado pelo servidor");

            var detalhe = await client.GetFromJsonAsync<AnuncianteDto>($"/api/wl/anunciantes/{criado.Id}");
            detalhe!.Email.Should().Be("contato@novoanunciante.com");
            detalhe.Contato.Should().Be("Bruno");
        }

        [Fact]
        public async Task Dados_comerciais_invalidos_sao_recusados_antes_de_qualquer_escrita()
        {
            const int a = 9508;
            var (factory, client) = await OperadorAsync(a, "anun-inval@exemplo.com");
            using var _ = factory;

            var emailRuim = await client.PostAsJsonAsync("/api/wl/anunciantes",
                new { Cnpj = "10508000000101", RazaoSocial = "Qualquer Coisa Ltda", Email = "nao-e-email" });
            var segmentoInexistente = await client.PostAsJsonAsync("/api/wl/anunciantes",
                new { Cnpj = "10508000000101", RazaoSocial = "Qualquer Coisa Ltda", SegmentoId = int.MaxValue });

            emailRuim.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            segmentoInexistente.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            factory.Core.Requisicoes.Should().BeEmpty();
        }

        [Fact]
        public async Task Inativar_muda_o_status_do_vinculo_e_nao_apaga_nada()
        {
            const int a = 9509;
            var cliente = await SeedCadastros.ClienteAsync("10509000000101", "AN0901", "Para Inativar");
            await SeedCadastros.VinculoClienteAsync(a, cliente);

            var (factory, client) = await OperadorAsync(a, "anun-inat@exemplo.com");
            using var _ = factory;

            (await client.PatchAsJsonAsync($"/api/wl/anunciantes/{cliente}/status", new { Ativo = false }))
                .EnsureSuccessStatusCode();

            (await SeedCadastros.ContarAsync(
                    "SELECT COUNT(*) FROM AfiliadaCliente WHERE IdAfiliada = @p0 AND IdCliente = @p1 AND Status = 0", a, cliente))
                .Should().Be(1);
            (await SeedCadastros.ContarAsync("SELECT COUNT(*) FROM Cliente WHERE Id = @p0 AND StatusExibicao = 1", cliente))
                .Should().Be(1, "o Cliente não é tocado");

            var inativos = await client.GetFromJsonAsync<PaginaDto<AnuncianteDto>>("/api/wl/anunciantes?status=Inativo");
            inativos!.Itens.Should().ContainSingle(i => i.Id == cliente);

            (await client.PatchAsJsonAsync($"/api/wl/anunciantes/{cliente}/status", new { Ativo = true }))
                .EnsureSuccessStatusCode();
            (await SeedCadastros.ContarAsync(
                    "SELECT COUNT(*) FROM AfiliadaCliente WHERE IdAfiliada = @p0 AND IdCliente = @p1 AND Status = 1", a, cliente))
                .Should().Be(1, "inativação é reversível");
        }

        [Fact]
        public async Task Busca_filtros_ordenacao_e_paginacao_sao_do_servidor()
        {
            const int a = 9510;
            var varejo = await SeedCadastros.SegmentoAsync("Seg 10510 Varejo");
            var alfa = await SeedCadastros.ClienteAsync("10510000000101", "AN1001", "Alfa Bebidas");
            var beta = await SeedCadastros.ClienteAsync("10510000000202", "AN1002", "Beta Moveis");
            var gama = await SeedCadastros.ClienteAsync("10510000000303", "AN1003", "Gama Bebidas");
            await SeedCadastros.VinculoClienteAsync(a, alfa, segmentoId: varejo);
            await SeedCadastros.VinculoClienteAsync(a, beta);
            await SeedCadastros.VinculoClienteAsync(a, gama, ativo: false, segmentoId: varejo);

            var (factory, client) = await OperadorAsync(a, "anun-filtro@exemplo.com");
            using var _ = factory;

            var p1 = await client.GetFromJsonAsync<PaginaDto<AnuncianteDto>>("/api/wl/anunciantes?pageSize=2&page=1");
            var p2 = await client.GetFromJsonAsync<PaginaDto<AnuncianteDto>>("/api/wl/anunciantes?pageSize=2&page=2");
            p1!.Total.Should().Be(3);
            p1.Itens.Select(i => i.Nome).Should().Equal("Alfa Bebidas", "Beta Moveis");
            p2!.Itens.Select(i => i.Nome).Should().Equal("Gama Bebidas");

            var bebidas = await client.GetFromJsonAsync<PaginaDto<AnuncianteDto>>("/api/wl/anunciantes?busca=bebidas");
            bebidas!.Itens.Select(i => i.Id).Should().BeEquivalentTo(new[] { alfa, gama });

            var porCnpj = await client.GetFromJsonAsync<PaginaDto<AnuncianteDto>>("/api/wl/anunciantes?busca=10.510.000/0002");
            porCnpj!.Itens.Select(i => i.Id).Should().Equal(beta);

            var ativosDoVarejo = await client.GetFromJsonAsync<PaginaDto<AnuncianteDto>>(
                $"/api/wl/anunciantes?status=Ativo&segmentoId={varejo}");
            ativosDoVarejo!.Itens.Select(i => i.Id).Should().Equal(alfa);

            var desc = await client.GetFromJsonAsync<PaginaDto<AnuncianteDto>>("/api/wl/anunciantes?sort=nome&desc=true");
            desc!.Itens.First().Nome.Should().Be("Gama Bebidas");

            p1.Itens.Should().OnlyContain(i => i.AgenciasVinculadas != null, "lista vazia = venda direta, nunca null");
        }

        [Fact]
        public async Task Investimento_historico_soma_os_PIs_do_cliente_nesta_afiliada_sem_cancelados()
        {
            const int a = 9511, b = 9512;
            var localId = await Seed.LocalAsync(a, "L10511");
            var pecaId = await Seed.PecaAsync(localId, "P10511");
            await Seed.ReservaAsync(a, "R10511", pecaId);

            var cliente = await SeedCadastros.ClienteAsync("10511000000101", "AN1101", "Investidor");
            await SeedCadastros.VinculoClienteAsync(a, cliente);
            await Seed.AfiliadaAsync(b);
            await SeedCadastros.PiDoClienteAsync(a, cliente, "10511A", 1500.50m);
            await SeedCadastros.PiDoClienteAsync(a, cliente, "10511B", 2000m, status: 8);   // Veiculado
            await SeedCadastros.PiDoClienteAsync(a, cliente, "10511C", 9999m, status: -1);  // Cancelado
            await SeedCadastros.PiDoClienteAsync(a, cliente, "10511D", 7777m, status: -2);  // Rejeitado
            await SeedCadastros.PiDoClienteAsync(b, cliente, "10511E", 5000m);              // outra exibidora

            var (factory, client) = await OperadorAsync(a, "anun-invest@exemplo.com");
            using var _ = factory;

            var pagina = await client.GetFromJsonAsync<PaginaDto<AnuncianteDto>>("/api/wl/anunciantes");
            pagina!.Itens.Single(i => i.Id == cliente).InvestimentoHistorico.Should().Be(3500.50m);

            var detalhe = await client.GetFromJsonAsync<AnuncianteDto>($"/api/wl/anunciantes/{cliente}");
            detalhe!.InvestimentoHistorico.Should().Be(3500.50m);
        }

        [Fact]
        public async Task Historico_do_detalhe_nao_mostra_campanhas_com_outra_exibidora()
        {
            const int a = 9513, b = 9514;
            var localId = await Seed.LocalAsync(a, "L10513");
            var pecaId = await Seed.PecaAsync(localId, "P10513");
            await Seed.ReservaAsync(a, "R10513", pecaId);

            var cliente = await SeedCadastros.ClienteAsync("10513000000101", "AN1301", "Compartilhado");
            await SeedCadastros.VinculoClienteAsync(a, cliente);
            await SeedCadastros.VinculoClienteAsync(b, cliente);
            var campanhaA = await SeedCadastros.PiDoClienteAsync(a, cliente, "10513A", 100m);
            var campanhaB = await SeedCadastros.PiDoClienteAsync(b, cliente, "10513B", 100m);

            var (factory, client) = await OperadorAsync(a, "anun-hist@exemplo.com");
            using var _ = factory;

            var json = JObject.Parse(await client.GetStringAsync($"/api/wl/anunciantes/{cliente}"));
            var campanhas = json["historico"]!.Select(c => (int)c["id"]!).ToList();

            campanhas.Should().Contain(campanhaA).And.NotContain(campanhaB);
        }

        private sealed record CadastroDto(int Id, bool Criado, bool Vinculado);

        private sealed record RefDto(int Id, string Nome);

        private sealed class AnuncianteDto
        {
            public int Id { get; set; }
            public string Nome { get; set; } = "";
            public string RazaoSocial { get; set; } = "";
            public string Cnpj { get; set; } = "";
            public string? Email { get; set; }
            public string? Contato { get; set; }
            public RefDto? Segmento { get; set; }
            public int Status { get; set; }
            public RefDto[]? AgenciasVinculadas { get; set; }
            public decimal InvestimentoHistorico { get; set; }
        }
    }
}
