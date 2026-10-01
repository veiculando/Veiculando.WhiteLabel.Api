using System;
using System.Data.Entity;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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

namespace Veiculando.WhiteLabel.Api.Tests
{
    /// <summary>
    /// HF-8 — prospecção ponta a ponta com o anunciante da casa: a Exibidora abre a
    /// sessão SEM escolher anunciante (<c>anuncianteId: null</c>, o que o painel sempre
    /// manda) e o App abre como a própria afiliada, em venda direta, registrando o
    /// operador que abriu.
    /// </summary>
    [Collection(DatabaseCollection.Nome)]
    public class ProspeccaoAnuncianteDaCasaTests
    {
        private readonly SqlServerFixture _db;

        public ProspeccaoAnuncianteDaCasaTests(SqlServerFixture db) => _db = db;

        private sealed record Sessao(string AppUrl, string Token, int FonteUsuarioId);
        private sealed record Login(string Token, bool Prospeccao, int FonteUsuarioId);
        private sealed record Cotacao(Guid QuoteId, decimal Total);

        private static async Task PrepararAfiliadaAsync(int afiliadaId, string cnpj, bool comTelefone = true)
        {
            // Local + peça + reserva garantem o pano de fundo do Core de teste (usuário 1,
            // perfil, etc.). Depois a afiliada ganha CNPJ próprio e os dados que a
            // agência de venda direta copia dela.
            var local = await Seed.LocalAsync(afiliadaId, $"CSA{afiliadaId}");
            var peca = await Seed.PecaAsync(local, $"P-CSA-{afiliadaId}");
            await Seed.ReservaAsync(afiliadaId, $"CSA-{afiliadaId}", peca);

            using var ctx = new VeiculandoDataContext();
            await ctx.Database.ExecuteSqlCommandAsync(@"
IF NOT EXISTS (SELECT 1 FROM PerfilUsuario WHERE Codigo = 'UsuarioAnunciante')
    INSERT PerfilUsuario (Nome, Codigo, DataCadastro, DataAtualizacao, StatusExibicao)
    VALUES ('Anunciante WL', 'UsuarioAnunciante', GETUTCDATE(), GETUTCDATE(), 1);
UPDATE Afiliada SET Cnpj = @p1, IdUsuarioCadastro = 1, RazaoSocial = @p2, Cidade = 'Sao Paulo', Uf = 'SP',
    Telefone = @p3 WHERE Id = @p0;
SET IDENTITY_INSERT Periodo ON;
INSERT Periodo (Id, Codigo, Periodicidade, DataInicio, DataFim, StatusExibicao)
VALUES (@p0, @p4, 0, DATEADD(day, 1, GETUTCDATE()), DATEADD(day, 8, GETUTCDATE()), 1);
SET IDENTITY_INSERT Periodo OFF;",
                afiliadaId, cnpj, $"Exibidora {afiliadaId} Ltda",
                comTelefone ? "11999999999" : (object)DBNull.Value, $"CSA{afiliadaId}");
        }

