using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FluentAssertions;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests
{
    /// <summary>
    /// Resposta a pedido de reserva — o defeito de maior severidade da revisao.
    /// </summary>
    /// <remarks>
    /// A implementacao anterior estava quebrada nos dois ramos:
    ///
    /// <para><b>Rejeitar nao fazia nada.</b> O corpo era
    /// <c>if (dto.Aceitar) { pedido.AtualizaStatus(); }</c> seguido de um
    /// <c>SaveChanges</c> sem alteracao — e respondia "Reserva rejeitada com
    /// sucesso". A reserva ficava Solicitado para sempre.</para>
    ///
    /// <para><b>Aceitar confirmava sem olhar item nenhum.</b>
    /// <c>AtualizaStatus()</c> decide a partir de <c>Itens</c>, nunca carregada
    /// (lazy loading desligado). Como o ctor protegido inicializa a lista vazia,
    /// <c>Itens.All(...)</c> era verdadeiro por vacuidade e o status virava
    /// Confirmado — e a grade <c>PecaPeriodoStatus</c> ficava intocada, deixando a
    /// peca livre para ser reservada de novo. Risco de overbooking.</para>
    ///
    /// <para>A correcao delega ao <c>PedidoReservaRespostaHandler</c> do core. Os
    /// testes abaixo verificam o CONTRATO dessa delegacao, que e o que cabe a um
    /// teste do BFF: o core nao roda aqui.</para>
    /// </remarks>
    [Collection(DatabaseCollection.Nome)]
    public class PedidosReservaTests
    {
        private const int Afiliada = 8500;
        private readonly SqlServerFixture _db;

        public PedidosReservaTests(SqlServerFixture db) => _db = db;

        private async Task<(WlApiFactory Factory, System.Net.Http.HttpClient Client, int ReservaId, string Codigo)> PrepararAsync(
            string sufixo, int afiliadaId = Afiliada)
        {
            var email = $"reserva-{sufixo}@exemplo.com";
            await Seed.OperadorAsync(afiliadaId, email, new[] { "PedidoReservaGerenciar" });

            var localId = await Seed.LocalAsync(afiliadaId, $"L-{sufixo}");
            var pecaId = await Seed.PecaAsync(localId, $"P-{sufixo}");
            var (reservaId, codigo) = await Seed.ReservaAsync(afiliadaId, $"R-{sufixo}", pecaId);

            var factory = new WlApiFactory(_db, afiliadaId);
            var client = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);

            return (factory, client, reservaId, codigo);
        }

        /// <summary>
        /// Ids dos itens do pedido, descobertos pelo endpoint de detalhe — que e
        /// exatamente como a tela os obtem antes de montar a resposta.
        /// </summary>
        private static async Task<int[]> ItensAsync(System.Net.Http.HttpClient client, string codigo)
        {
            var detalhe = await client.GetFromJsonAsync<ReservaDetalheDto>(
                $"/api/wl/pedidos-reserva/{codigo}");

            return detalhe!.Itens.Select(i => i.Id).ToArray();
        }

        /// <summary>Resposta aceitando (ou recusando) todos os itens do pedido.</summary>
        private static object RespostaUniforme(int reservaId, int[] itens, bool aceitar) => new
        {
            PedidoReservaId = reservaId,
            Itens = itens.Select(id => new { IdItemPedidoReserva = id, Aceitar = aceitar }).ToArray()
        };

        [Fact]
        public async Task Aceitar_delega_ao_core_marcando_itens_como_reservados()
        {
            var (factory, client, reservaId, codigo) = await PrepararAsync("ace");
            using var _ = factory;
            using var __ = client;

            var itens = await ItensAsync(client, codigo);

            var resposta = await client.PostAsJsonAsync("/api/wl/pedidos-reserva/resposta",
                RespostaUniforme(reservaId, itens, aceitar: true));

            resposta.StatusCode.Should().Be(HttpStatusCode.OK);

            var corpo = await resposta.Content.ReadFromJsonAsync<MensagemDto>();
            corpo!.Message.Should().Contain("confirmada",
                "a tela le `resposta.message` para o banner de confirmacao");

            factory.Core.Requisicoes.Should().ContainSingle(
                "responder reserva precisa ser delegado ao core, nao decidido aqui");

            var enviado = factory.Core.Requisicoes[0];
            enviado.Url.Should().EndWith("api/pedido-reserva/resposta");
            enviado.Corpo.Should().Contain("\"Disponibilidade\":1",
                "aceitar marca os itens como Reservado (1)");
            enviado.Corpo.Should().Contain("\"IdsPecaSugerida\":[]",
                "o handler do core acessa IdsPecaSugerida.Length sem checar nulidade");
            enviado.Corpo.Should().Contain("\"IdPeca\":",
                "o core precisa do IdPeca para achar a linha da grade de disponibilidade");
            enviado.Corpo.Should().Contain("\"IdPeriodo\":");
        }

        /// <summary>
        /// O ramo que literalmente nao fazia nada e respondia sucesso.
        /// </summary>
        [Fact]
        public async Task Rejeitar_delega_ao_core_marcando_itens_como_indisponiveis()
        {
            var (factory, client, reservaId, codigo) = await PrepararAsync("rej");
            using var _ = factory;
            using var __ = client;

            var itens = await ItensAsync(client, codigo);

            var resposta = await client.PostAsJsonAsync("/api/wl/pedidos-reserva/resposta",
                RespostaUniforme(reservaId, itens, aceitar: false));

            resposta.StatusCode.Should().Be(HttpStatusCode.OK);

            factory.Core.Requisicoes.Should().ContainSingle(
                "rejeitar nao pode ser um no-op que responde sucesso");

            factory.Core.Requisicoes[0].Corpo.Should().Contain("\"Disponibilidade\":2",
                "rejeitar marca os itens como Indisponivel (2)");
        }

        /// <summary>
        /// Guarda equivalente a do handler do core, aplicada antes da chamada
        /// remota. Sem ela, responder duas vezes o mesmo pedido era aceito.
        /// </summary>
        [Fact]
        public async Task Responder_pedido_ja_respondido_devolve_409()
        {
            var email = "reserva-dup@exemplo.com";
            await Seed.OperadorAsync(Afiliada, email, new[] { "PedidoReservaGerenciar" });

            var localId = await Seed.LocalAsync(Afiliada, "L-dup");
            var pecaId = await Seed.PecaAsync(localId, "P-dup");
            var (reservaId, _) = await Seed.ReservaAsync(
                Afiliada, "R-dup", pecaId, Seed.StatusPedidoReserva.Confirmado);

            using var factory = new WlApiFactory(_db, Afiliada);
            using var client = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);

            var resposta = await client.PostAsJsonAsync("/api/wl/pedidos-reserva/resposta",
                new { PedidoReservaId = reservaId, Itens = new[] { new { IdItemPedidoReserva = 1, Aceitar = true } } });

            resposta.StatusCode.Should().Be(HttpStatusCode.Conflict);
            factory.Core.Requisicoes.Should().BeEmpty("nao deve encaminhar ao core um pedido ja respondido");
        }

        [Fact]
        public async Task Responder_reserva_de_outra_afiliada_devolve_404()
        {
            const int outraAfiliada = 8600;

            var email = "reserva-iso@exemplo.com";
            await Seed.OperadorAsync(Afiliada, email, new[] { "PedidoReservaGerenciar" });

            var localDeB = await Seed.LocalAsync(outraAfiliada, "L-iso");
            var pecaDeB = await Seed.PecaAsync(localDeB, "P-iso");
            var (reservaDeB, _) = await Seed.ReservaAsync(outraAfiliada, "R-iso", pecaDeB);

            using var factory = new WlApiFactory(_db, Afiliada);
            using var client = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);

            var resposta = await client.PostAsJsonAsync("/api/wl/pedidos-reserva/resposta",
                new { PedidoReservaId = reservaDeB, Itens = new[] { new { IdItemPedidoReserva = 1, Aceitar = true } } });

            resposta.StatusCode.Should().Be(HttpStatusCode.NotFound);
            factory.Core.Requisicoes.Should().BeEmpty();
        }

        [Fact]
        public async Task Operador_sem_a_permissao_recebe_403()
        {
            var email = "reserva-semperm@exemplo.com";
            await Seed.OperadorAsync(Afiliada, email, new string[0]);

            var localId = await Seed.LocalAsync(Afiliada, "L-sp");
            var pecaId = await Seed.PecaAsync(localId, "P-sp");
            var (reservaId, _) = await Seed.ReservaAsync(Afiliada, "R-sp", pecaId);

            using var factory = new WlApiFactory(_db, Afiliada);
            using var client = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);

            var resposta = await client.PostAsJsonAsync("/api/wl/pedidos-reserva/resposta",
                new { PedidoReservaId = reservaId, Itens = new[] { new { IdItemPedidoReserva = 1, Aceitar = true } } });

            resposta.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        [Fact]
        public async Task Erro_do_core_e_repassado_e_nao_vira_500()
        {
            var (factory, client, reservaId, codigo) = await PrepararAsync("erro");
            using var _ = factory;
            using var __ = client;

            var itens = await ItensAsync(client, codigo);

            factory.Core.Responder = _ => new System.Net.Http.HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new System.Net.Http.StringContent(
                    "[{\"property\":\"PedidoReserva\",\"message\":\"Este pedido nao esta mais disponivel\"}]",
                    System.Text.Encoding.UTF8, "application/json")
            };

            var resposta = await client.PostAsJsonAsync("/api/wl/pedidos-reserva/resposta",
                RespostaUniforme(reservaId, itens, aceitar: true));

            resposta.StatusCode.Should().Be(HttpStatusCode.BadRequest,
                "as notificacoes do dominio precisam chegar a tela em vez de virar 500 generico");

            var corpo = await resposta.Content.ReadAsStringAsync();
            corpo.Should().Contain("nao esta mais disponivel");
        }

        [Fact]
        public async Task Detalhe_da_reserva_carrega_itens_e_nao_estoura_NRE()
        {
            var (factory, client, _, _) = await PrepararAsync("det");
            using var f = factory;
            using var c = client;

            // GetByCodigo acessa pr.Pedido.Campanha.Agencia — sem Include, `pr.Pedido`
            // vinha null e a projecao estourava NullReferenceException.
            var resposta = await client.GetAsync("/api/wl/pedidos-reserva/R-det");

            resposta.StatusCode.Should().Be(HttpStatusCode.OK);

            var detalhe = await resposta.Content.ReadFromJsonAsync<ReservaDetalheDto>();
            detalhe!.Codigo.Should().Be("R-det");
            detalhe.Itens.Should().NotBeEmpty("os itens vem do Include e nao podem chegar vazios");
        }

        // ------------------------------------------------------------------
        // Cenario 2 — resposta mista por item
        // ------------------------------------------------------------------

        /// <summary>
        /// Cada item recebe a SUA decisao, e cada um e enviado ao core uma vez so.
        /// </summary>
        /// <remarks>
        /// O contrato anterior era `{ pedidoReservaId, aceitar }` e aplicava a
        /// mesma decisao a todos. O handler do core sempre soube tratar item a
        /// item — o que faltava era o BFF expor isso.
        /// </remarks>
        [Fact]
        public async Task Resposta_mista_envia_a_decisao_de_cada_item_uma_unica_vez()
        {
            var email = "reserva-mista@exemplo.com";
            await Seed.OperadorAsync(Afiliada, email, new[] { "PedidoReservaGerenciar" });

            var localId = await Seed.LocalAsync(Afiliada, "L-mix");
            var peca1 = await Seed.PecaAsync(localId, "P-mix1");
            var peca2 = await Seed.PecaAsync(localId, "P-mix2");
            var (reservaId, codigo) = await Seed.ReservaAsync(Afiliada, "R-mix", peca1, pecaIdExtra: peca2);

            using var factory = new WlApiFactory(_db, Afiliada);
            using var client = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);

            var itens = await ItensAsync(client, codigo);
            itens.Should().HaveCount(2, "o cenario exige um pedido com varios itens");

            var resposta = await client.PostAsJsonAsync("/api/wl/pedidos-reserva/resposta", new
            {
                PedidoReservaId = reservaId,
                Itens = new object[]
                {
                    new { IdItemPedidoReserva = itens[0], Aceitar = true },
                    new { IdItemPedidoReserva = itens[1], Aceitar = false },
                }
            });

            resposta.StatusCode.Should().Be(HttpStatusCode.OK);

            var corpo = await resposta.Content.ReadFromJsonAsync<ResumoRespostaDto>();
            corpo!.Aceitos.Should().Be(1);
            corpo.Rejeitados.Should().Be(1);

            var enviado = factory.Core.Requisicoes.Should().ContainSingle().Subject;

            // Um Reservado (1) e um Indisponivel (2) no mesmo comando.
            enviado.Corpo.Should().Contain("\"Disponibilidade\":1");
            enviado.Corpo.Should().Contain("\"Disponibilidade\":2");

            // Cada item exatamente uma vez.
            foreach (var id in itens)
            {
                Regex.Matches(enviado.Corpo, $"\"IdItemPedidoReserva\":{id}\\b")
                    .Should().HaveCount(1, $"o item {id} nao pode ser enviado em duplicidade");
            }
        }

        /// <summary>Rejeicao pode carregar peca sugerida — o "motivo" que o dominio tem.</summary>
        [Fact]
        public async Task Rejeicao_com_peca_sugerida_do_proprio_tenant_e_encaminhada()
        {
            var (factory, client, reservaId, codigo) = await PrepararAsync("sug");
            using var _ = factory;
            using var __ = client;

            var localId = await Seed.LocalAsync(Afiliada, "L-sug2");
            var alternativa = await Seed.PecaAsync(localId, "P-sug2");

            var itens = await ItensAsync(client, codigo);

            var resposta = await client.PostAsJsonAsync("/api/wl/pedidos-reserva/resposta", new
            {
                PedidoReservaId = reservaId,
                Itens = new[]
                {
                    new { IdItemPedidoReserva = itens[0], Aceitar = false, IdsPecaSugerida = new[] { alternativa } }
                }
            });

            resposta.StatusCode.Should().Be(HttpStatusCode.OK);
            factory.Core.Requisicoes[0].Corpo.Should().Contain($"\"IdsPecaSugerida\":[{alternativa}]");
        }

        /// <summary>Sugestao numa ACEITACAO nao faz sentido e nao e propagada.</summary>
        [Fact]
        public async Task Sugestao_em_item_aceito_e_ignorada()
        {
            var (factory, client, reservaId, codigo) = await PrepararAsync("sugace");
            using var _ = factory;
            using var __ = client;

            var localId = await Seed.LocalAsync(Afiliada, "L-sa2");
            var alternativa = await Seed.PecaAsync(localId, "P-sa2");

            var itens = await ItensAsync(client, codigo);

            await client.PostAsJsonAsync("/api/wl/pedidos-reserva/resposta", new
            {
                PedidoReservaId = reservaId,
                Itens = new[]
                {
                    new { IdItemPedidoReserva = itens[0], Aceitar = true, IdsPecaSugerida = new[] { alternativa } }
                }
            });

            factory.Core.Requisicoes[0].Corpo.Should().Contain("\"IdsPecaSugerida\":[]");
        }

        // ------------------------------------------------------------------
        // Cenario 3 — payload invalido nao chega ao core
        // ------------------------------------------------------------------

        [Fact]
        public async Task Payload_sem_itens_e_recusado_sem_chamar_o_core()
        {
            var (factory, client, reservaId, _) = await PrepararAsync("vazio");
            using var _f = factory;
            using var _c = client;

            var resposta = await client.PostAsJsonAsync("/api/wl/pedidos-reserva/resposta",
                new { PedidoReservaId = reservaId, Itens = new object[0] });

            resposta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            factory.Core.Requisicoes.Should().BeEmpty();
        }

        /// <summary>
        /// Omitir um item o deixaria pendente para sempre com o pedido ja
        /// respondido — o handler do core so itera o que recebe.
        /// </summary>
        [Fact]
        public async Task Item_omitido_e_recusado_sem_chamar_o_core()
        {
            var email = "reserva-omit@exemplo.com";
            await Seed.OperadorAsync(Afiliada, email, new[] { "PedidoReservaGerenciar" });

            var localId = await Seed.LocalAsync(Afiliada, "L-omit");
            var peca1 = await Seed.PecaAsync(localId, "P-omit1");
            var peca2 = await Seed.PecaAsync(localId, "P-omit2");
            var (reservaId, codigo) = await Seed.ReservaAsync(Afiliada, "R-omit", peca1, pecaIdExtra: peca2);

            using var factory = new WlApiFactory(_db, Afiliada);
            using var client = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);

            var itens = await ItensAsync(client, codigo);

            var resposta = await client.PostAsJsonAsync("/api/wl/pedidos-reserva/resposta", new
            {
                PedidoReservaId = reservaId,
                Itens = new[] { new { IdItemPedidoReserva = itens[0], Aceitar = true } }
            });

            resposta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await resposta.Content.ReadAsStringAsync()).Should().Contain("decis");
            factory.Core.Requisicoes.Should().BeEmpty();
        }

        [Fact]
        public async Task Item_duplicado_e_recusado_sem_chamar_o_core()
        {
            var (factory, client, reservaId, codigo) = await PrepararAsync("dupitem");
            using var _ = factory;
            using var __ = client;

            var itens = await ItensAsync(client, codigo);

            var resposta = await client.PostAsJsonAsync("/api/wl/pedidos-reserva/resposta", new
            {
                PedidoReservaId = reservaId,
                Itens = new[]
                {
                    new { IdItemPedidoReserva = itens[0], Aceitar = true },
                    new { IdItemPedidoReserva = itens[0], Aceitar = false },
                }
            });

            resposta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await resposta.Content.ReadAsStringAsync()).Should().Contain("uma única vez");
            factory.Core.Requisicoes.Should().BeEmpty();
        }

        [Fact]
        public async Task Item_desconhecido_e_recusado_sem_chamar_o_core()
        {
            var (factory, client, reservaId, codigo) = await PrepararAsync("desc");
            using var _ = factory;
            using var __ = client;

            var itens = await ItensAsync(client, codigo);

            var resposta = await client.PostAsJsonAsync("/api/wl/pedidos-reserva/resposta", new
            {
                PedidoReservaId = reservaId,
                Itens = new[]
                {
                    new { IdItemPedidoReserva = itens[0], Aceitar = true },
                    new { IdItemPedidoReserva = 999_999, Aceitar = true },
                }
            });

            resposta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            factory.Core.Requisicoes.Should().BeEmpty();
        }

        // ------------------------------------------------------------------
        // Cenario 4 — peca sugerida de outro tenant
        // ------------------------------------------------------------------

        /// <summary>
        /// O handler do core tem a checagem de afiliada COMENTADA (`//TODO :
        /// BLOQUEAR outra exibidora`) e nao valida a peca sugerida. Se a chamada
        /// sair daqui, a peca de outra exibidora e gravada em IdPecaRecomendada.
        /// A barreira e esta.
        /// </summary>
        [Fact]
        public async Task Peca_sugerida_de_outro_tenant_e_recusada_sem_chamar_o_core()
        {
            const int outraAfiliada = 8610;

            var (factory, client, reservaId, codigo) = await PrepararAsync("sugalheia");
            using var _ = factory;
            using var __ = client;

            var localDeB = await Seed.LocalAsync(outraAfiliada, "L-sgb");
            var pecaDeB = await Seed.PecaAsync(localDeB, "P-sgb");

            var itens = await ItensAsync(client, codigo);

            var resposta = await client.PostAsJsonAsync("/api/wl/pedidos-reserva/resposta", new
            {
                PedidoReservaId = reservaId,
                Itens = new[]
                {
                    new { IdItemPedidoReserva = itens[0], Aceitar = false, IdsPecaSugerida = new[] { pecaDeB } }
                }
            });

            resposta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            factory.Core.Requisicoes.Should().BeEmpty(
                "a peca sugerida de outra exibidora nao pode chegar ao core");
        }

        // ------------------------------------------------------------------
        // Cenario 5 — o servidor deriva peca e periodo
        // ------------------------------------------------------------------

        /// <summary>
        /// O cliente decide O QUE responder; o servidor decide SOBRE O QUE.
        /// IdPeca/IdPeriodo saem do pedido carregado, nunca do corpo — aceita-los
        /// do cliente permitiria mover a resposta para outra peca.
        /// </summary>
        [Fact]
        public async Task IdPeca_e_IdPeriodo_do_cliente_sao_ignorados()
        {
            var (factory, client, reservaId, codigo) = await PrepararAsync("deriva");
            using var _ = factory;
            using var __ = client;

            var itens = await ItensAsync(client, codigo);

            await client.PostAsJsonAsync("/api/wl/pedidos-reserva/resposta", new
            {
                PedidoReservaId = reservaId,
                Itens = new[]
                {
                    new
                    {
                        IdItemPedidoReserva = itens[0],
                        Aceitar = true,
                        IdPeca = 987_654,
                        IdPeriodo = 987_654,
                    }
                }
            });

            var enviado = factory.Core.Requisicoes.Should().ContainSingle().Subject;
            enviado.Corpo.Should().NotContain("987654",
                "os ids vindos do cliente nao podem alcancar o comando do core");
        }

        // ------------------------------------------------------------------
        // Cenario 9 — paginacao estavel
        // ------------------------------------------------------------------

        [Fact]
        public async Task Listagem_e_paginada_com_teto_e_desempate_por_id()
        {
            const int afiliadaPag = 8620;

            var email = "reserva-pag@exemplo.com";
            await Seed.OperadorAsync(afiliadaPag, email, new[] { "PedidoReservaGerenciar" });

            var localId = await Seed.LocalAsync(afiliadaPag, "L-pag");

            // Todas com a mesma DataCadastro: sem desempate por id a ordem entre
            // elas nao e definida e as paginas podem repetir ou pular registros.
            for (var i = 0; i < 5; i++)
            {
                var peca = await Seed.PecaAsync(localId, $"P-pag{i}");
                await Seed.ReservaAsync(afiliadaPag, $"R-pag{i}", peca);
            }

            using var factory = new WlApiFactory(_db, afiliadaPag);
            using var client = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);

            var p1 = await client.GetFromJsonAsync<PaginaDto<ReservaListDto>>(
                "/api/wl/pedidos-reserva?page=1&pageSize=2");
            var p2 = await client.GetFromJsonAsync<PaginaDto<ReservaListDto>>(
                "/api/wl/pedidos-reserva?page=2&pageSize=2");
            var p3 = await client.GetFromJsonAsync<PaginaDto<ReservaListDto>>(
                "/api/wl/pedidos-reserva?page=3&pageSize=2");

            p1!.Total.Should().Be(5);
            p1.Itens.Should().HaveCount(2);
            p2!.Itens.Should().HaveCount(2);
            p3!.Itens.Should().HaveCount(1);

            var vistos = p1.Itens.Concat(p2.Itens).Concat(p3.Itens).Select(r => r.Id).ToList();
            vistos.Should().OnlyHaveUniqueItems("nenhum pedido pode aparecer em duas paginas");
            vistos.Should().HaveCount(5, "nenhum pedido pode ser pulado entre as paginas");
        }

        [Fact]
        public async Task PageSize_acima_do_teto_e_limitado_pelo_servidor()
        {
            var (factory, client, _, _) = await PrepararAsync("teto");
            using var _f = factory;
            using var _c = client;

            var pagina = await client.GetFromJsonAsync<PaginaDto<ReservaListDto>>(
                "/api/wl/pedidos-reserva?pageSize=100000");

            pagina!.PageSize.Should().Be(100, "o teto e do servidor, nao uma sugestao ao cliente");
        }

        [Fact]
        public async Task Sort_fora_da_whitelist_cai_no_padrao_em_vez_de_erro()
        {
            var (factory, client, _, _) = await PrepararAsync("sortinv");
            using var _f = factory;
            using var _c = client;

            var resposta = await client.GetAsync(
                "/api/wl/pedidos-reserva?sort=; DROP TABLE PedidoReserva--");

            resposta.StatusCode.Should().Be(HttpStatusCode.OK,
                "um sort desconhecido cai no padrao; nao vira erro nem alcanca a query");
        }

        private sealed record MensagemDto(string Message);
        private sealed record ResumoRespostaDto(string Message, int Aceitos, int Rejeitados);
        private sealed record ReservaListDto(int Id, string Codigo, string Status);
        private sealed record ReservaDetalheDto(int Id, string Codigo, string Status, ItemDto[] Itens);
        private sealed record ItemDto(int Id, string PecaCodigo, string LocalCodigo, string Status);
    }
}
