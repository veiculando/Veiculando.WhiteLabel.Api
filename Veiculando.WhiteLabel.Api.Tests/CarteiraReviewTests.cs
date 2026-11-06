using System;
using System.Data.Entity;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities.WhiteLabel;
using Veiculando.Domain.Enums;
using Veiculando.WhiteLabel.Api.Services;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;
using static Veiculando.WhiteLabel.Api.Tests.ProspeccaoCarteiraTests;

namespace Veiculando.WhiteLabel.Api.Tests
{
    /// <summary>
    /// VEI-RD-157 (TP2b): análise da carteira no painel, troca de responsável e relatório de
    /// pedidos por vendedora. Quem envia não analisa (P2); a aprovação ativa vínculo e
    /// contrato de venda direta com comissão 0 (P3, I4); o pedido fica com quem vendeu (I2).
    /// </summary>
    [Collection(DatabaseCollection.Nome)]
    public class CarteiraReviewTests
    {
        private readonly SqlServerFixture _db;

        public CarteiraReviewTests(SqlServerFixture db) => _db = db;

        private sealed record Resgatada(Guid QuoteId);

        private static async Task<HttpClient> GestorAsync(WlApiFactory factory, int tenant)
        {
            var email = $"gestor{Guid.NewGuid():N}"[..20] + "@exemplo.com";
            await Seed.OperadorAsync(tenant, email,
                new[] { "ClienteGerenciar", "UsuarioAfiliadaGerenciar", "FinanceiroVisualizar", "RelatorioExportar" });
            return await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);
        }

        private static async Task<JsonElement> FilaAsync(HttpClient gestor, string status = null)
        {
            var r = await gestor.GetAsync("/api/wl/kyc/app/carteira" + (status == null ? "" : "?status=" + status));
            r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
            return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
        }

        [Fact]
        public async Task Gestor_aprova_e_a_aprovacao_ativa_vinculo_e_contrato_de_venda_direta_sem_comissao()
        {
            const int tenant = 8990;
            await PrepararAfiliadaAsync(tenant);
            using var factory = new WlApiFactory(_db, tenant);
            using var gestor = await GestorAsync(factory, tenant);
            var (ana, _) = await AbrirCarteiraAsync(factory, tenant, "rev-ana");

            var rascunho = await CadastrarAsync(ana, Cnpj(tenant, 1));
            (await FilaAsync(gestor)).GetArrayLength().Should().Be(0, "rascunho não entra na fila");
            await EnviarComDocumentosAsync(ana, rascunho.Id);

            var fila = await FilaAsync(gestor);
            var item = fila.EnumerateArray().Single();
            item.GetProperty("origem").GetString().Should().Be(nameof(WlCarteiraOrigem.Cadastro));
            item.GetProperty("vendedora").GetString().Should().Be("Vendedora rev-ana", "nome cadastrado, não o e-mail (VEI-RD-160)");
            var detalhe = JsonDocument.Parse(await gestor.GetStringAsync($"/api/wl/kyc/app/carteira/{rascunho.Id}")).RootElement;
            var documentos = detalhe.GetProperty("documentos").EnumerateArray().ToList();
            documentos.Should().HaveCount(2);
            var download = await gestor.GetAsync($"/api/wl/kyc/app/carteira/{rascunho.Id}/documentos/{documentos[0].GetProperty("id").GetGuid()}");
            download.StatusCode.Should().Be(HttpStatusCode.OK);

            var aprovado = await gestor.PostAsync($"/api/wl/kyc/app/carteira/{rascunho.Id}/aprovar", null);
            aprovado.StatusCode.Should().Be(HttpStatusCode.OK, await aprovado.Content.ReadAsStringAsync());

            using (var db = new VeiculandoDataContext())
            {
                (await db.AfiliadaClientes.SingleAsync(v => v.IdAfiliada == tenant && v.IdCliente == rascunho.ClienteId))
                    .Status.Should().Be(StatusVinculoEnum.Ativo);
                var emailDaCasa = AnuncianteDaCasaProvisionamento.EmailDe(tenant);
                var casa = await db.WlUsuariosAnunciante.SingleAsync(u => u.AfiliadaId == tenant && u.Email.Endereco == emailDaCasa);
                var contrato = await db.AgenciaClientes.SingleAsync(l => l.IdCliente == rascunho.ClienteId);
                contrato.IdAgencia.Should().Be(casa.AgenciaId!.Value, "o contrato é com a agência de venda direta");
                contrato.ComissaoAgencia.Should().Be(0m, "comissão de agência entra no valor do pedido; a da vendedora não mora no Core");
            }
            (await ContextoAsync(ana)).GetProperty("clients").EnumerateArray()
                .Select(c => c.GetProperty("id").GetInt32()).Should().Equal(rascunho.ClienteId);
            (await FilaAsync(gestor)).GetArrayLength().Should().Be(0);
            (await gestor.PostAsync($"/api/wl/kyc/app/carteira/{rascunho.Id}/aprovar", null)).StatusCode
                .Should().Be(HttpStatusCode.Conflict, "já aprovado");
        }

