using System;
using System.Data.Entity;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities.OrdensServico;
using Veiculando.Domain.Enums;
using Veiculando.WhiteLabel.Api.Services;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests;

// VEI-RD-141d. Checking de campo: arquivo no storage privado WL, regra do Core.
[Collection(DatabaseCollection.Nome)]
public class CampoCheckingTests
{
    private const int Afiliada = 9873;
    private static readonly DateTimeOffset Hora = new(2026, 10, 28, 16, 4, 59, TimeSpan.FromHours(-3));
    private readonly SqlServerFixture _db;
    public CampoCheckingTests(SqlServerFixture db) => _db = db;

    private async Task<(WlApiFactory Factory, HttpClient Client, int Operador, int Peca)> Preparar(string sufixo)
    {
        var local = await Seed.LocalAsync(Afiliada, "LC" + sufixo);
        var peca = await Seed.PecaAsync(local, "PC" + sufixo);
        using var ctx = new VeiculandoDataContext();
        var email = $"checking-{sufixo}@exemplo.com";
        await ctx.Database.ExecuteSqlCommandAsync(@"
INSERT Usuario (Nome, Email, Senha, StatusAprovacao, Acessos, DataUltimoLogin,
 EmailConfirmado, DataCadastro, DataAtualizacao, StatusExibicao, IdPerfil)
VALUES ('Campo', @p0, @p1, 1, 0, GETUTCDATE(), 1, GETUTCDATE(), GETUTCDATE(), 1, 1);
INSERT Usuario_Operador (Id, IdAfiliada, Colador, Fiscal) VALUES (SCOPE_IDENTITY(), @p2, 1, 1);",
            email, SenhaUsuarioCore.Hash(Seed.SenhaPadrao), Afiliada);
        var op = await ctx.Database.SqlQuery<int>("SELECT Id FROM Usuario WHERE Email=@p0", email).SingleAsync();
        var factory = new WlApiFactory(_db, Afiliada);
        var client = factory.ClienteAnonimo();
        var login = await client.PostAsJsonAsync("/api/wl/campo/auth/login", new { email, senha = Seed.SenhaPadrao });
        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (factory, client, op, peca);
    }

    // Ordem iniciada no periodo 1; com colar, a peca ja tem a colagem marcada.
    private static async Task<int> Ordem(HttpClient client, int operador, int pecaId, bool colar = true)
    {
        await Seed.AfiliadaAsync(Afiliada);
        int id;
        using (var ctx = new VeiculandoDataContext())
        {
            var peca = await ctx.Pecas.Include(p => p.Local.Cidade).SingleAsync(p => p.Id == pecaId);
            var ordem = new OrdemServico(await ctx.Afiliadas.FindAsync(Afiliada), await ctx.Periodos.FindAsync(1),
                3000 + await ctx.OrdensServico.CountAsync(o => o.IdAfiliada == Afiliada),
                new[] { peca.Local.Cidade }, new[] { peca }, await ctx.Usuarios.FindAsync(1));
            ordem.DefinirAtribuidos(operador, operador);
            ctx.OrdensServico.Add(ordem);
            await ctx.SaveChangesAsync();
            id = ordem.Id;
        }
        var corpo = new { idCliente = Guid.NewGuid(), hora = Hora, latitude = -23.55, longitude = -46.63 };
        (await client.PostAsJsonAsync($"/api/wl/campo/ordens/{id}/iniciar", corpo)).EnsureSuccessStatusCode();
        if (colar)
            (await client.PostAsJsonAsync($"/api/wl/campo/ordens/{id}/pecas/{pecaId}/colagem",
                corpo with { idCliente = Guid.NewGuid() })).EnsureSuccessStatusCode();
        return id;
    }

    // PI da afiliada no periodo da ordem, ligado a peca por PecaPeriodoStatus, com o item de checking.
    private static async Task<int> ItemDeChecking(int pecaId, string codigo, bool comItem = true)
    {
        var piId = await Seed.InsercaoAsync(Afiliada, codigo, pecaId);
        using var ctx = new VeiculandoDataContext();
        await ctx.Database.ExecuteSqlCommandAsync(@"UPDATE PedidoItem SET IdPeriodo=1
            WHERE Id IN (SELECT IdPedidoItem FROM PedidoInsercaoItem WHERE IdPedidoInsercao=@p0);
            INSERT PecaPeriodoStatus (IdPeca, IdPeriodo, Status, IdPedidoInsercao) VALUES (@p1, 1, 0, @p0);",
            piId, pecaId);
        var pi = await ctx.PedidosInsercao.Include(x => x.Pedido)
            .Include(x => x.Itens.Select(i => i.PedidoItem.Peca.Local)).SingleAsync(x => x.Id == piId);
        if (!comItem) return pi.Itens.Single().IdPedidoItem;
        var user = await ctx.Usuarios.FindAsync(1);
        var checking = new Veiculando.Domain.Entities.Checking(pi, user);
        var item = new Veiculando.Domain.Entities.CheckingItem(checking, pi.Itens.Single(), user);
        ctx.Checkings.Add(checking);
        await ctx.SaveChangesAsync();
        return item.IdPedidoItem;
    }

