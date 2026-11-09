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
using Veiculando.Domain.Entities;
using Veiculando.Domain.Entities.WhiteLabel;
using Veiculando.Domain.Enums;
using Veiculando.WhiteLabel.Api.Services;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests;

/// <summary>
/// Revisão de pares do TP 4.1 (VEI-RD-45m, 67e, 68e): itens só da exibidora (B1), conjunto único
/// de clientes com o mesmo predicado de vigência (B2), filtro por cliente (B3), verba disponível
/// (I1), validação comum e ordem 404 → 403 → 409 → 400 no PUT (I2–I4).
/// </summary>
[Collection(DatabaseCollection.Nome)]
public sealed class AppMinhasCampanhasEscopoTests
{
    private readonly SqlServerFixture _db;
    public AppMinhasCampanhasEscopoTests(SqlServerFixture db) => _db = db;

    [Fact]
    public async Task Pedido_misto_e_item_deletado_contam_igual_na_lista_e_no_detalhe()
    {
        const int tenant = 8933;
        const int outra = 8934;
        const string email = "mc-pj-8933@exemplo.com";
        var cnpj = ProspeccaoCarteiraTests.Cnpj(tenant, 1);
        var daqui = await Seed.PecaAsync(await Seed.LocalAsync(tenant, $"MC{tenant}"), $"P-MC-{tenant}");
        var dali = await Seed.PecaAsync(await Seed.LocalAsync(outra, $"MC{outra}"), $"P-MC-{outra}");
        await Seed.ReservaAsync(tenant, $"MC-{tenant}", daqui); // grafo de apoio (usuário 1, agência 1, período 1)

        // CNPJ gravado com máscara, como em produção: o pj compara só dígitos.
        var cliente = await ClienteAsync(tenant, "M8933", "Cliente Misto", Mascara(cnpj));
        var comVerba = await CampanhaAsync("MC8933V", 1, 1, cliente, StatusCampanhaEnum.PedidoEmAberto, 100_000m);
        var semVerba = await CampanhaAsync("MC8933N", 1, 1, cliente, StatusCampanhaEnum.PedidoEmAberto, null);
        await CampanhaAsync("MC8933C", 1, 1, cliente, StatusCampanhaEnum.Cancelada, 5_000m);

        // Confirmado: 1.000 daqui + 5.000 de outra exibidora + 700 daqui Deletado.
        var misto = await PedidoAsync(comVerba, "PMC8933A", StatusPedidoEnum.Confirmado);
        await ItemAsync(misto, daqui, StatusPedidoItemEnum.Confirmado, 1_000m);
        await ItemAsync(misto, dali, StatusPedidoItemEnum.Confirmado, 5_000m);
        await ItemAsync(misto, daqui, StatusPedidoItemEnum.Deletado, 700m);
        var pendente = await PedidoAsync(comVerba, "PMC8933B", StatusPedidoEnum.Solicitado);
        await ItemAsync(pendente, daqui, StatusPedidoItemEnum.Solicitado, 2_000m);
        var outroSemVerba = await PedidoAsync(semVerba, "PMC8933C", StatusPedidoEnum.Solicitado);
        await ItemAsync(outroSemVerba, daqui, StatusPedidoItemEnum.Solicitado, 3_000m);

        await Seed.AnuncianteAsync(tenant, email, cnpj);
        await AprovarAsync(tenant, email, "pj", cnpj);
        using var factory = new WlApiFactory(_db, tenant);
        using var app = await LoginAsync(factory, email);

        var lista = await JsonAsync(app, "/api/wl/app/campaigns");
        lista.EnumerateArray().Select(c => c.GetProperty("codigo").GetString())
            .Should().BeEquivalentTo(new[] { "MC8933V", "MC8933N", "MC8933C" }, "o pj vê as campanhas do próprio CNPJ");
        var naLista = lista.EnumerateArray().Single(c => c.GetProperty("codigo").GetString() == "MC8933V");
        naLista.GetProperty("pedidosConfirmados").GetDecimal().Should().Be(1_000m, "nem a outra exibidora nem o item Deletado somam");
        naLista.GetProperty("pedidosPendentes").GetDecimal().Should().Be(2_000m);
        naLista.GetProperty("anunciante").GetProperty("id").GetInt32().Should().Be(cliente);
        lista.EnumerateArray().Single(c => c.GetProperty("codigo").GetString() == "MC8933C")
            .GetProperty("status").GetString().Should().Be("cancelada", "cancelada fica como histórico (P2)");

        var detalhe = await JsonAsync(app, "/api/wl/app/campaigns/MC8933V");
        detalhe.GetProperty("pedidosConfirmados").GetDecimal().Should().Be(naLista.GetProperty("pedidosConfirmados").GetDecimal());
        detalhe.GetProperty("pedidosPendentes").GetDecimal().Should().Be(naLista.GetProperty("pedidosPendentes").GetDecimal());
        detalhe.GetProperty("verbaDisponivel").GetDecimal().Should().Be(97_000m, "verba − confirmados − pendentes desta exibidora");
        detalhe.GetProperty("totalPecas").GetInt32().Should().Be(2);
        detalhe.GetProperty("pecasConfirmadas").GetInt32().Should().Be(1);
        var pedidoMisto = detalhe.GetProperty("pedidos").EnumerateArray().Single(p => p.GetProperty("codigo").GetString() == "PMC8933A");
        pedidoMisto.GetProperty("valor").GetDecimal().Should().Be(1_000m);
        pedidoMisto.GetProperty("pecas").GetInt32().Should().Be(1);

        var detalheSemVerba = await JsonAsync(app, "/api/wl/app/campaigns/MC8933N");
        detalheSemVerba.GetProperty("verbaDisponivel").ValueKind.Should().Be(JsonValueKind.Null, "sem verba não há disponível");

        // Campanha criada pela agência para o CNPJ: o pj vê, mas não edita (decisão do PO, 09/11).
        detalhe.GetProperty("podeEditar").GetBoolean().Should().BeFalse();
        var put = await app.PutAsJsonAsync("/api/wl/app/campaigns/MC8933V", Payload(100_000m));
        put.StatusCode.Should().Be(HttpStatusCode.Forbidden, await put.Content.ReadAsStringAsync());

        var clientes = await JsonAsync(app, "/api/wl/app/clients");
        clientes.GetProperty("perfil").GetString().Should().Be("anunciante_direto");
        clientes.GetProperty("clientes").EnumerateArray().Select(c => c.GetProperty("id").GetInt32()).Should().Equal(cliente);
    }

