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
    /// Tipos de Suporte e Formatos (Sprint 10.5 BE-2, test plan cec4eea1). Os
    /// cinco testes obrigatórios do plano, mais as regras de reaproveitamento e
    /// edição de formato compartilhado.
    /// </summary>
    [Collection(DatabaseCollection.Nome)]
    public class TiposSuporteTests
    {
        private readonly SqlServerFixture _db;

        public TiposSuporteTests(SqlServerFixture db) => _db = db;

        private static readonly string[] Permissao = { "PecaGerenciar" };

        private async Task<(WlApiFactory Factory, HttpClient Client)> OperadorAsync(int afiliada, string email)
        {
            await Seed.OperadorAsync(afiliada, email, Permissao);
            var factory = new WlApiFactory(_db, afiliada);
            var client = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);
            return (factory, client);
        }

        /// <summary>
        /// Simula o FormatoHandler do core: grava no catálogo global o formato
        /// recebido, como o handler real faria.
        /// </summary>
        private static void CoreGravaFormatos(WlApiFactory factory)
        {
            factory.Core.Responder = req =>
            {
                if (req.Url.EndsWith("api/formato"))
                {
                    var c = JObject.Parse(req.Corpo);
                    if ((int)c["Id"]! == 0)
                        SeedCadastros.FormatoAsync((int)c["MidiaTipo"]!, (decimal)c["Largura"]!, (decimal)c["Altura"]!,
                            (short)c["ResolucaoLargura"]!, (short)c["ResolucaoAltura"]!, (short)c["Duracao"]!)
                            .GetAwaiter().GetResult();
                    else
                        SeedCadastros.ExecutarAsync("UPDATE Formato SET Largura = @p0, Altura = @p1 WHERE Id = @p2",
                            (decimal)c["Largura"]!, (decimal)c["Altura"]!, (int)c["Id"]!).GetAwaiter().GetResult();
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"sucesso\":true}", System.Text.Encoding.UTF8, "application/json")
                };
            };
        }

        [Fact]
        public async Task Nao_existe_rota_para_criar_nem_editar_o_TipoSuporte_global()
        {
            const int a = 9520;
            var tipo = await SeedCadastros.TipoSuporteAsync("TS10520", "Catalogo 10520");
            var (factory, client) = await OperadorAsync(a, "ts-global@exemplo.com");
            using var _ = factory;

            var criar = await client.PostAsJsonAsync("/api/wl/tipos-suporte", new { Nome = "Novo", Codigo = "NOVO" });
            var editar = await client.PutAsJsonAsync($"/api/wl/tipos-suporte/{tipo}", new { Nome = "Renomeado" });
            var editarStatusGlobal = await client.PatchAsJsonAsync($"/api/wl/tipos-suporte/{tipo}/status", new { Ativo = false });

            criar.IsSuccessStatusCode.Should().BeFalse();
            editar.IsSuccessStatusCode.Should().BeFalse();
            editarStatusGlobal.StatusCode.Should().Be(HttpStatusCode.NotFound,
                "o PATCH mexe só na habilitação — sem habilitação aqui, não há o que mudar");

            (await SeedCadastros.ContarAsync(
                    "SELECT COUNT(*) FROM TipoSuporte WHERE Id = @p0 AND Nome = 'Catalogo 10520' AND StatusExibicao = 1", tipo))
                .Should().Be(1);
            (await SeedCadastros.ContarAsync("SELECT COUNT(*) FROM TipoSuporte WHERE Codigo = 'NOVO'")).Should().Be(0);
            factory.Core.Requisicoes.Should().BeEmpty();
        }

        [Fact]
        public async Task Habilitar_tipo_ja_habilitado_e_idempotente()
        {
            const int a = 9521;
            var tipo = await SeedCadastros.TipoSuporteAsync("TS10521", "Idempotente");
            var (factory, client) = await OperadorAsync(a, "ts-idem@exemplo.com");
            using var _ = factory;

            var primeira = await client.PostAsync($"/api/wl/tipos-suporte/{tipo}/habilitar", null);
            var segunda = await client.PostAsync($"/api/wl/tipos-suporte/{tipo}/habilitar", null);

            primeira.StatusCode.Should().Be(HttpStatusCode.Created);
            segunda.StatusCode.Should().Be(HttpStatusCode.OK);
            (await SeedCadastros.ContarAsync(
                    "SELECT COUNT(*) FROM AfiliadaTipoSuporte WHERE IdAfiliada = @p0 AND IdTipoSuporte = @p1", a, tipo))
                .Should().Be(1);

            // Inativa e habilita de novo: reativa a MESMA linha.
            (await client.PatchAsJsonAsync($"/api/wl/tipos-suporte/{tipo}/status", new { Ativo = false })).EnsureSuccessStatusCode();
            (await client.PostAsync($"/api/wl/tipos-suporte/{tipo}/habilitar", null)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await SeedCadastros.ContarAsync(
                    "SELECT COUNT(*) FROM AfiliadaTipoSuporte WHERE IdAfiliada = @p0 AND IdTipoSuporte = @p1 AND Status = 1", a, tipo))
                .Should().Be(1);
        }

        [Fact]
        public async Task Tipo_inativo_no_catalogo_nao_pode_ser_habilitado_nem_aparece()
        {
            const int a = 9522;
            var inativo = await SeedCadastros.TipoSuporteAsync("TS10522", "Descontinuado", ativoNoCatalogo: false);
            var (factory, client) = await OperadorAsync(a, "ts-inat-cat@exemplo.com");
            using var _ = factory;

            (await client.PostAsync($"/api/wl/tipos-suporte/{inativo}/habilitar", null))
                .StatusCode.Should().Be(HttpStatusCode.NotFound);

            var disponiveis = await client.GetFromJsonAsync<TipoDto[]>("/api/wl/tipos-suporte/disponiveis");
            disponiveis!.Should().NotContain(t => t.Id == inativo);
        }

        [Fact]
        public async Task Formato_inativo_nao_entra_em_peca_nova_mas_continua_na_peca_existente()
        {
            const int a = 9523;
            var tipo = await SeedCadastros.TipoSuporteAsync("TS10523", "Outdoor 10523");
            var habilitacao = await SeedCadastros.HabilitacaoAsync(a, tipo);
            var ativo = await SeedCadastros.FormatoAsync(1, 10.23m, 3m);
            var inativo = await SeedCadastros.FormatoAsync(1, 10.23m, 4m);
            await SeedCadastros.AssociarFormatoAsync(habilitacao, ativo);
            await SeedCadastros.AssociarFormatoAsync(habilitacao, inativo, ativo: false);

            var localId = await Seed.LocalAsync(a, "L10523");
            var pecaExistente = await Seed.PecaAsync(localId, "P10523");
            await SeedCadastros.PecaComTipoEFormatoAsync(pecaExistente, tipo, inativo);

            var (factory, client) = await OperadorAsync(a, "ts-peca@exemplo.com");
            using var _ = factory;

            // Lista que alimenta o cadastro de peça.
            var paraCadastro = await client.GetFromJsonAsync<FormatoDto[]>($"/api/wl/tipos-suporte/{tipo}/formatos?status=Ativo");
            paraCadastro!.Select(f => f.Id).Should().Equal(ativo);

            // A lista completa ainda mostra o inativo (histórico).
            var todos = await client.GetFromJsonAsync<FormatoDto[]>($"/api/wl/tipos-suporte/{tipo}/formatos");
            todos!.Should().Contain(f => f.Id == inativo && f.Status == 0);

            // Peça nova com o formato inativo: recusada pelo BFF, nada chega ao core.
            var nova = await client.PostAsJsonAsync("/api/wl/pecas",
                new { IdLocal = localId, IdTipoSuporte = tipo, IdFormato = inativo, CodigoInterno = "NOVA" });
            nova.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            factory.Core.Requisicoes.Should().BeEmpty();

            // Peça existente mantendo o formato inativo: editável.
            var edicao = await client.PutAsJsonAsync($"/api/wl/pecas/{pecaExistente}",
                new { IdLocal = localId, IdTipoSuporte = tipo, IdFormato = inativo, CodigoInterno = "INT-P10523" });
            edicao.IsSuccessStatusCode.Should().BeTrue(await edicao.Content.ReadAsStringAsync());
            factory.Core.Requisicoes.Should().ContainSingle(r => r.Url.EndsWith("api/peca"));

            // Peça nova com o formato ativo: segue para o core.
            (await client.PostAsJsonAsync("/api/wl/pecas",
                    new { IdLocal = localId, IdTipoSuporte = tipo, IdFormato = ativo, CodigoInterno = "NOVA2" }))
                .IsSuccessStatusCode.Should().BeTrue();

            (await SeedCadastros.ContarAsync(
                    "SELECT COUNT(*) FROM Peca WHERE Id = @p0 AND IdFormatoArteFinal = @p1", pecaExistente, inativo))
                .Should().Be(1, "a peça existente não perde o formato");
        }

        [Fact]
        public async Task Midia_digital_exige_resolucao_e_duracao_e_estatica_exige_largura_e_altura()
        {
            const int a = 9524;
            var tipo = await SeedCadastros.TipoSuporteAsync("TS10524", "LED 10524");
            await SeedCadastros.HabilitacaoAsync(a, tipo);
            var (factory, client) = await OperadorAsync(a, "ts-midia@exemplo.com");
            using var _ = factory;
            CoreGravaFormatos(factory);

            var url = $"/api/wl/tipos-suporte/{tipo}/formatos";

            var digitalSemResolucaoVertical = await client.PostAsJsonAsync(url,
                new { MidiaTipo = 2, Largura = 3m, Altura = 2m, ResolucaoLargura = 1920, Duracao = 10 });
            var digitalSemDuracao = await client.PostAsJsonAsync(url,
                new { MidiaTipo = 2, Largura = 3m, Altura = 2m, ResolucaoLargura = 1920, ResolucaoAltura = 1080 });
            var estaticaSemLargura = await client.PostAsJsonAsync(url,
                new { MidiaTipo = 1, Altura = 3m });

            digitalSemResolucaoVertical.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            digitalSemDuracao.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            estaticaSemLargura.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            factory.Core.Requisicoes.Should().BeEmpty("validação acontece antes de chamar o core");

            var digital = await client.PostAsJsonAsync(url, new
            {
                MidiaTipo = 2, Largura = 10.24m, Altura = 2m, ResolucaoLargura = 1920, ResolucaoAltura = 1080, Duracao = 15,
                ExtensoesAceitas = new[] { ".MP4", " .jpg " }
            });
            digital.StatusCode.Should().Be(HttpStatusCode.Created, await digital.Content.ReadAsStringAsync());
            var enviado = JObject.Parse(factory.Core.Requisicoes.Single().Corpo);
            enviado["ExtensoesAceitas"]!.Select(e => (string)e!).Should().Equal(".mp4", ".jpg");

            // Estática com resolução "perdida" no payload: zerada, para a dedup do core
            // tratar "9 x 3 m" como um formato só.
            var estatica = await client.PostAsJsonAsync(url,
                new { MidiaTipo = 1, Largura = 10.24m, Altura = 3m, ResolucaoLargura = 800, Duracao = 5 });
            estatica.StatusCode.Should().Be(HttpStatusCode.Created);
            (await SeedCadastros.ContarAsync(
                    "SELECT COUNT(*) FROM Formato WHERE MidiaTipo = 1 AND Largura = 10.24 AND Altura = 3 AND ResolucaoLargura = 0 AND Duracao = 0"))
                .Should().Be(1);
        }

        [Fact]
        public async Task Formato_com_dimensao_existente_e_reaproveitado_sem_duplicar_o_catalogo()
        {
            const int a = 9525;
            var tipo = await SeedCadastros.TipoSuporteAsync("TS10525", "Outdoor 10525");
            await SeedCadastros.HabilitacaoAsync(a, tipo);
            var existente = await SeedCadastros.FormatoAsync(1, 10.25m, 3m);

            var (factory, client) = await OperadorAsync(a, "ts-reuso@exemplo.com");
            using var _ = factory;

            var resposta = await client.PostAsJsonAsync($"/api/wl/tipos-suporte/{tipo}/formatos",
                new { MidiaTipo = 1, Largura = 10.25m, Altura = 3m });

            resposta.StatusCode.Should().Be(HttpStatusCode.OK);
            var corpo = JObject.Parse(await resposta.Content.ReadAsStringAsync());
            ((bool)corpo["reaproveitado"]!).Should().BeTrue();
            ((int)corpo["formato"]!["id"]!).Should().Be(existente);
            factory.Core.Requisicoes.Should().BeEmpty();
            (await SeedCadastros.ContarAsync("SELECT COUNT(*) FROM Formato WHERE Largura = 10.25 AND Altura = 3")).Should().Be(1);

            (await client.PostAsJsonAsync($"/api/wl/tipos-suporte/{tipo}/formatos",
                    new { MidiaTipo = 1, Largura = 10.25m, Altura = 3m }))
                .StatusCode.Should().Be(HttpStatusCode.Conflict, "já associado a este tipo");
        }

        [Fact]
        public async Task Editar_formato_em_uso_por_peca_retorna_409_e_formato_exclusivo_edita()
        {
            const int a = 9526;
            var tipo = await SeedCadastros.TipoSuporteAsync("TS10526", "Outdoor 10526");
            var habilitacao = await SeedCadastros.HabilitacaoAsync(a, tipo);
            var emUso = await SeedCadastros.FormatoAsync(1, 10.26m, 3m);
            var exclusivo = await SeedCadastros.FormatoAsync(1, 10.26m, 4m);
            await SeedCadastros.AssociarFormatoAsync(habilitacao, emUso);
            await SeedCadastros.AssociarFormatoAsync(habilitacao, exclusivo);
            var localId = await Seed.LocalAsync(a, "L10526");
            var peca = await Seed.PecaAsync(localId, "P10526");
            await SeedCadastros.PecaComTipoEFormatoAsync(peca, tipo, emUso);

            var (factory, client) = await OperadorAsync(a, "ts-edit@exemplo.com");
            using var _ = factory;
            CoreGravaFormatos(factory);

            (await client.PutAsJsonAsync($"/api/wl/tipos-suporte/{tipo}/formatos/{emUso}",
                    new { MidiaTipo = 1, Largura = 10.26m, Altura = 5m }))
                .StatusCode.Should().Be(HttpStatusCode.Conflict);
            factory.Core.Requisicoes.Should().BeEmpty();

            var edicao = await client.PutAsJsonAsync($"/api/wl/tipos-suporte/{tipo}/formatos/{exclusivo}",
                new { MidiaTipo = 1, Largura = 10.26m, Altura = 6m });
            edicao.StatusCode.Should().Be(HttpStatusCode.OK, await edicao.Content.ReadAsStringAsync());
            ((int)JObject.Parse(factory.Core.Requisicoes.Single().Corpo)["Id"]!).Should().Be(exclusivo);
        }

        [Fact]
        public async Task Isolamento_por_afiliada()
        {
            const int a = 9527, b = 9528;
            var tipo = await SeedCadastros.TipoSuporteAsync("TS10527", "So de B");
            var habilitacaoB = await SeedCadastros.HabilitacaoAsync(b, tipo);
            var formatoB = await SeedCadastros.FormatoAsync(1, 10.27m, 3m);
            await SeedCadastros.AssociarFormatoAsync(habilitacaoB, formatoB);

            var (factory, client) = await OperadorAsync(a, "ts-iso@exemplo.com");
            using var _ = factory;

            var habilitados = await client.GetFromJsonAsync<TipoDto[]>("/api/wl/tipos-suporte/habilitados");
            habilitados!.Should().NotContain(t => t.Id == tipo);

            var disponiveis = await client.GetFromJsonAsync<TipoDto[]>("/api/wl/tipos-suporte/disponiveis");
            disponiveis!.Should().Contain(t => t.Id == tipo, "habilitado em B continua disponível para A");

            (await client.GetAsync($"/api/wl/tipos-suporte/{tipo}/formatos")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await client.PatchAsJsonAsync($"/api/wl/tipos-suporte/{tipo}/status", new { Ativo = false }))
                .StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await client.PatchAsJsonAsync($"/api/wl/tipos-suporte/{tipo}/formatos/{formatoB}/status", new { Ativo = false }))
                .StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await client.PutAsJsonAsync($"/api/wl/tipos-suporte/{tipo}/formatos/{formatoB}",
                    new { MidiaTipo = 1, Largura = 1m, Altura = 1m }))
                .StatusCode.Should().Be(HttpStatusCode.NotFound);

            (await SeedCadastros.ContarAsync(
                    "SELECT COUNT(*) FROM AfiliadaTipoSuporteFormato WHERE IdAfiliadaTipoSuporte = @p0 AND Status = 1", habilitacaoB))
                .Should().Be(1, "nada de B foi alterado");
        }

        [Fact]
        public async Task Habilitados_traz_contagens_do_servidor_e_some_com_tipo_desativado_no_catalogo()
        {
            const int a = 9529;
            var tipo = await SeedCadastros.TipoSuporteAsync("TS10529", "Contagens");
            var desativado = await SeedCadastros.TipoSuporteAsync("TS10529X", "Desativado depois");
            var habilitacao = await SeedCadastros.HabilitacaoAsync(a, tipo);
            await SeedCadastros.HabilitacaoAsync(a, desativado);
            await SeedCadastros.ExecutarAsync("UPDATE TipoSuporte SET StatusExibicao = 0 WHERE Id = @p0", desativado);
            await SeedCadastros.AssociarFormatoAsync(habilitacao, await SeedCadastros.FormatoAsync(1, 10.29m, 3m));
            await SeedCadastros.AssociarFormatoAsync(habilitacao, await SeedCadastros.FormatoAsync(1, 10.29m, 4m), ativo: false);

            var local1 = await Seed.LocalAsync(a, "L10529A");
            var local2 = await Seed.LocalAsync(a, "L10529B");
            var p1 = await Seed.PecaAsync(local1, "P10529A");
            var p2 = await Seed.PecaAsync(local1, "P10529B");
            var p3 = await Seed.PecaAsync(local2, "P10529C");
            foreach (var p in new[] { p1, p2, p3 })
                await SeedCadastros.PecaComTipoEFormatoAsync(p, tipo, null);

            var (factory, client) = await OperadorAsync(a, "ts-cont@exemplo.com");
            using var _ = factory;

            var habilitados = await client.GetFromJsonAsync<TipoDto[]>("/api/wl/tipos-suporte/habilitados");

            habilitados!.Should().NotContain(t => t.Id == desativado);
            var item = habilitados!.Single(t => t.Id == tipo);
            item.QtdFormatos.Should().Be(2);
            item.QtdFormatosAtivos.Should().Be(1);
            item.QtdLocaisEmOperacao.Should().Be(2);
            item.Categoria.Should().BeNull("decisão pendente do PO — nunca valor inventado");
            item.Descricao.Should().BeNull();
        }

        private sealed class TipoDto
        {
            public int Id { get; set; }
            public string Nome { get; set; } = "";
            public string Codigo { get; set; } = "";
            public string? Categoria { get; set; }
            public string? Descricao { get; set; }
            public int Status { get; set; }
            public int QtdFormatos { get; set; }
            public int QtdFormatosAtivos { get; set; }
            public int QtdLocaisEmOperacao { get; set; }
        }

        private sealed class FormatoDto
        {
            public int Id { get; set; }
            public int MidiaTipo { get; set; }
            public int Status { get; set; }
        }
    }
}