        private static async Task<Sessao> EmitirSemAnuncianteAsync(HttpClient operador)
        {
            // É exatamente o que a Exibidora manda: sem anunciante.
            var resposta = await operador.PostAsJsonAsync("/api/wl/prospeccao/sessao", new { anuncianteId = (int?)null });
            resposta.StatusCode.Should().Be(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
            return (await resposta.Content.ReadFromJsonAsync<Sessao>())!;
        }

        private static Task<HttpResponseMessage> ResgatarAsync(HttpClient app, Sessao s, int? operadorId = null) =>
            app.PostAsJsonAsync("/api/wl/app/prospeccao/sessao",
                new { Token = s.Token, OperadorId = operadorId ?? s.FonteUsuarioId, AnuncianteId = (int?)null });

        private static async Task<string> NovoOperadorAsync(int afiliadaId, string prefixo)
        {
            var email = $"{prefixo}{Guid.NewGuid():N}"[..20] + "@exemplo.com";
            await Seed.OperadorAsync(afiliadaId, email, new[] { "PedidoCriar", "UsuarioAfiliadaGerenciar" });
            return email;
        }

        [Fact]
        public async Task Resgate_sem_anunciante_abre_a_sessao_como_anunciante_da_casa()
        {
            const int tenant = 8971;
            const string cnpj = "12345678000195";
            await PrepararAfiliadaAsync(tenant, cnpj);
            var email = await NovoOperadorAsync(tenant, "casa-op");

            using var factory = new WlApiFactory(_db, tenant);
            using var operador = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);
            using var app = factory.ClienteAnonimo();
            var sessao = await EmitirSemAnuncianteAsync(operador);

            var resgate = await ResgatarAsync(app, sessao);
            resgate.StatusCode.Should().Be(HttpStatusCode.OK, await resgate.Content.ReadAsStringAsync());
            var login = (await resgate.Content.ReadFromJsonAsync<Login>())!;
            login.Prospeccao.Should().BeTrue();
            login.FonteUsuarioId.Should().Be(sessao.FonteUsuarioId);

            using var db = new VeiculandoDataContext();
            var emailDaCasa = AnuncianteDaCasaProvisionamento.EmailDe(tenant);
            var casa = await db.WlUsuariosAnunciante.SingleAsync(u =>
                u.AfiliadaId == tenant && u.Email.Endereco == emailDaCasa);
            casa.Cnpj.Should().Be(cnpj);
            casa.ClienteId.Should().NotBeNull();
            casa.AgenciaId.Should().NotBeNull();

            // A cadeia é a de venda direta do KYC "ad": agência com o CNPJ da afiliada.
            var agencia = await db.Agencias.SingleAsync(a => a.Cnpj.Numero == cnpj);
            agencia.Id.Should().Be(casa.AgenciaId!.Value);
            agencia.Nome.Should().Be(Veiculando.Domain.Services.AgenciaVendaDiretaProvisionamento.NomeFantasia);
            (await db.AfiliadaAgencias.CountAsync(v => v.IdAfiliada == tenant && v.IdAgencia == agencia.Id)).Should().Be(1);
            (await db.AfiliadaClientes.CountAsync(v => v.IdAfiliada == tenant && v.IdCliente == casa.ClienteId!.Value)).Should().Be(1);
            (await db.AgenciaClientes.CountAsync(c => c.IdAgencia == agencia.Id && c.IdCliente == casa.ClienteId!.Value)).Should().Be(1);
            (await db.UsuariosAnunciantes.CountAsync(u => u.Email.Endereco == casa.Email.Endereco)).Should().Be(1);
            var onboarding = await db.WlAppOnboardings.SingleAsync(o => o.UsuarioId == casa.Id);
            onboarding.Status.Should().Be(WlAppKycStatus.Aprovado);
            onboarding.TipoConta.Should().Be("ad");

            // O evento Resgatada grava o anunciante da casa; a auditoria mostra Emitida + Resgatada.
            var resgatada = await db.WlProspeccaoSessaoEventos.SingleAsync(e =>
                e.AfiliadaId == tenant && e.Evento == WlProspeccaoEventoTipo.Resgatada);
            resgatada.AnuncianteId.Should().Be(casa.Id);
            resgatada.OperadorId.Should().Be(sessao.FonteUsuarioId);
            (await db.WlProspeccaoSessaoEventos.CountAsync(e =>
                e.AfiliadaId == tenant && e.Evento == WlProspeccaoEventoTipo.Emitida)).Should().Be(1);

            using var json = JsonDocument.Parse(await operador.GetStringAsync("/api/wl/prospeccao/auditoria"));
            var linha = json.RootElement.GetProperty("itens").EnumerateArray().Single();
            linha.GetProperty("operador").GetProperty("id").GetInt32().Should().Be(sessao.FonteUsuarioId);
            linha.GetProperty("anunciante").GetProperty("id").GetInt32().Should().Be(casa.Id);
            linha.GetProperty("usadaEm").ValueKind.Should().NotBe(JsonValueKind.Null);

            // A sessão navega no App: o contexto da compra responde (KYC aprovado, agência ativa).
            app.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
            (await app.GetAsync("/api/wl/app/checkout/context")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task Anunciante_da_casa_e_provisionado_uma_vez_so_entre_sessoes()
        {
            const int tenant = 8972;
            const string cnpj = "98765432000198";
            await PrepararAfiliadaAsync(tenant, cnpj);
            var email = await NovoOperadorAsync(tenant, "casa-idem");

            using var factory = new WlApiFactory(_db, tenant);
            using var operador = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);
            using var app = factory.ClienteAnonimo();
            for (var i = 0; i < 3; i++)
            {
                var r = await ResgatarAsync(app, await EmitirSemAnuncianteAsync(operador));
                r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
            }

            using var db = new VeiculandoDataContext();
            (await db.WlUsuariosAnunciante.CountAsync(u => u.AfiliadaId == tenant)).Should().Be(1);
            (await db.Agencias.CountAsync(a => a.Cnpj.Numero == cnpj)).Should().Be(1);
            (await db.Clientes.CountAsync(c => c.Cnpj.Numero == cnpj)).Should().Be(1);
            (await db.WlAppOnboardings.CountAsync(o => o.AfiliadaId == tenant)).Should().Be(1);
            (await db.WlProspeccaoSessaoEventos.CountAsync(e =>
                e.AfiliadaId == tenant && e.Evento == WlProspeccaoEventoTipo.Resgatada)).Should().Be(3);
        }

        [Fact]
        public async Task Token_reusado_e_operador_diferente_do_assinado_sao_recusados()
        {
            const int tenant = 8973;
            await PrepararAfiliadaAsync(tenant, "11444777000161");
            var email = await NovoOperadorAsync(tenant, "casa-seg");
            var outroEmail = $"casa-outro{Guid.NewGuid():N}"[..20] + "@exemplo.com";
            var outro = await Seed.OperadorAsync(tenant, outroEmail, new[] { "PedidoCriar" });

            using var factory = new WlApiFactory(_db, tenant);
            using var operador = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);
            using var app = factory.ClienteAnonimo();

            var trocada = await EmitirSemAnuncianteAsync(operador);
            (await ResgatarAsync(app, trocada, operadorId: outro)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            var sessao = await EmitirSemAnuncianteAsync(operador);
            (await ResgatarAsync(app, sessao)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await ResgatarAsync(app, sessao)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task Exibidora_sem_telefone_nao_abre_a_casa_e_nada_fica_provisionado()
        {
            const int tenant = 8974;
            await PrepararAfiliadaAsync(tenant, "33000167000101", comTelefone: false);
            var email = await NovoOperadorAsync(tenant, "casa-sem-tel");

            using var factory = new WlApiFactory(_db, tenant);
            using var operador = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);
            using var app = factory.ClienteAnonimo();
            var resposta = await ResgatarAsync(app, await EmitirSemAnuncianteAsync(operador));

            resposta.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await resposta.Content.ReadAsStringAsync()).Should().Contain("telefone");
            using var db = new VeiculandoDataContext();
            (await db.WlUsuariosAnunciante.CountAsync(u => u.AfiliadaId == tenant)).Should().Be(0,
                "a transação desfaz a cadeia inteira quando um elo falha");
            (await db.WlProspeccaoSessaoEventos.CountAsync(e =>
                e.AfiliadaId == tenant && e.Evento == WlProspeccaoEventoTipo.Resgatada)).Should().Be(0);
        }

        [Fact]
        public async Task Anunciante_da_casa_nao_tem_login_por_senha()
        {
            const int tenant = 8975;
            await PrepararAfiliadaAsync(tenant, "55443322000160");
            var email = await NovoOperadorAsync(tenant, "casa-login");

            using var factory = new WlApiFactory(_db, tenant);
            using var operador = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);
            using var app = factory.ClienteAnonimo();
            var r = await ResgatarAsync(app, await EmitirSemAnuncianteAsync(operador)); r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());