    [Fact]
    public async Task Agencia_com_contrato_expirado_perde_cliente_e_campanhas_e_edita_so_o_rascunho_vigente()
    {
        const int tenant = 8935;
        const string email = "mc-ag-8935@exemplo.com";
        var peca = await Seed.PecaAsync(await Seed.LocalAsync(tenant, $"MC{tenant}"), $"P-MC-{tenant}");
        await Seed.ReservaAsync(tenant, $"MC-{tenant}", peca);
        var cnpjAgencia = ProspeccaoCarteiraTests.Cnpj(tenant, 9);
        var (agencia, responsavel) = await AgenciaAsync(tenant, cnpjAgencia, email);
        var vigente = await ClienteAsync(tenant, "V8935", "Cliente Vigente", ProspeccaoCarteiraTests.Cnpj(tenant, 1));
        var expirado = await ClienteAsync(tenant, "E8935", "Cliente Expirado", ProspeccaoCarteiraTests.Cnpj(tenant, 2));
        await ContratoAsync(agencia, vigente, DateTime.UtcNow.AddYears(1));
        await ContratoAsync(agencia, expirado, DateTime.UtcNow.AddDays(-1));
        await CampanhaAsync("MC8935V", agencia, responsavel, vigente, StatusCampanhaEnum.PedidoEmAberto, 50_000m);
        await CampanhaAsync("MC8935E", agencia, responsavel, expirado, StatusCampanhaEnum.Criada, 50_000m);
        await CampanhaAsync("MC8935C", agencia, responsavel, vigente, StatusCampanhaEnum.Confirmada, 50_000m);
        var comPedido = await CampanhaAsync("MC8935P", agencia, responsavel, vigente, StatusCampanhaEnum.PedidoEmAberto, 50_000m);
        var pedido = await PedidoAsync(comPedido, "PMC8935A", StatusPedidoEnum.Solicitado);
        await ItemAsync(pedido, peca, StatusPedidoItemEnum.Solicitado, 30_000m);

        await Seed.AnuncianteAsync(tenant, email, cnpjAgencia);
        await AprovarAsync(tenant, email, "ag", cnpjAgencia, agencia);
        using var factory = new WlApiFactory(_db, tenant);
        using var app = await LoginAsync(factory, email);

        // GET /clients e Minhas Campanhas usam o mesmo conjunto: o expirado sai dos dois.
        var clientes = await JsonAsync(app, "/api/wl/app/clients");
        clientes.GetProperty("perfil").GetString().Should().Be("agencia");
        clientes.GetProperty("podeAdicionarAnunciante").GetBoolean().Should().BeFalse();
        clientes.GetProperty("clientes").EnumerateArray().Select(c => c.GetProperty("id").GetInt32()).Should().Equal(vigente);
        var lista = await JsonAsync(app, "/api/wl/app/campaigns");
        lista.EnumerateArray().Select(c => c.GetProperty("codigo").GetString())
            .Should().BeEquivalentTo(new[] { "MC8935V", "MC8935C", "MC8935P" });
        (await app.GetAsync($"/api/wl/app/campaigns?clienteId={expirado}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await JsonAsync(app, $"/api/wl/app/campaigns?clienteId={vigente}")).GetArrayLength().Should().Be(3);
        (await app.GetAsync("/api/wl/app/campaigns/MC8935E")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await app.PutAsJsonAsync("/api/wl/app/campaigns/MC8935E", Payload(50_000m))).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "fora da carteira é 404 antes de olhar status");
        // O contexto da compra oferece o mesmo conjunto de clientes.
        (await JsonAsync(app, "/api/wl/app/checkout/context")).GetProperty("clients").EnumerateArray()
            .Select(c => c.GetProperty("id").GetInt32()).Should().Equal(vigente);

        // Ordem do PUT: 409 para não rascunho, 400 com details para dado inválido.
        (await JsonAsync(app, "/api/wl/app/campaigns/MC8935V")).GetProperty("podeEditar").GetBoolean().Should().BeTrue();
        (await app.PutAsJsonAsync("/api/wl/app/campaigns/MC8935C", Payload(50_000m))).StatusCode.Should().Be(HttpStatusCode.Conflict);
        await DeveSer400ComDetails(await app.PutAsJsonAsync("/api/wl/app/campaigns/MC8935V", Payload(1_500m)));
        await DeveSer400ComDetails(await app.PutAsJsonAsync("/api/wl/app/campaigns/MC8935V", Payload(50_000m, nome: "")));
        await DeveSer400ComDetails(await app.PutAsJsonAsync("/api/wl/app/campaigns/MC8935P", Payload(20_000m)),
            "comprometido", "verba abaixo dos R$ 30.000 já pedidos nesta exibidora");
        await DeveSer400ComDetails(await app.PutAsJsonAsync("/api/wl/app/campaigns/MC8935P",
            Payload(40_000m, inicio: DateTime.UtcNow.Date.AddDays(10))), "período", "o período deixaria o pedido de fora");

        var ok = await app.PutAsJsonAsync("/api/wl/app/campaigns/MC8935V",
            Payload(80_000m, nome: "Campanha Editada", fim: DateTime.UtcNow.Date.AddDays(40)));
        ok.StatusCode.Should().Be(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());
        using (var ctx = new VeiculandoDataContext())
        {
            var salva = await ctx.Campanhas.AsNoTracking().SingleAsync(c => c.Codigo == "MC8935V");
            salva.Nome.Should().Be("Campanha Editada");
            salva.Verba.Should().Be(80_000m);
            salva.DataFimPrevisto.Should().Be(DateTime.UtcNow.Date.AddDays(40));
            salva.IdCliente.Should().Be(vigente, "a edição não troca o cliente");
        }

        // O POST segue a mesma validação: verba fora da faixa do modal é 400 com details.
        await DeveSer400ComDetails(await app.PostAsJsonAsync("/api/wl/app/checkout/context/campaigns",
            new { name = "Nova", product = "Produto", startDate = DateTime.UtcNow.Date.AddDays(1),
                endDate = DateTime.UtcNow.Date.AddDays(8), budget = 500m, clientId = vigente }));
    }

    [Fact]
    public async Task Prospeccao_ve_so_clientes_liberados_e_perde_a_campanha_quando_a_liberacao_cai()
    {
        const int tenant = 8936;
        await ProspeccaoCarteiraTests.PrepararAfiliadaAsync(tenant);
        using var factory = new WlApiFactory(_db, tenant);
        var (app, _) = await ProspeccaoCarteiraTests.AbrirCarteiraAsync(factory, tenant, "mc-ana");
        var ids = new int[2];
        for (var n = 0; n < 2; n++)
        {
            var c = await ProspeccaoCarteiraTests.CadastrarAsync(app, ProspeccaoCarteiraTests.Cnpj(tenant, n + 1));
            await ProspeccaoCarteiraTests.EnviarComDocumentosAsync(app, c.Id);
            await ProspeccaoCarteiraTests.AprovarAsync(tenant, c.Id);
            ids[n] = c.ClienteId;
            var criada = await app.PostAsJsonAsync("/api/wl/app/checkout/context/campaigns", new
            {
                name = $"Campanha {n}", product = "Pão", startDate = DateTime.UtcNow.Date.AddDays(1),
                endDate = DateTime.UtcNow.Date.AddDays(30), budget = 1000m, clientId = c.ClienteId
            });
            criada.StatusCode.Should().Be(HttpStatusCode.Created, await criada.Content.ReadAsStringAsync());
        }

        // Campanha da exibidora da casa, com o mesmo responsável: não pode aparecer.
        string codigoCasa;
        using (var ctx = new VeiculandoDataContext())
        {
            var primeiro = ids[0];
            var modelo = await ctx.Campanhas.AsNoTracking().FirstAsync(c => c.IdCliente == primeiro);
            var emailDaCasa = AnuncianteDaCasaProvisionamento.EmailDe(tenant);
            var casa = await ctx.WlUsuariosAnunciante.SingleAsync(u => u.AfiliadaId == tenant && u.Email.Endereco == emailDaCasa);
            casa.ClienteId.Should().NotBeNull();
            codigoCasa = "MC8936H";
            await CampanhaAsync(codigoCasa, modelo.IdAgencia, modelo.IdUsuarioAnunciante, casa.ClienteId!.Value,
                StatusCampanhaEnum.Criada, null);
        }

        var clientes = await JsonAsync(app, "/api/wl/app/clients");
        clientes.GetProperty("perfil").GetString().Should().Be("carteira");
        clientes.GetProperty("clientes").EnumerateArray().Select(c => c.GetProperty("id").GetInt32())
            .Should().BeEquivalentTo(ids, "só a carteira liberada; a casa não entra");
        var lista = await JsonAsync(app, "/api/wl/app/campaigns");
        lista.GetArrayLength().Should().Be(2);
        lista.EnumerateArray().Should().NotContain(c => c.GetProperty("codigo").GetString() == codigoCasa);
        lista.EnumerateArray().Select(c => c.GetProperty("anunciante").GetProperty("id").GetInt32())
            .Should().BeEquivalentTo(ids);
        (await app.GetAsync($"/api/wl/app/campaigns/{codigoCasa}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var revogada = lista.EnumerateArray().Single(c => c.GetProperty("anunciante").GetProperty("id").GetInt32() == ids[1])
            .GetProperty("codigo").GetString();
        (await app.GetAsync($"/api/wl/app/campaigns/{revogada}")).StatusCode.Should().Be(HttpStatusCode.OK);

        // Liberação cai (contrato com a venda direta expira): sai do /clients e a campanha dá 404.
        using (var ctx = new VeiculandoDataContext())
            await ctx.Database.ExecuteSqlCommandAsync(
                "UPDATE dbo.AgenciaCliente SET DataExpiracaoContrato = DATEADD(day, -1, GETUTCDATE()) WHERE IdCliente = @p0", ids[1]);
        (await JsonAsync(app, "/api/wl/app/clients")).GetProperty("clientes").EnumerateArray()
            .Select(c => c.GetProperty("id").GetInt32()).Should().Equal(ids[0]);
        (await app.GetAsync($"/api/wl/app/campaigns/{revogada}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await app.PutAsJsonAsync($"/api/wl/app/campaigns/{revogada}", Payload(1_000m))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await JsonAsync(app, "/api/wl/app/campaigns")).GetArrayLength().Should().Be(1);
        (await app.GetAsync($"/api/wl/app/campaigns?clienteId={ids[1]}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- apoio ----

    private static object Payload(decimal? verba, string nome = "Campanha Teste", DateTime? inicio = null, DateTime? fim = null) => new
    {
        name = nome, product = "Produto", job = "Job",
        startDate = inicio ?? DateTime.UtcNow.Date.AddDays(1), endDate = fim ?? DateTime.UtcNow.Date.AddDays(30), budget = verba
    };

    private static async Task DeveSer400ComDetails(HttpResponseMessage r, string trecho = null, string porque = "")
    {
        var corpo = await r.Content.ReadAsStringAsync();
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest, corpo);
        var json = JsonDocument.Parse(corpo).RootElement;
        json.GetProperty("message").GetString().Should().NotBeNullOrEmpty();
        var details = json.GetProperty("details").EnumerateArray().Select(d => d.GetString()).ToList();
        details.Should().NotBeEmpty(porque);
        if (trecho != null) details.Should().Contain(d => d.Contains(trecho), porque);
    }

    private static string Mascara(string c) => $"{c[..2]}.{c[2..5]}.{c[5..8]}/{c[8..12]}-{c[12..]}";

    private static async Task<JsonElement> JsonAsync(HttpClient app, string url)
    {
        var r = await app.GetAsync(url);
        r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<HttpClient> LoginAsync(WlApiFactory factory, string email)
    {
        var client = factory.ClienteAnonimo();
        var response = await client.PostAsJsonAsync("/api/wl/app/auth/login", new { email, password = Seed.SenhaPadrao });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var login = await response.Content.ReadFromJsonAsync<LoginResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.Token);
        return client;
    }

    private static async Task AprovarAsync(int tenant, string email, string tipo, string documento, int? agenciaId = null)
    {
        using var ctx = new VeiculandoDataContext();
        var user = await ctx.WlUsuariosAnunciante.SingleAsync(u => u.AfiliadaId == tenant && u.Email.Endereco == email);
        var onboarding = new WlAppOnboarding(user);
        onboarding.SalvarRascunho(tipo, documento, "{}", 4);
        onboarding.ReivindicarDocumento(documento);
        onboarding.Enviar();
        onboarding.Transicionar(WlAppKycStatus.EmAnalise, null);
        onboarding.Transicionar(WlAppKycStatus.Aprovado, null);
        ctx.WlAppOnboardings.Add(onboarding);
        if (agenciaId.HasValue) user.VincularIdentidadeComercial(null, agenciaId.Value, documento);
        await ctx.SaveChangesAsync();
    }

    /// <summary>Agência com vínculo ativo na exibidora e o usuário Core responsável (mesmo e-mail do App).</summary>
    private static async Task<(int Agencia, int Responsavel)> AgenciaAsync(int tenant, string cnpj, string email)
    {
        using var ctx = new VeiculandoDataContext();
        var agencia = await ctx.Database.SqlQuery<int>(@"
INSERT INTO Agencia (Nome, Cnpj, Email, AvaliacaoMedia, BonificacaoVolume, DataCadastro, DataAtualizacao, StatusExibicao)
VALUES (@p0, @p1, @p2, 0, 0, GETDATE(), GETDATE(), 1);
SELECT CAST(SCOPE_IDENTITY() AS int);", $"Agencia {tenant}", cnpj, email).SingleAsync();
        var usuario = await ctx.Database.SqlQuery<int>(@"
INSERT INTO Usuario (Nome, Email, Senha, StatusAprovacao, Acessos, DataUltimoLogin, EmailConfirmado,
                     DataCadastro, DataAtualizacao, StatusExibicao, IdPerfil)
VALUES (@p0, @p1, 'x', 1, 0, GETDATE(), 1, GETDATE(), GETDATE(), 1, 1);
DECLARE @id int = CAST(SCOPE_IDENTITY() AS int);
INSERT INTO UsuarioAnunciante (Id, IdAgencia) VALUES (@id, @p2);
SELECT @id;", $"Responsavel {tenant}", email, agencia).SingleAsync();
        var vinculo = new AfiliadaAgencia(await ctx.Afiliadas.SingleAsync(a => a.Id == tenant),
            await ctx.Agencias.SingleAsync(a => a.Id == agencia), await ctx.Usuarios.SingleAsync(u => u.Id == 1));
        ctx.AfiliadaAgencias.Add(vinculo);
        await ctx.SaveChangesAsync();
        return (agencia, usuario);
    }

    private static async Task<int> ClienteAsync(int tenant, string codigo, string nome, string cnpj)
    {
        using var ctx = new VeiculandoDataContext();
        return await ctx.Database.SqlQuery<int>(@"
INSERT INTO Cliente (Codigo, Nome, Cnpj, DescontoNegociado, Status, DataCadastro, DataAtualizacao, StatusExibicao)
VALUES (@p0, @p1, @p2, 0, 1, GETDATE(), GETDATE(), 1);
DECLARE @id int = CAST(SCOPE_IDENTITY() AS int);
INSERT INTO AfiliadaCliente (IdAfiliada, IdCliente, FonteOrigem, Status, DataVinculo, DataCadastro, DataAtualizacao, StatusExibicao)
VALUES (@p3, @id, 1, 1, GETUTCDATE(), GETUTCDATE(), GETUTCDATE(), 1);
SELECT @id;", codigo, nome, cnpj, tenant).SingleAsync();
    }

    private static async Task ContratoAsync(int agencia, int cliente, DateTime expiracao)
    {
        using var ctx = new VeiculandoDataContext();
        await ctx.Database.ExecuteSqlCommandAsync(@"
INSERT AgenciaCliente (IdAgencia, IdCliente, ComissaoAgencia, DataInicioContrato, DataExpiracaoContrato,
    DataCadastro, DataAtualizacao, StatusExibicao)
VALUES (@p0, @p1, 0, DATEADD(year, -1, GETUTCDATE()), @p2, GETUTCDATE(), GETUTCDATE(), 1);", agencia, cliente, expiracao);
    }

    private static async Task<int> CampanhaAsync(string codigo, int agencia, int responsavel, int cliente,
        StatusCampanhaEnum status, decimal? verba)
    {
        using var ctx = new VeiculandoDataContext();
        return await ctx.Database.SqlQuery<int>(@"
INSERT INTO Campanha (FonteOrigem, IdAgencia, IdUsuarioAnunciante, IdCliente, Nome, Codigo, Produto, Status,
                      DataInicioPrevisto, DataFimPrevisto, Verba, DescontoNegociado, PermiteConviteAvaliacao,
                      DataCadastro, DataAtualizacao, StatusExibicao)
VALUES (0, @p0, @p1, @p2, @p3, @p3, 'Produto', @p4, CAST(GETUTCDATE() AS date), DATEADD(day, 30, CAST(GETUTCDATE() AS date)),
        @p5, 0, 0, GETDATE(), GETDATE(), 1);
SELECT CAST(SCOPE_IDENTITY() AS int);", agencia, responsavel, cliente, codigo, (int)status,
            (object)verba ?? DBNull.Value).SingleAsync();
    }

    /// <summary>Pedido no período 1 do seed (hoje a hoje+14).</summary>
    private static async Task<int> PedidoAsync(int campanha, string codigo, StatusPedidoEnum status)
    {
        using var ctx = new VeiculandoDataContext();
        return await ctx.Database.SqlQuery<int>(@"
INSERT INTO Pedido (FonteOrigem, IdCampanha, IdCidade, IdPeriodo, IdUsuarioAnunciante, Codigo, Status, StatusPagamento, Revisao,
                    DescontoNegociado, DescontoFinanceiro, ComissaoAgencia, BonificacaoVolume, ComissaoVeiculando,
                    ValorTotalTabela, ValorTotalBruto, ValorDescontoNegociado, ValorDescontoFinanceiro, ValorComissaoAgencia,
                    ValorBonificacaoVolume, ValorComissaoVeiculando, ValorLiquidoVeiculacao, ValorLiquidoAnunciante,
                    DataCadastro, DataAtualizacao, StatusExibicao)
VALUES (0, @p0, 1, 1, 1, @p1, @p2, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, GETDATE(), GETDATE(), 1);
SELECT CAST(SCOPE_IDENTITY() AS int);", campanha, codigo, (int)status).SingleAsync();
    }

    private static async Task ItemAsync(int pedido, int peca, StatusPedidoItemEnum status, decimal valor)
    {
        using var ctx = new VeiculandoDataContext();
        await ctx.Database.ExecuteSqlCommandAsync(@"
INSERT INTO PedidoItem (IdPedido, IdPeca, IdPeriodo, Status, DescontoNegociado, ValorTabela, ValorBruto, ValorDescontoNegociado,
                        ValorDescontoFinanceiro, ValorComissaoAgencia, ValorBonificacaoVolume, ValorComissaoVeiculando,
                        ValorLiquidoVeiculacao, ValorLiquidoAnunciante)
VALUES (@p0, @p1, 1, @p2, 0, @p3, @p3, 0, 0, 0, 0, 0, @p3, @p3);", pedido, peca, (int)status, valor);
    }

    private sealed record LoginResponse(string Token);
}
