using System;
using System.Data.Entity;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Veiculando.Data.Contexts;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests;

[Collection(DatabaseCollection.Nome)]
public class LocalLifecycleTests
{
    private readonly SqlServerFixture _db;
    public LocalLifecycleTests(SqlServerFixture db) => _db = db;

    [Fact]
    public async Task Inativar_reativar_cancelar_persiste_e_rejeita_versao_antiga()
    {
        const int tenant = 7801;
        const string email = "lifecycle@teste.local";
        await Seed.OperadorAsync(tenant, email, new[] { "PecaGerenciar" });
        var id = await Seed.LocalAsync(tenant, "CICLO7801");
        using var factory = new WlApiFactory(_db, tenant);
        using var client = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);
        var atual = await client.GetFromJsonAsync<JsonElement>($"/api/wl/locais/{id}");
        var versao = atual.GetProperty("timeStamp").GetString();
        (await client.PostAsJsonAsync($"/api/wl/locais/{id}/inativar", new { timeStamp = versao })).StatusCode.Should().Be(HttpStatusCode.OK);
        var inativo = await client.GetFromJsonAsync<JsonElement>($"/api/wl/locais/{id}");
        inativo.GetProperty("statusExibicao").GetInt32().Should().Be(0);
        (await client.GetAsync($"/api/wl/locais/{id}/publico")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PutAsJsonAsync($"/api/wl/locais/{id}/publico", new { audiencia = 12345, fonte = "QA inativo" })).StatusCode.Should().Be(HttpStatusCode.OK);
        var lista = await client.GetFromJsonAsync<JsonElement[]>("/api/wl/locais");
        lista.Should().Contain(x => x.GetProperty("id").GetInt32() == id);
        (await client.PostAsJsonAsync($"/api/wl/locais/{id}/reativar", new { timeStamp = versao })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.PostAsJsonAsync($"/api/wl/locais/{id}/reativar", new { timeStamp = inativo.GetProperty("timeStamp").GetString() })).StatusCode.Should().Be(HttpStatusCode.OK);
        var pendente = await client.GetFromJsonAsync<JsonElement>($"/api/wl/locais/{id}");
        pendente.GetProperty("statusExibicao").GetInt32().Should().Be(2);
        (await client.PostAsJsonAsync($"/api/wl/locais/{id}/inativar", new { timeStamp = pendente.GetProperty("timeStamp").GetString() })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.PostAsJsonAsync($"/api/wl/locais/{id}/cancelar", new { timeStamp = pendente.GetProperty("timeStamp").GetString() })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync($"/api/wl/locais/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync($"/api/wl/locais/{id}/publico")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PutAsJsonAsync($"/api/wl/locais/{id}/publico", new { audiencia = 12345 })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var context = new VeiculandoDataContext();
        ((int)(await context.Locais.SingleAsync(x => x.Id == id)).StatusExibicao).Should().Be(-1);
    }

    [Fact]
    public async Task Transicoes_exigem_permissao_e_nao_alcancam_outro_tenant()
    {
        const int tenant = 7802;
        await Seed.OperadorAsync(tenant, "leitura7802@teste.local", Array.Empty<string>());
        await Seed.OperadorAsync(tenant, "operacao7802@teste.local", new[] { "PecaGerenciar" });
        var id = await Seed.LocalAsync(7803, "OUTRO7803");
        using var factory = new WlApiFactory(_db, tenant);
        using var anon = factory.ClienteAnonimo();
        using var reader = await factory.ClienteAutenticadoAsync("leitura7802@teste.local", Seed.SenhaPadrao);
        using var writer = await factory.ClienteAutenticadoAsync("operacao7802@teste.local", Seed.SenhaPadrao);
        foreach (var acao in new[] { "inativar", "reativar", "cancelar" })
        {
            var route = $"/api/wl/locais/{id}/{acao}";
            var payload = new { timeStamp = Convert.ToBase64String(new byte[8]) };
            (await anon.PostAsJsonAsync(route, payload)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await reader.PostAsJsonAsync(route, payload)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await writer.PostAsJsonAsync(route, payload)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }
}