            var login = await app.PostAsJsonAsync("/api/wl/app/auth/login", new
            {
                email = AnuncianteDaCasaProvisionamento.EmailDe(tenant),
                password = Seed.SenhaPadrao
            });
            login.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task Pedido_da_casa_sai_com_a_trilha_do_operador_na_cadeia_de_venda_direta()
        {
            const int tenant = 8976;
            const string cnpj = "22333444000162";
            await PrepararAfiliadaAsync(tenant, cnpj);
            int pecaId;
            using (var lookup = new VeiculandoDataContext())
                pecaId = await lookup.Database.SqlQuery<int>(
                    "SELECT TOP 1 Id FROM Peca WHERE Codigo = @p0", $"P-CSA-{tenant}").SingleAsync();
            var email = await NovoOperadorAsync(tenant, "casa-pedido");

            using var factory = new WlApiFactory(_db, tenant);
            using var operador = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);
            using var app = factory.ClienteAnonimo();
            var sessao = await EmitirSemAnuncianteAsync(operador);
            var resgate = await ResgatarAsync(app, sessao);
            resgate.StatusCode.Should().Be(HttpStatusCode.OK, await resgate.Content.ReadAsStringAsync());
            var login = (await resgate.Content.ReadFromJsonAsync<Login>())!;
            app.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);