    private static MultipartFormDataContent Foto(Guid idCliente, bool comPosicao = true)
    {
        var form = new MultipartFormDataContent();
        var bytes = new ByteArrayContent(Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jX1sAAAAASUVORK5CYII="));
        bytes.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(bytes, "foto", "foto.png");
        form.Add(new StringContent(idCliente.ToString()), "idCliente");
        form.Add(new StringContent(Hora.ToString("O", CultureInfo.InvariantCulture)), "hora");
        if (comPosicao)
        {
            form.Add(new StringContent("-23.55"), "latitude");
            form.Add(new StringContent("-46.63"), "longitude");
        }
        form.Add(new StringContent("Pixel 8"), "modelo");
        form.Add(new StringContent("Android 15"), "sistema");
        form.Add(new StringContent("1.0.3"), "versaoApp");
        return form;
    }

    private static async Task<string> Codigo(HttpResponseMessage resp)
    {
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString();
    }

    [Fact]
    public async Task Fiscal_grava_foto_no_storage_e_no_core_e_repeticao_devolve_o_mesmo_corpo()
    {
        var (factory, client, op, peca) = await Preparar("ok");
        using var f = factory; using var c = client;
        var id = await Ordem(client, op, peca);
        var idItem = await ItemDeChecking(peca, "CKOK");
        var idCliente = Guid.NewGuid();
        var rota = $"/api/wl/campo/ordens/{id}/pecas/{peca}/checking";

        using var form = Foto(idCliente);
        var resp = await client.PostAsync(rota, form);
        var texto = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK, texto);
        var dto = JsonDocument.Parse(texto).RootElement;
        dto.GetProperty("hora").GetDateTimeOffset().Should().Be(Hora);
        dto.GetProperty("ordem").GetProperty("estado").GetString()
            .Should().Be(StatusOrdemServicoEnum.Concluida.NomeNoCampo());

        var arquivo = factory.Uploads.Files.Should().ContainSingle().Subject;
        arquivo.Key.Should().MatchRegex($"^tenant-{Afiliada}/checking/{idItem}/wl-[a-f0-9]{{32}}\\.png$");
        arquivo.Value.Pending.Should().BeFalse("o pipeline confirma o arquivo depois do banco");
        using (var ctx = new VeiculandoDataContext())
        {
            var foto = await ctx.CheckingFotos.Include(x => x.CheckingItem).SingleAsync(x => x.IdClienteAcao == idCliente);
            foto.UrlArquivo.Should().Be(WlUploadReferences.Prefix + arquivo.Key);
            foto.IdPedidoItem.Should().Be(idItem);
            foto.DataCapturaAparelho.Should().Be(Hora);
            foto.ModeloAparelho.Should().Be("Pixel 8");
            foto.SistemaAparelho.Should().Be("Android 15");
            foto.VersaoApp.Should().Be("1.0.3");
            foto.IdOperadorCampo.Should().Be(op);
            foto.IdOrdemServicoCampo.Should().Be(id);
            foto.Geolocalizacao.Latitude.Should().Be(-23.55);
            foto.CheckingItem.Status.Should().Be(StatusCheckingItemEnum.Recebido);
            var ordem = await ctx.OrdensServico.Include(o => o.Pecas).SingleAsync(o => o.Id == id);
            ordem.Pecas.Single().DataFotoCampo.Should().Be(Hora);
            ordem.Status.Should().Be(StatusOrdemServicoEnum.Concluida);
        }

        using var repetido = Foto(idCliente);
        (await (await client.PostAsync(rota, repetido)).Content.ReadAsStringAsync()).Should().Be(texto);
        factory.Uploads.Files.Should().ContainSingle();
        using (var ctx = new VeiculandoDataContext())
            (await ctx.CheckingFotos.CountAsync(x => x.IdClienteAcao == idCliente)).Should().Be(1);
    }