        [Fact]
        public async Task Vendedora_nao_analisa_o_proprio_cadastro_mesmo_com_permissao()
        {
            const int tenant = 8991;
            await PrepararAfiliadaAsync(tenant);
            using var factory = new WlApiFactory(_db, tenant);
            var email = $"rev-auto{Guid.NewGuid():N}"[..20] + "@exemplo.com";
            await Seed.OperadorAsync(tenant, email, new[] { "PedidoCriar", "ClienteGerenciar" });
            using var painelDaAna = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);
            var emitida = await painelDaAna.PostAsJsonAsync("/api/wl/prospeccao/sessao", new { anuncianteId = (int?)null });
            var sessao = JsonDocument.Parse(await emitida.Content.ReadAsStringAsync()).RootElement;
            using var ana = factory.ClienteAnonimo();
            var resgate = await ana.PostAsJsonAsync("/api/wl/app/prospeccao/sessao", new
            {
                Token = sessao.GetProperty("token").GetString(), OperadorId = sessao.GetProperty("fonteUsuarioId").GetInt32(),
                AnuncianteId = (int?)null
            });
            ana.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",
                JsonDocument.Parse(await resgate.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString());

            var c = await CadastrarAsync(ana, Cnpj(tenant, 1));
            await EnviarComDocumentosAsync(ana, c.Id);