            var campanha = await app.PostAsJsonAsync("/api/wl/app/checkout/context/campaigns", new
            {
                name = "Campanha da casa", product = "Venda direta",
                startDate = DateTime.UtcNow.Date.AddDays(1), endDate = DateTime.UtcNow.Date.AddDays(8)
            });
            campanha.StatusCode.Should().Be(HttpStatusCode.Created, await campanha.Content.ReadAsStringAsync());
            int campanhaId;
            using (var lookup = new VeiculandoDataContext())
                campanhaId = (await lookup.Campanhas.SingleAsync(c => c.Nome == "Campanha da casa" && c.Cliente.Cnpj.Numero == cnpj)).Id;

            var cotacaoResposta = await app.PostAsJsonAsync("/api/wl/app/checkout/quote",
                new { pieceIds = new[] { pecaId }, campaignId = campanhaId, periodCode = $"CSA{tenant}" });
            cotacaoResposta.StatusCode.Should().Be(HttpStatusCode.OK, await cotacaoResposta.Content.ReadAsStringAsync());
            var cotacao = (await cotacaoResposta.Content.ReadFromJsonAsync<Cotacao>())!;

            app.DefaultRequestHeaders.Add("Idempotency-Key", $"casa-{tenant}-pedido");
            var pedidoResposta = await app.PostAsJsonAsync("/api/wl/app/checkout/orders",
                new { quoteId = cotacao.QuoteId, termsAccepted = true, termsVersion = "aurum-v1" });
            pedidoResposta.StatusCode.Should().Be(HttpStatusCode.OK, await pedidoResposta.Content.ReadAsStringAsync());

            using var db = new VeiculandoDataContext();
            var pedido = await db.Pedidos.Include(p => p.Campanha).SingleAsync(p => p.IdCampanha == campanhaId);
            var agencia = await db.Agencias.SingleAsync(a => a.Cnpj.Numero == cnpj);
            pedido.Campanha.IdAgencia.Should().Be(agencia.Id, "o pedido entra na agência de venda direta da afiliada");
            pedido.FonteOrigem.Should().Be(FonteOrigemEnum.WhiteLabel);
            pedido.FonteAgenciaId.Should().Be(tenant);
            pedido.FonteUsuarioId.Should().Be(sessao.FonteUsuarioId, "a trilha identifica o operador que abriu a prospecção");
            (await db.WlProspeccaoSessaoEventos.CountAsync(e =>
                e.AfiliadaId == tenant && e.Evento == WlProspeccaoEventoTipo.PedidoCriado)).Should().Be(1);

            (await operador.GetStringAsync("/api/wl/prospeccao/auditoria")).Should().Contain(pedido.Codigo);
        }
    }
}