    [Fact]
    public async Task Pre_condicoes_recusadas_nao_deixam_arquivo_nem_foto()
    {
        var (factory, client, op, peca) = await Preparar("rec");
        using var f = factory; using var c = client;

        var naoColada = await Ordem(client, op, peca, colar: false);
        await ItemDeChecking(peca, "CKREC");
        using (var form = Foto(Guid.NewGuid()))
            (await Codigo(await client.PostAsync($"/api/wl/campo/ordens/{naoColada}/pecas/{peca}/checking", form)))
                .Should().Be(RecusaCampo.PecaNaoColada);

        var semItemLocal = await Seed.LocalAsync(Afiliada, "LCREC2");
        var semItemPeca = await Seed.PecaAsync(semItemLocal, "PCREC2");
        var semItem = await Ordem(client, op, semItemPeca);
        await ItemDeChecking(semItemPeca, "CKREC2", comItem: false);
        using (var form = Foto(Guid.NewGuid()))
            (await Codigo(await client.PostAsync($"/api/wl/campo/ordens/{semItem}/pecas/{semItemPeca}/checking", form)))
                .Should().Be(RecusaCampo.CheckingIndisponivel);

        var historico = await Ordem(client, op, peca);
        using (var ctx = new VeiculandoDataContext())
        {
            (await ctx.OrdensServico.FindAsync(historico)).Desatribuir(op);
            await ctx.SaveChangesAsync();
        }
        using (var form = Foto(Guid.NewGuid()))
            (await Codigo(await client.PostAsync($"/api/wl/campo/ordens/{historico}/pecas/{peca}/checking", form)))
                .Should().Be(RecusaCampo.SomenteHistorico);

        factory.Uploads.Files.Should().BeEmpty();
        using var verifica = new VeiculandoDataContext();
        (await verifica.CheckingFotos.CountAsync(x => x.IdOperadorCampo == op)).Should().Be(0);
    }

    [Fact]
    public async Task Ordem_invisivel_e_404_corpo_invalido_e_400_e_id_reutilizado_e_recusado()
    {
        var (factory, client, op, peca) = await Preparar("inv");
        using var f = factory; using var c = client;
        var id = await Ordem(client, op, peca);
        await ItemDeChecking(peca, "CKINV");

        using (var form = Foto(Guid.NewGuid()))
            (await client.PostAsync($"/api/wl/campo/ordens/999999/pecas/{peca}/checking", form))
                .StatusCode.Should().Be(HttpStatusCode.NotFound);
        using (var form = Foto(Guid.NewGuid(), comPosicao: false))
            (await client.PostAsync($"/api/wl/campo/ordens/{id}/pecas/{peca}/checking", form))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        factory.Uploads.Files.Should().BeEmpty();

        // O UUID da colagem nao serve; o de um checking gravado so serve para a mesma peca e ordem.
        var idCliente = Guid.NewGuid();
        using (var form = Foto(idCliente))
            (await client.PostAsync($"/api/wl/campo/ordens/{id}/pecas/{peca}/checking", form)).EnsureSuccessStatusCode();
        var outra = await Ordem(client, op, peca);
        using (var form = Foto(idCliente))
            (await Codigo(await client.PostAsync($"/api/wl/campo/ordens/{outra}/pecas/{peca}/checking", form)))
                .Should().Be(Veiculando.WhiteLabel.Api.Controllers.CampoControllerBase.IdClienteReutilizado);

        client.DefaultRequestHeaders.Authorization = null;
        using (var form = Foto(Guid.NewGuid()))
            (await client.PostAsync($"/api/wl/campo/ordens/{id}/pecas/{peca}/checking", form))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Falha_do_storage_e_503_sem_foto_e_permite_reenviar()
    {
        var (factory, client, op, peca) = await Preparar("sto");
        using var f = factory; using var c = client;
        var id = await Ordem(client, op, peca);
        await ItemDeChecking(peca, "CKSTO");
        var idCliente = Guid.NewGuid();
        var rota = $"/api/wl/campo/ordens/{id}/pecas/{peca}/checking";

        factory.Uploads.FailPut = true;
        using (var form = Foto(idCliente))
            (await client.PostAsync(rota, form)).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        using (var ctx = new VeiculandoDataContext())
            (await ctx.CheckingFotos.CountAsync(x => x.IdClienteAcao == idCliente)).Should().Be(0);

        factory.Uploads.FailPut = false;
        using (var form = Foto(idCliente))
            (await client.PostAsync(rota, form)).StatusCode.Should().Be(HttpStatusCode.OK);
        factory.Uploads.Files.Should().ContainSingle();
    }
}