            (await painelDaAna.PostAsync($"/api/wl/kyc/app/carteira/{c.Id}/aprovar", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await painelDaAna.PostAsJsonAsync($"/api/wl/kyc/app/carteira/{c.Id}/ajustes", new { reason = "x" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await painelDaAna.PostAsJsonAsync($"/api/wl/kyc/app/carteira/{c.Id}/reprovar", new { reason = "x" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            using var db = new VeiculandoDataContext();
            (await db.WlCarteiraClientes.SingleAsync(x => x.Id == c.Id)).Status.Should().Be(WlCarteiraStatus.PendenteAnalise);
        }

        [Fact]
        public async Task Ajustes_e_reprovacao_exigem_motivo_e_a_vendedora_reenvia_depois_dos_ajustes()
        {
            const int tenant = 8992;
            await PrepararAfiliadaAsync(tenant);
            using var factory = new WlApiFactory(_db, tenant);
            using var gestor = await GestorAsync(factory, tenant);
            var (ana, _) = await AbrirCarteiraAsync(factory, tenant, "rev-ana");
            var c = await CadastrarAsync(ana, Cnpj(tenant, 1));
            await EnviarComDocumentosAsync(ana, c.Id);

            (await gestor.PostAsJsonAsync($"/api/wl/kyc/app/carteira/{c.Id}/ajustes", new { reason = " " })).StatusCode
                .Should().Be(HttpStatusCode.BadRequest);
            (await gestor.PostAsJsonAsync($"/api/wl/kyc/app/carteira/{c.Id}/ajustes", new { reason = "Contrato ilegível" })).StatusCode
                .Should().Be(HttpStatusCode.OK);
            var lista = JsonDocument.Parse(await ana.GetStringAsync("/api/wl/app/prospeccao/carteira")).RootElement.EnumerateArray().Single();
            lista.GetProperty("status").GetString().Should().Be(nameof(WlCarteiraStatus.AjustesSolicitados));
            lista.GetProperty("motivo").GetString().Should().Be("Contrato ilegível");

            (await AnexarAsync(ana, c.Id, WlCarteiraDocumento.TipoContratoSocial)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await ana.PostAsync($"/api/wl/app/prospeccao/carteira/{c.Id}/enviar", null)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await FilaAsync(gestor)).GetArrayLength().Should().Be(1);

            (await gestor.PostAsJsonAsync($"/api/wl/kyc/app/carteira/{c.Id}/reprovar", new { reason = "CNPJ baixado" })).StatusCode
                .Should().Be(HttpStatusCode.OK);
            (await FilaAsync(gestor, nameof(WlCarteiraStatus.Reprovado))).GetArrayLength().Should().Be(1);
            using var db = new VeiculandoDataContext();
            (await db.AfiliadaClientes.SingleAsync(v => v.IdAfiliada == tenant && v.IdCliente == c.ClienteId)).Status
                .Should().Be(StatusVinculoEnum.Inativo, "reprovado não vira cliente da exibidora (I4)");
        }

        [Fact]
        public async Task Reprovacao_inativa_o_cliente_criado_pela_vendedora_e_a_retomada_aprovada_reativa()
        {
            const int tenant = 8966;
            await PrepararAfiliadaAsync(tenant);
            using var factory = new WlApiFactory(_db, tenant);
            using var gestor = await GestorAsync(factory, tenant);
            var (ana, _) = await AbrirCarteiraAsync(factory, tenant, "inat-ana");
            var (bia, _) = await AbrirCarteiraAsync(factory, tenant, "inat-bia");
            var cnpj = Cnpj(tenant, 1);

            var c = await CadastrarAsync(ana, cnpj);
            await EnviarComDocumentosAsync(ana, c.Id);
            (await gestor.PostAsJsonAsync($"/api/wl/kyc/app/carteira/{c.Id}/reprovar", new { reason = "Documentos falsos" }))
                .StatusCode.Should().Be(HttpStatusCode.OK);
            using (var db = new VeiculandoDataContext())
                (await db.Clientes.SingleAsync(x => x.Id == c.ClienteId)).StatusExibicao
                    .Should().Be(StatusExibicaoEnum.Inativo, "cliente que só existia por esse cadastro não fica ativo no Core (VEI-SUP-39)");

            // Outra vendedora retoma o reprovado; aprovado, o cliente volta a valer.
            var retomado = await CadastrarAsync(bia, cnpj);
            retomado.Id.Should().Be(c.Id);
            retomado.ClienteId.Should().Be(c.ClienteId);
            await EnviarComDocumentosAsync(bia, c.Id);
            var aprovado = await gestor.PostAsync($"/api/wl/kyc/app/carteira/{c.Id}/aprovar", null);
            aprovado.StatusCode.Should().Be(HttpStatusCode.OK, await aprovado.Content.ReadAsStringAsync());
            using (var db = new VeiculandoDataContext())
                (await db.Clientes.SingleAsync(x => x.Id == c.ClienteId)).StatusExibicao.Should().Be(StatusExibicaoEnum.Ativo);
            (await ContextoAsync(bia)).GetProperty("clients").EnumerateArray()
                .Select(x => x.GetProperty("id").GetInt32()).Should().Equal(c.ClienteId);
        }

        [Fact]
        public async Task Reprovacao_nao_inativa_cliente_que_ja_era_da_exibidora()
        {
            const int tenant = 8965;
            await PrepararAfiliadaAsync(tenant);
            var cnpj = Cnpj(tenant, 1);
            var clienteId = await ClienteDaExibidoraAsync(tenant, cnpj);
            using var factory = new WlApiFactory(_db, tenant);
            using var gestor = await GestorAsync(factory, tenant);
            var (ana, _) = await AbrirCarteiraAsync(factory, tenant, "inat-sol");

            var c = await CadastrarAsync(ana, cnpj);
            c.Origem.Should().Be(nameof(WlCarteiraOrigem.Solicitacao));
            await EnviarComDocumentosAsync(ana, c.Id);
            (await gestor.PostAsJsonAsync($"/api/wl/kyc/app/carteira/{c.Id}/reprovar", new { reason = "Não é dela" }))
                .StatusCode.Should().Be(HttpStatusCode.OK);
            using var db = new VeiculandoDataContext();
            (await db.Clientes.SingleAsync(x => x.Id == clienteId)).StatusExibicao
                .Should().Be(StatusExibicaoEnum.Ativo, "o cliente já existia antes da carteira");
        }

        private static Task<int> LimparRascunhosAsync() =>
            new CarteiraRascunhoLimpeza(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<CarteiraRascunhoLimpeza>.Instance).ExecutarAsync(DateTime.UtcNow, default);

        private static async Task EnvelhecerAsync(Guid carteiraId, int dias)
        {
            using var db = new VeiculandoDataContext();
            await db.Database.ExecuteSqlCommandAsync(
                "UPDATE WL_CarteiraCliente SET AtualizadoEm = DATEADD(day, -@p0, GETUTCDATE()) WHERE Id = @p1", dias, carteiraId);
        }

        private static async Task<StatusExibicaoEnum> StatusClienteAsync(int clienteId)
        {
            using var db = new VeiculandoDataContext();
            return (await db.Clientes.SingleAsync(x => x.Id == clienteId)).StatusExibicao;
        }

        [Fact]
        public async Task Rascunho_abandonado_inativa_o_cliente_e_a_retomada_aprovada_reativa()
        {
            const int tenant = 8962;
            await PrepararAfiliadaAsync(tenant);
            using var factory = new WlApiFactory(_db, tenant);
            using var gestor = await GestorAsync(factory, tenant);
            var (ana, _) = await AbrirCarteiraAsync(factory, tenant, "rasc-ana");
            var (bia, _) = await AbrirCarteiraAsync(factory, tenant, "rasc-bia");
            var cnpj = Cnpj(tenant, 1);

            var c = await CadastrarAsync(ana, cnpj);
            await EnvelhecerAsync(c.Id, CarteiraRascunhoLimpeza.DiasPadrao + 1);
            await LimparRascunhosAsync();
            (await StatusClienteAsync(c.ClienteId)).Should()
                .Be(StatusExibicaoEnum.Inativo, "rascunho nunca enviado não deixa cliente ativo no Core (VEI-RD-161)");

            // Outra vendedora retoma o rascunho abandonado; aprovado, o cliente volta a valer.
            var retomado = await CadastrarAsync(bia, cnpj);
            retomado.Id.Should().Be(c.Id);
            retomado.Origem.Should().Be(nameof(WlCarteiraOrigem.Cadastro));
            await EnviarComDocumentosAsync(bia, c.Id);
            var aprovado = await gestor.PostAsync($"/api/wl/kyc/app/carteira/{c.Id}/aprovar", null);
            aprovado.StatusCode.Should().Be(HttpStatusCode.OK, await aprovado.Content.ReadAsStringAsync());
            (await StatusClienteAsync(c.ClienteId)).Should().Be(StatusExibicaoEnum.Ativo);
        }

        [Fact]
        public async Task Limpeza_nao_inativa_rascunho_recente_nem_cliente_que_ja_era_da_exibidora()
        {
            const int tenant = 8963;
            await PrepararAfiliadaAsync(tenant);
            var cnpjSolicitacao = Cnpj(tenant, 2);
            var clienteDaExibidora = await ClienteDaExibidoraAsync(tenant, cnpjSolicitacao);
            using var factory = new WlApiFactory(_db, tenant);
            var (ana, _) = await AbrirCarteiraAsync(factory, tenant, "rasc-rec");

            var recente = await CadastrarAsync(ana, Cnpj(tenant, 1));
            await EnvelhecerAsync(recente.Id, CarteiraRascunhoLimpeza.DiasPadrao - 1);
            var solicitacao = await CadastrarAsync(ana, cnpjSolicitacao);
            solicitacao.Origem.Should().Be(nameof(WlCarteiraOrigem.Solicitacao));
            await EnvelhecerAsync(solicitacao.Id, CarteiraRascunhoLimpeza.DiasPadrao + 30);

            await LimparRascunhosAsync();
            (await StatusClienteAsync(recente.ClienteId)).Should().Be(StatusExibicaoEnum.Ativo, "o rascunho ainda está no prazo");
            (await StatusClienteAsync(clienteDaExibidora)).Should().Be(StatusExibicaoEnum.Ativo, "o cliente já existia antes da carteira");
        }

        [Fact]
        public async Task Troca_de_responsavel_move_o_cliente_e_o_relatorio_mantem_o_pedido_com_quem_vendeu()
        {
            const int tenant = 8993;
            await PrepararAfiliadaAsync(tenant);
            using (var ctx = new VeiculandoDataContext())
                await ctx.Database.ExecuteSqlCommandAsync(@"
SET IDENTITY_INSERT Periodo ON;
INSERT Periodo (Id, Codigo, Periodicidade, DataInicio, DataFim, StatusExibicao)
VALUES (@p0, @p1, 0, DATEADD(day, 1, GETUTCDATE()), DATEADD(day, 8, GETUTCDATE()), 1);
SET IDENTITY_INSERT Periodo OFF;", tenant, $"CRT{tenant}");
            int pecaId;
            using (var lookup = new VeiculandoDataContext())
                pecaId = await lookup.Database.SqlQuery<int>("SELECT TOP 1 Id FROM Peca WHERE Codigo = @p0", $"P-CRT-{tenant}").SingleAsync();

            using var factory = new WlApiFactory(_db, tenant);
            using var gestor = await GestorAsync(factory, tenant);
            var (ana, anaId) = await AbrirCarteiraAsync(factory, tenant, "rev-ana");
            var (bia, biaId) = await AbrirCarteiraAsync(factory, tenant, "rev-bia");
            var c = await CadastrarAsync(ana, Cnpj(tenant, 1));
            await EnviarComDocumentosAsync(ana, c.Id);
            (await gestor.PostAsync($"/api/wl/kyc/app/carteira/{c.Id}/aprovar", null)).StatusCode.Should().Be(HttpStatusCode.OK);

            // Ana fecha um pedido.
            var campanha = await ana.PostAsJsonAsync("/api/wl/app/checkout/context/campaigns", new
            {
                name = "Campanha Ana", product = "Pão", clientId = c.ClienteId,
                startDate = DateTime.UtcNow.Date.AddDays(1), endDate = DateTime.UtcNow.Date.AddDays(8)
            });
            campanha.StatusCode.Should().Be(HttpStatusCode.Created, await campanha.Content.ReadAsStringAsync());
            var campanhaId = JsonDocument.Parse(await campanha.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt32();
            var cotacao = await ana.PostAsJsonAsync("/api/wl/app/checkout/quote",
                new { pieceIds = new[] { pecaId }, campaignId = campanhaId, periodCode = $"CRT{tenant}" });
            cotacao.StatusCode.Should().Be(HttpStatusCode.OK, await cotacao.Content.ReadAsStringAsync());
            ana.DefaultRequestHeaders.Add("Idempotency-Key", $"rev-{tenant}-pedido");
            var pedido = await ana.PostAsJsonAsync("/api/wl/app/checkout/orders", new
            {
                quoteId = (await cotacao.Content.ReadFromJsonAsync<Resgatada>())!.QuoteId, termsAccepted = true, termsVersion = "v1"
            });
            pedido.StatusCode.Should().Be(HttpStatusCode.OK, await pedido.Content.ReadAsStringAsync());

            // Gestor passa o cliente para a Bia.
            (await gestor.PostAsJsonAsync($"/api/wl/kyc/app/carteira/{c.Id}/responsavel", new { vendedorId = biaId, reason = " " }))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var troca = await gestor.PostAsJsonAsync($"/api/wl/kyc/app/carteira/{c.Id}/responsavel",
                new { vendedorId = biaId, reason = "Carteira redistribuída" });
            troca.StatusCode.Should().Be(HttpStatusCode.OK, await troca.Content.ReadAsStringAsync());
            (await ContextoAsync(ana)).GetProperty("clients").GetArrayLength().Should().Be(0);
            (await ContextoAsync(bia)).GetProperty("clients").EnumerateArray()
                .Select(x => x.GetProperty("id").GetInt32()).Should().Equal(c.ClienteId);

            // O relatório continua dando o pedido para a Ana.
            var relatorio = JsonDocument.Parse(await gestor.GetStringAsync("/api/wl/relatorios/pedidos-por-vendedora")).RootElement;
            relatorio.GetProperty("totalPedidos").GetInt32().Should().Be(1);
            var linha = relatorio.GetProperty("pedidos").EnumerateArray().Single();
            linha.GetProperty("vendedorId").GetInt32().Should().Be(anaId);
            linha.GetProperty("vendedora").GetString().Should().StartWith("Vendedora ", "nome cadastrado, não o e-mail (VEI-RD-160)");
            linha.GetProperty("cnpjCliente").GetString().Should().Be(Cnpj(tenant, 1));
            var porBia = JsonDocument.Parse(await gestor.GetStringAsync($"/api/wl/relatorios/pedidos-por-vendedora?vendedorId={biaId}")).RootElement;
            porBia.GetProperty("totalPedidos").GetInt32().Should().Be(0);

            var csv = await gestor.GetAsync("/api/wl/relatorios/pedidos-por-vendedora/exportar");
            csv.StatusCode.Should().Be(HttpStatusCode.OK);
            var texto = await csv.Content.ReadAsStringAsync();
            texto.Should().Contain("VendedoraId;Vendedora;Pedido;DataPedido;Cliente;CnpjCliente").And.Contain(Cnpj(tenant, 1));
        }

        [Fact]
        public async Task Remover_responsavel_tira_o_cliente_da_vendedora_e_exige_gestor()
        {
            const int tenant = 8994;
            await PrepararAfiliadaAsync(tenant);
            using var factory = new WlApiFactory(_db, tenant);
            using var gestor = await GestorAsync(factory, tenant);
            var revisorEmail = $"rev-so{Guid.NewGuid():N}"[..20] + "@exemplo.com";
            await Seed.OperadorAsync(tenant, revisorEmail, new[] { "ClienteGerenciar" });
            using var revisor = await factory.ClienteAutenticadoAsync(revisorEmail, Seed.SenhaPadrao);
            var (ana, _) = await AbrirCarteiraAsync(factory, tenant, "rev-ana");
            var c = await CadastrarAsync(ana, Cnpj(tenant, 1));
            await EnviarComDocumentosAsync(ana, c.Id);
            (await revisor.PostAsync($"/api/wl/kyc/app/carteira/{c.Id}/aprovar", null)).StatusCode.Should().Be(HttpStatusCode.OK);

            var semPermissao = new HttpRequestMessage(HttpMethod.Delete, $"/api/wl/kyc/app/carteira/{c.Id}/responsavel")
            { Content = JsonContent.Create(new { reason = "Saiu da empresa" }) };
            (await revisor.SendAsync(semPermissao)).StatusCode.Should().Be(HttpStatusCode.Forbidden, "trocar responsável é do gestor");

            var remover = new HttpRequestMessage(HttpMethod.Delete, $"/api/wl/kyc/app/carteira/{c.Id}/responsavel")
            { Content = JsonContent.Create(new { reason = "Saiu da empresa" }) };
            (await gestor.SendAsync(remover)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await ContextoAsync(ana)).GetProperty("clients").GetArrayLength().Should().Be(0);
        }

        [Fact]
        public async Task Sem_ClienteGerenciar_nao_analisa_mesmo_sem_ser_quem_enviou()
        {
            // P7 (rev. 3): quem aprova é quem tem a permissão, não um login específico.
            const int tenant = 8995;
            await PrepararAfiliadaAsync(tenant);
            using var factory = new WlApiFactory(_db, tenant);
            var email = $"rev-sem{Guid.NewGuid():N}"[..20] + "@exemplo.com";
            await Seed.OperadorAsync(tenant, email, new[] { "PedidoCriar", "FinanceiroVisualizar" });
            using var semPermissao = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);
            var (ana, _) = await AbrirCarteiraAsync(factory, tenant, "rev-ana");
            var c = await CadastrarAsync(ana, Cnpj(tenant, 1));
            await EnviarComDocumentosAsync(ana, c.Id);

            (await semPermissao.GetAsync("/api/wl/kyc/app/carteira")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await semPermissao.PostAsync($"/api/wl/kyc/app/carteira/{c.Id}/aprovar", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await semPermissao.PostAsJsonAsync($"/api/wl/kyc/app/carteira/{c.Id}/reprovar", new { reason = "x" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            using var db = new VeiculandoDataContext();
            (await db.WlCarteiraClientes.SingleAsync(x => x.Id == c.Id)).Status.Should().Be(WlCarteiraStatus.PendenteAnalise);
        }
    }
}
