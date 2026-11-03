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
using Veiculando.Domain.Entities.OrdensServico;
using Veiculando.Domain.Enums;
using Veiculando.WhiteLabel.Api.Controllers;
using Veiculando.WhiteLabel.Api.Services;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests;

[Collection(DatabaseCollection.Nome)]
public class CampoAcoesTests
{
    private const int Afiliada = 9872;
    // Dentro da janela de HoraCampo (30 dias): relativa ao dia do teste, em segundos inteiros.
    private static readonly DateTimeOffset Hora = DateTimeOffset.FromUnixTimeSeconds(
        DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds()).ToOffset(TimeSpan.FromHours(-3));
    private readonly SqlServerFixture _db;
    public CampoAcoesTests(SqlServerFixture db) => _db = db;

    private async Task<(WlApiFactory Factory, HttpClient Client, int Operador, int Peca)> Preparar(string sufixo)
    {
        var local = await Seed.LocalAsync(Afiliada, "LA" + sufixo);
        var peca = await Seed.PecaAsync(local, "PA" + sufixo);
        await Seed.ReservaAsync(Afiliada, "RA" + sufixo, peca);
        using var ctx = new VeiculandoDataContext();
        var email = $"acao-{sufixo}@exemplo.com";
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

    private static async Task<int> Criar(int operador, int pecaId, int afiliadaId = Afiliada)
    {
        await Seed.AfiliadaAsync(afiliadaId);
        using var ctx = new VeiculandoDataContext();
        var peca = await ctx.Pecas.Include(p => p.Local.Cidade).SingleAsync(p => p.Id == pecaId);
        var ordem = new OrdemServico(await ctx.Afiliadas.FindAsync(afiliadaId), await ctx.Periodos.FindAsync(1),
            2000 + await ctx.OrdensServico.CountAsync(o => o.IdAfiliada == afiliadaId),
            new[] { peca.Local.Cidade }, new[] { peca }, await ctx.Usuarios.FindAsync(1));
        ordem.DefinirAtribuidos(operador, operador);
        ctx.OrdensServico.Add(ordem);
        await ctx.SaveChangesAsync();
        return ordem.Id;
    }

    private static object Corpo(Guid idCliente, double? lat = -23.55, double? lng = -46.63) =>
        new { idCliente, hora = Hora, latitude = lat, longitude = lng };

    private static async Task<int> Registros(Guid idCliente)
    {
        using var ctx = new VeiculandoDataContext();
        return await ctx.OrdemServicoAcoesCampo.CountAsync(a => a.IdCliente == idCliente);
    }

    private static async Task<string> Codigo(HttpResponseMessage resp)
    {
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString();
    }

    [Fact]
    public async Task Hora_do_aparelho_fora_da_janela_e_aceita_com_a_hora_do_servidor()
    {
        var (factory, client, op, peca) = await Preparar("hor");
        using var f = factory; using var c = client;
        var id = await Criar(op, peca);
        var antiga = Hora.AddDays(-45);
        var antes = DateTimeOffset.UtcNow;
        var idCliente = Guid.NewGuid();

        var resp = await client.PostAsJsonAsync($"/api/wl/campo/ordens/{id}/iniciar",
            new { idCliente, hora = antiga, latitude = -23.55, longitude = -46.63 });

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        using var ctx = new VeiculandoDataContext();
        var registro = await ctx.OrdemServicoAcoesCampo.SingleAsync(a => a.IdCliente == idCliente);
        registro.DataAcaoAparelho.Should().Be(antiga);
        registro.DataAcao.Should().BeOnOrAfter(antes);
        (await ctx.OrdensServico.FindAsync(id)).DataInicio.Should().Be(registro.DataAcao);
    }

    [Fact]
    public async Task Iniciar_e_colagem_aplicam_e_repeticao_devolve_o_mesmo_corpo()
    {
        var (factory, client, op, peca) = await Preparar("ok");
        using var f = factory; using var c = client;
        var id = await Criar(op, peca);
        var iniciar = Guid.NewGuid();
        var primeira = await client.PostAsJsonAsync($"/api/wl/campo/ordens/{id}/iniciar", Corpo(iniciar));
        var texto = await primeira.Content.ReadAsStringAsync();
        primeira.StatusCode.Should().Be(HttpStatusCode.OK, texto);
        var dto = JsonDocument.Parse(texto).RootElement;
        dto.GetProperty("ordem").GetProperty("dataInicio").GetDateTimeOffset().Should().Be(Hora);
        dto.GetProperty("ordem").GetProperty("progresso").GetProperty("tipo").GetString().Should().Be("colagens");
        var repetida = await client.PostAsJsonAsync($"/api/wl/campo/ordens/{id}/iniciar", Corpo(iniciar));
        (await repetida.Content.ReadAsStringAsync()).Should().Be(texto);
        (await Registros(iniciar)).Should().Be(1);

        var colagem = Guid.NewGuid();
        var marcada = await client.PostAsJsonAsync($"/api/wl/campo/ordens/{id}/pecas/{peca}/colagem", Corpo(colagem, null, null));
        var textoColagem = await marcada.Content.ReadAsStringAsync();
        marcada.StatusCode.Should().Be(HttpStatusCode.OK, textoColagem);
        var progresso = JsonDocument.Parse(textoColagem).RootElement.GetProperty("ordem").GetProperty("progresso");
        progresso.GetProperty("tipo").GetString().Should().Be("fotos");
        (await (await client.PostAsJsonAsync($"/api/wl/campo/ordens/{id}/pecas/{peca}/colagem", Corpo(colagem, null, null)))
            .Content.ReadAsStringAsync()).Should().Be(textoColagem);
        (await Registros(colagem)).Should().Be(1);
        using var ctx = new VeiculandoDataContext();
        var ordem = await ctx.OrdensServico.Include(o => o.Pecas).SingleAsync(o => o.Id == id);
        ordem.Status.Should().Be(StatusOrdemServicoEnum.AguardandoFoto);
        ordem.Pecas.Single().DataColagem.Should().Be(Hora);
    }

    [Fact]
    public async Task Recusa_por_historico_e_cancelada_sai_com_codigo_e_fica_registrada()
    {
        var (factory, client, op, peca) = await Preparar("rec");
        using var f = factory; using var c = client;
        var historico = await Criar(op, peca);
        var cancelada = await Criar(op, peca);
        using (var ctx = new VeiculandoDataContext())
        {
            (await ctx.OrdensServico.FindAsync(historico)).Desatribuir(op);
            await ctx.SaveChangesAsync();
            await ctx.Database.ExecuteSqlCommandAsync("UPDATE OrdemServico SET Status=@p0 WHERE Id=@p1",
                (int)StatusOrdemServicoEnum.Cancelada, cancelada);
        }
        var idHistorico = Guid.NewGuid();
        (await Codigo(await client.PostAsJsonAsync($"/api/wl/campo/ordens/{historico}/pecas/{peca}/colagem", Corpo(idHistorico))))
            .Should().Be(RecusaCampo.SomenteHistorico);
        (await Codigo(await client.PostAsJsonAsync($"/api/wl/campo/ordens/{historico}/pecas/{peca}/colagem", Corpo(idHistorico))))
            .Should().Be(RecusaCampo.SomenteHistorico);
        (await Registros(idHistorico)).Should().Be(1);
        (await Codigo(await client.PostAsJsonAsync($"/api/wl/campo/ordens/{cancelada}/iniciar", Corpo(Guid.NewGuid()))))
            .Should().Be(RecusaCampo.OrdemCancelada);
    }

    [Fact]
    public async Task Ordem_invisivel_e_404_e_id_reutilizado_ou_corpo_invalido_sao_recusados()
    {
        var (factory, client, op, peca) = await Preparar("iso");
        using var f = factory; using var c = client;
        var propria = await Criar(op, peca);
        var semVinculo = await Criar(1, peca);
        var localB = await Seed.LocalAsync(Afiliada + 1, "LAisob");
        var outraAfiliada = await Criar(op, await Seed.PecaAsync(localB, "PAisob"), Afiliada + 1);
        foreach (var id in new[] { semVinculo, outraAfiliada, int.MaxValue })
            (await client.PostAsJsonAsync($"/api/wl/campo/ordens/{id}/iniciar", Corpo(Guid.NewGuid())))
                .StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await client.PostAsJsonAsync($"/api/wl/campo/ordens/{propria}/iniciar", Corpo(Guid.Empty)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync($"/api/wl/campo/ordens/{propria}/iniciar", Corpo(Guid.NewGuid(), null, null)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync($"/api/wl/campo/ordens/{propria}/iniciar", Corpo(Guid.NewGuid(), 91)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var idCliente = Guid.NewGuid();
        (await client.PostAsJsonAsync($"/api/wl/campo/ordens/{propria}/iniciar", Corpo(idCliente)))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await Codigo(await client.PostAsJsonAsync($"/api/wl/campo/ordens/{propria}/pecas/{peca}/colagem", Corpo(idCliente))))
            .Should().Be(CampoControllerBase.IdClienteReutilizado);

        client.DefaultRequestHeaders.Authorization = null;
        (await client.PostAsJsonAsync($"/api/wl/campo/ordens/{propria}/iniciar", Corpo(Guid.NewGuid())))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
