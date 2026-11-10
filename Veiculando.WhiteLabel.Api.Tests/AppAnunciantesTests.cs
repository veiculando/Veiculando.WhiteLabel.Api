using System;
using System.Data.Entity;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities.WhiteLabel;
using Veiculando.Domain.Enums;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;
using M = Veiculando.WhiteLabel.Api.Tests.AppMinhasCampanhasEscopoTests;

namespace Veiculando.WhiteLabel.Api.Tests;

/// <summary>
/// "Adicionar anunciante" da agência no App (VEI-RD-67d/67g): pedido de representação com o
/// desenho da carteira da vendedora (decisão do PO, 10/11).
/// </summary>
[Collection(DatabaseCollection.Nome)]
public sealed class AppAnunciantesTests
{
    private const string Rota = "/api/wl/app/anunciantes";
    private readonly SqlServerFixture _db;
    public AppAnunciantesTests(SqlServerFixture db) => _db = db;

    [Fact]
    public async Task Agencia_cadastra_anunciante_novo_anexa_os_tres_documentos_e_envia()
    {
        const int tenant = 8937;
        var (app, factory, _) = await AgenciaLogadaAsync(tenant, "an-ag-8937@exemplo.com", 9);
        using var _f = factory;
        var cnpj = ProspeccaoCarteiraTests.Cnpj(tenant, 1);

        (await M.JsonAsync(app, "/api/wl/app/clients")).GetProperty("podeAdicionarAnunciante").GetBoolean().Should().BeTrue();
        (await M.JsonAsync(app, $"{Rota}/cnpj/{cnpj}")).GetProperty("situacao").GetString().Should().Be("novo");

        // Comissão fora da faixa e cliente novo sem dados: 400 antes de gravar.
        (await app.PostAsJsonAsync(Rota, Pedido(cnpj, comissao: 31m))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await app.PostAsJsonAsync(Rota, Pedido(cnpj, comissao: 12.5m))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await app.PostAsJsonAsync(Rota, new { cnpj, comissaoAgencia = 10m })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var criado = await CriarAsync(app, Pedido(cnpj, comissao: 12m));
        criado.GetProperty("status").GetString().Should().Be("Rascunho");
        criado.GetProperty("origem").GetString().Should().Be("Cadastro");
        criado.GetProperty("comissaoAgencia").GetDecimal().Should().Be(12m);
        var id = criado.GetProperty("id").GetGuid();
        var clienteId = criado.GetProperty("clienteId").GetInt32();

        using (var ctx = new VeiculandoDataContext())
        {
            var vinculo = await ctx.AfiliadaClientes.SingleAsync(v => v.IdAfiliada == tenant && v.IdCliente == clienteId);
            vinculo.Status.Should().NotBe(StatusVinculoEnum.Ativo, "só a aprovação da exibidora ativa o cliente novo");
        }

        // Sem a carta não envia; com os dois do cliente e sem a carta também não.
        (await app.PostAsync($"{Rota}/{id}/enviar", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        await AnexarOkAsync(app, id, WlRepresentacaoDocumento.TipoContratoSocial);
        await AnexarOkAsync(app, id, WlRepresentacaoDocumento.TipoResponsavel);
        var semCarta = await app.PostAsync($"{Rota}/{id}/enviar", null);
        semCarta.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await semCarta.Content.ReadAsStringAsync()).Should().Contain("carta de representação");
        (await AnexarAsync(app, id, "qualquer")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AnexarOkAsync(app, id, WlRepresentacaoDocumento.TipoCartaRepresentacao);

        var condicoes = await app.PutAsJsonAsync($"{Rota}/{id}/condicoes", new { comissaoAgencia = 15m });
        condicoes.StatusCode.Should().Be(HttpStatusCode.OK, await condicoes.Content.ReadAsStringAsync());

        var enviado = await app.PostAsync($"{Rota}/{id}/enviar", null);
        enviado.StatusCode.Should().Be(HttpStatusCode.OK, await enviado.Content.ReadAsStringAsync());

        // Em análise: não altera, não anexa, não duplica.
        (await app.PutAsJsonAsync($"{Rota}/{id}/condicoes", new { comissaoAgencia = 5m })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await AnexarAsync(app, id, WlRepresentacaoDocumento.TipoCartaRepresentacao)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var repetido = await app.PostAsJsonAsync(Rota, Pedido(cnpj));
        repetido.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await repetido.Content.ReadAsStringAsync()).Should().Contain("pending_request");
        var consulta = await M.JsonAsync(app, $"{Rota}/cnpj/{cnpj}");
        consulta.GetProperty("situacao").GetString().Should().Be("pedido");
        consulta.GetProperty("status").GetString().Should().Be("PendenteAnalise");

        var lista = await M.JsonAsync(app, Rota);
        var item = lista.EnumerateArray().Single();
        item.GetProperty("status").GetString().Should().Be("PendenteAnalise");
        item.GetProperty("comissaoAgencia").GetDecimal().Should().Be(15m);
        item.GetProperty("cnpj").GetString().Should().Be(cnpj);
        item.GetProperty("documentos").GetArrayLength().Should().Be(3);

        // Antes da aprovação o cliente não entra na carteira da agência.
        (await M.JsonAsync(app, "/api/wl/app/clients")).GetProperty("clientes").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Cliente_existente_vira_solicitacao_e_contrato_vigente_bloqueia_o_pedido()
    {
        const int tenant = 8938;
        var (app, factory, agencia) = await AgenciaLogadaAsync(tenant, "an-ag-8938@exemplo.com", 9);
        using var _f = factory;
        var cnpjRepresentado = ProspeccaoCarteiraTests.Cnpj(tenant, 1);
        var cnpjDaCasa = ProspeccaoCarteiraTests.Cnpj(tenant, 2);
        var representado = await M.ClienteAsync(tenant, "R8938", "Cliente Representado", Mascara(cnpjRepresentado));
        await M.ClienteAsync(tenant, "C8938", "Cliente da Exibidora", Mascara(cnpjDaCasa));
        await M.ContratoAsync(agencia, representado, DateTime.UtcNow.AddYears(1));

        (await M.JsonAsync(app, $"{Rota}/cnpj/{cnpjRepresentado}")).GetProperty("situacao").GetString().Should().Be("representado");
        var dup = await app.PostAsJsonAsync(Rota, new { cnpj = cnpjRepresentado, comissaoAgencia = 10m });
        dup.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await dup.Content.ReadAsStringAsync()).Should().Contain("already_represented");

        // CNPJ gravado com máscara no Core é achado pelos dígitos.
        var existente = await M.JsonAsync(app, $"{Rota}/cnpj/{cnpjDaCasa}");
        existente.GetProperty("situacao").GetString().Should().Be("existente");
        existente.GetProperty("nome").GetString().Should().Be("Cliente da Exibidora");
        existente.GetProperty("documentosDispensaveis").GetBoolean().Should().BeFalse();

        // Cliente existente: só CNPJ, comissão e vigência.
        var criado = await CriarAsync(app, new { cnpj = cnpjDaCasa, comissaoAgencia = 10m });
        criado.GetProperty("origem").GetString().Should().Be("Solicitacao");
        var fim = criado.GetProperty("fimContrato").GetDateTime();
        fim.Should().Be(DateTime.UtcNow.Date.AddYears(2), "vigência padrão de 2 anos");
        var id = criado.GetProperty("id").GetGuid();

        // Sem KYC aprovado do cliente, a solicitação também exige os documentos do cliente.
        await AnexarOkAsync(app, id, WlRepresentacaoDocumento.TipoCartaRepresentacao);
        (await app.PostAsync($"{Rota}/{id}/enviar", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Outra agência da mesma exibidora não enxerga o pedido.
        var (outra, outraFactory, _) = await AgenciaLogadaAsync(tenant, "an-ag2-8938@exemplo.com", 8);
        using var _o = outraFactory;
        (await outra.PostAsync($"{Rota}/{id}/enviar", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await M.JsonAsync(outra, Rota)).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Sem_KYC_aprovado_e_recusado()
    {
        const int tenant = 8938;
        const string email = "an-semkyc-8938@exemplo.com";
        await Seed.AnuncianteAsync(tenant, email, ProspeccaoCarteiraTests.Cnpj(tenant, 7));
        using var factory = new WlApiFactory(_db, tenant);
        var app = await M.LoginAsync(factory, email);
        (await app.GetAsync(Rota)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await app.PostAsJsonAsync(Rota, Pedido(ProspeccaoCarteiraTests.Cnpj(tenant, 3)))).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Exibidora_aprova_ajusta_reprova_e_renova_contrato_vencido()
    {
        const int tenant = 8939;
        var (app, factory, agencia) = await AgenciaLogadaAsync(tenant, "an-ag-8939@exemplo.com", 9);
        using var _f = factory;
        await Seed.OperadorAsync(tenant, "gestor-8939@exemplo.com", new[] { "ClienteGerenciar" }, nome: "Gestora");
        var gestor = await factory.ClienteAutenticadoAsync("gestor-8939@exemplo.com", Seed.SenhaPadrao);
        await Seed.OperadorAsync(tenant, "sem-perm-8939@exemplo.com", new[] { "PedidoCriar" });
        var semPermissao = await factory.ClienteAutenticadoAsync("sem-perm-8939@exemplo.com", Seed.SenhaPadrao);
        const string Fila = "/api/wl/kyc/app/representacoes";

        // 1) Cliente novo: aprovar cria o contrato com a comissão e a vigência do pedido.
        var novo = await EnviadoAsync(app, Pedido(ProspeccaoCarteiraTests.Cnpj(tenant, 1), comissao: 12m), todos: true);
        (await semPermissao.GetAsync(Fila)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var fila = await M.JsonAsync(gestor, Fila);
        var item = fila.EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == novo);
        item.GetProperty("agencia").GetString().Should().Be($"Agencia {tenant}");
        item.GetProperty("comissaoAgencia").GetDecimal().Should().Be(12m);
        var detalhe = await M.JsonAsync(gestor, $"{Fila}/{novo}");
        detalhe.GetProperty("solicitante").GetString().Should().Be("an-ag-8939@exemplo.com");
        var docs = detalhe.GetProperty("documentos").EnumerateArray().ToList();
        docs.Should().HaveCount(3);
        var download = await gestor.GetAsync($"{Fila}/{novo}/documentos/{docs[0].GetProperty("id").GetGuid()}");
        download.StatusCode.Should().Be(HttpStatusCode.OK);

        var aprovado = await gestor.PostAsync($"{Fila}/{novo}/aprovar", null);
        aprovado.StatusCode.Should().Be(HttpStatusCode.OK, await aprovado.Content.ReadAsStringAsync());
        (await gestor.PostAsync($"{Fila}/{novo}/aprovar", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var clienteNovo = detalhe.GetProperty("clienteId").GetInt32();
        using (var ctx = new VeiculandoDataContext())
        {
            var contrato = await ctx.AgenciaClientes.AsNoTracking().SingleAsync(l => l.IdAgencia == agencia && l.IdCliente == clienteNovo);
            contrato.ComissaoAgencia.Should().Be(0.12m, "o Core guarda fração");
            contrato.DataInicioContrato.Should().Be(DateTime.UtcNow.Date);
            contrato.DataExpiracaoContrato.Should().Be(DateTime.UtcNow.Date.AddYears(2));
            (await ctx.AfiliadaClientes.AsNoTracking().SingleAsync(v => v.IdAfiliada == tenant && v.IdCliente == clienteNovo))
                .Status.Should().Be(StatusVinculoEnum.Ativo);
        }
        // Aprovado, o cliente entra na carteira da agência e o CNPJ aparece como representado.
        (await M.JsonAsync(app, "/api/wl/app/clients")).GetProperty("clientes").EnumerateArray()
            .Select(c => c.GetProperty("id").GetInt32()).Should().Contain(clienteNovo);
        (await M.JsonAsync(app, $"{Rota}/cnpj/{ProspeccaoCarteiraTests.Cnpj(tenant, 1)}")).GetProperty("situacao").GetString()
            .Should().Be("representado");

        // 2) Ajustes com motivo voltam para a agência; reprovação de cadastro novo inativa o cliente.
        var outro = await EnviadoAsync(app, Pedido(ProspeccaoCarteiraTests.Cnpj(tenant, 2)), todos: true);
        (await gestor.PostAsJsonAsync($"{Fila}/{outro}/ajustes", new { reason = " " })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await gestor.PostAsJsonAsync($"{Fila}/{outro}/ajustes", new { reason = "Carta sem assinatura" })).StatusCode.Should().Be(HttpStatusCode.OK);
        var meu = (await M.JsonAsync(app, Rota)).EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == outro);
        meu.GetProperty("status").GetString().Should().Be("AjustesSolicitados");
        meu.GetProperty("motivo").GetString().Should().Be("Carta sem assinatura");
        (await gestor.PostAsJsonAsync($"{Fila}/{outro}/reprovar", new { reason = "CNPJ não confere" })).StatusCode.Should().Be(HttpStatusCode.OK);
        var clienteReprovado = meu.GetProperty("clienteId").GetInt32();
        using (var ctx = new VeiculandoDataContext())
            (await ctx.Clientes.AsNoTracking().SingleAsync(c => c.Id == clienteReprovado)).StatusExibicao
                .Should().Be(StatusExibicaoEnum.Inativo, "o cliente só existia por este pedido");

        // 3) Contrato vencido: a agência pede de novo e a aprovação renova as condições.
        var cnpjVencido = ProspeccaoCarteiraTests.Cnpj(tenant, 3);
        var vencido = await M.ClienteAsync(tenant, "V8939", "Cliente Vencido", cnpjVencido);
        await M.ContratoAsync(agencia, vencido, DateTime.UtcNow.AddDays(-1));
        var renovacao = await EnviadoAsync(app, new { cnpj = cnpjVencido, comissaoAgencia = 7m }, todos: true);
        (await gestor.PostAsync($"{Fila}/{renovacao}/aprovar", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        using (var ctx = new VeiculandoDataContext())
        {
            var contrato = await ctx.AgenciaClientes.AsNoTracking().SingleAsync(l => l.IdAgencia == agencia && l.IdCliente == vencido);
            contrato.ComissaoAgencia.Should().Be(0.07m);
            contrato.DataExpiracaoContrato.Should().Be(DateTime.UtcNow.Date.AddYears(2));
        }
    }

    private static async Task<Guid> EnviadoAsync(HttpClient app, object pedido, bool todos)
    {
        var id = (await CriarAsync(app, pedido)).GetProperty("id").GetGuid();
        if (todos)
        {
            await AnexarOkAsync(app, id, WlRepresentacaoDocumento.TipoContratoSocial);
            await AnexarOkAsync(app, id, WlRepresentacaoDocumento.TipoResponsavel);
        }
        await AnexarOkAsync(app, id, WlRepresentacaoDocumento.TipoCartaRepresentacao);
        var r = await app.PostAsync($"{Rota}/{id}/enviar", null);
        r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        return id;
    }

    // ---- apoio ----

    private async Task<(HttpClient App, WlApiFactory Factory, int Agencia)> AgenciaLogadaAsync(int tenant, string email, int n)
    {
        var cnpjAgencia = ProspeccaoCarteiraTests.Cnpj(tenant, n);
        // Cria o grafo de apoio (perfil, usuário Core 1) de que o AgenciaAsync depende.
        var peca = await Seed.PecaAsync(await Seed.LocalAsync(tenant, $"AN{tenant}{n}"), $"P-AN-{tenant}-{n}");
        await Seed.ReservaAsync(tenant, $"AN-{tenant}-{n}", peca);
        await Seed.ServicoCoreAsync(tenant);
        var (agencia, _) = await M.AgenciaAsync(tenant, cnpjAgencia, email);
        await Seed.AnuncianteAsync(tenant, email, cnpjAgencia);
        await M.AprovarAsync(tenant, email, "ag", cnpjAgencia, agencia);
        var factory = new WlApiFactory(_db, tenant);
        return (await M.LoginAsync(factory, email), factory, agencia);
    }

    private static object Pedido(string cnpj, decimal comissao = 10m) => new
    {
        cnpj, nomeFantasia = "Padaria Teste", razaoSocial = "Padaria Teste Ltda", telefone = "(12) 3333-4444",
        logradouro = "Rua A", numero = "10", bairro = "Centro", cep = "12000-000", cidade = "Taubaté", uf = "SP",
        comissaoAgencia = comissao
    };

    private static async Task<JsonElement> CriarAsync(HttpClient app, object pedido)
    {
        var r = await app.PostAsJsonAsync(Rota, pedido);
        r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<HttpResponseMessage> AnexarAsync(HttpClient app, Guid id, string tipo)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(tipo), "type");
        var pdf = new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<<>>\nendobj"));
        pdf.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(pdf, "file", tipo + ".pdf");
        return await app.PostAsync($"{Rota}/{id}/documentos", form);
    }

    private static async Task AnexarOkAsync(HttpClient app, Guid id, string tipo)
    {
        var r = await AnexarAsync(app, id, tipo);
        r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
    }

    private static string Mascara(string c) => $"{c[..2]}.{c[2..5]}.{c[5..8]}/{c[8..12]}-{c[12..]}";
}
