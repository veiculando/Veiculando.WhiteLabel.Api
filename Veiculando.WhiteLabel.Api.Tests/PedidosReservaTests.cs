using System.Net;
using System.Net.Http.Json;
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

        private async Task<(WlApiFactory Factory, System.Net.Http.HttpClient Client, int ReservaId)> PrepararAsync(
            string sufixo, int afiliadaId = Afiliada)
        {
            var email = $"reserva-{sufixo}@exemplo.com";
            await Seed.OperadorAsync(afiliadaId, email, new[] { "PedidoReservaGerenciar" });

            var localId = await Seed.LocalAsync(afiliadaId, $"L-{sufixo}");
            var pecaId = await Seed.PecaAsync(localId, $"P-{sufixo}");
            var (reservaId, _) = await Seed.ReservaAsync(afiliadaId, $"R-{sufixo}", pecaId);

            var factory = new WlApiFactory(_db, afiliadaId);
            var client = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);

            return (factory, client, reservaId);
        }

        [Fact]
        public async Task Aceitar_delega_ao_core_marcando_itens_como_reservados()
        {
            var (factory, client, reservaId) = await PrepararAsync("ace");
            using var _ = factory;
            using var __ = client;

            var resposta = await client.PostAsJsonAsync("/api/wl/pedidos-reserva/resposta",
                new { PedidoReservaId = reservaId, Aceitar = true });

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
            var (factory, client, reservaId) = await PrepararAsync("rej");
            using var _ = factory;
            using var __ = client;

            var resposta = await client.PostAsJsonAsync("/api/wl/pedidos-reserva/resposta",
                new { PedidoReservaId = reservaId, Aceitar = false });

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
                new { PedidoReservaId = reservaId, Aceitar = true });

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
                new { PedidoReservaId = reservaDeB, Aceitar = true });

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
                new { PedidoReservaId = reservaId, Aceitar = true });

            resposta.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        [Fact]
        public async Task Erro_do_core_e_repassado_e_nao_vira_500()
        {
            var (factory, client, reservaId) = await PrepararAsync("erro");
            using var _ = factory;
            using var __ = client;

            factory.Core.Responder = _ => new System.Net.Http.HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new System.Net.Http.StringContent(
                    "[{\"property\":\"PedidoReserva\",\"message\":\"Este pedido nao esta mais disponivel\"}]",
                    System.Text.Encoding.UTF8, "application/json")
            };

            var resposta = await client.PostAsJsonAsync("/api/wl/pedidos-reserva/resposta",
                new { PedidoReservaId = reservaId, Aceitar = true });

            resposta.StatusCode.Should().Be(HttpStatusCode.BadRequest,
                "as notificacoes do dominio precisam chegar a tela em vez de virar 500 generico");

            var corpo = await resposta.Content.ReadAsStringAsync();
            corpo.Should().Contain("nao esta mais disponivel");
        }

        [Fact]
        public async Task Detalhe_da_reserva_carrega_itens_e_nao_estoura_NRE()
        {
            var (factory, client, _) = await PrepararAsync("det");
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

        private sealed record MensagemDto(string Message);
        private sealed record ReservaDetalheDto(int Id, string Codigo, string Status, ItemDto[] Itens);
        private sealed record ItemDto(int Id, string PecaCodigo, string LocalCodigo, string Status);
    }
}
