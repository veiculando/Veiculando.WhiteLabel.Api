using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests;

[Collection(DatabaseCollection.Nome)]
public class UploadsIntegrationTests
{
    private readonly SqlServerFixture _db;
    public UploadsIntegrationTests(SqlServerFixture db) => _db = db;

    private static MultipartFormDataContent Photo(string mime = "image/png", string name = "../../foto.png")
    {
        var form = new MultipartFormDataContent();
        var bytes = new ByteArrayContent(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jX1sAAAAASUVORK5CYII="));
        bytes.Headers.ContentType = new MediaTypeHeaderValue(mime);
        form.Add(bytes, "foto", name);
        return form;
    }

    [Fact]
    public async Task Peca_persiste_foto_no_storage_e_referencia_no_banco_sem_vazar_tenant()
    {
        const int tenant = 7901;
        await Seed.OperadorAsync(tenant, "upload7901@teste.local", new[] { "PecaGerenciar" });
        var local = await Seed.LocalAsync(tenant, "UL7901");
        var peca = await Seed.PecaAsync(local, "UP7901");
        using var factory = new WlApiFactory(_db, tenant);
        using var client = await factory.ClienteAutenticadoAsync("upload7901@teste.local", Seed.SenhaPadrao);
        var route = $"/api/wl/pecas/locais/{local}/pecas/{peca}/foto";
        using var form = Photo();
        (await client.PostAsync(route, form)).StatusCode.Should().Be(HttpStatusCode.OK);
        factory.Uploads.Files.Should().ContainSingle();
        var fotos = await client.GetFromJsonAsync<JsonElement[]>(route + "s");
        fotos.Should().ContainSingle();
        fotos[0].GetProperty("sha256").GetString().Should().HaveLength(64);
        fotos[0].GetProperty("fileName").GetString().Should().MatchRegex("^[a-f0-9]{32}\\.png$");
        var download = fotos[0].GetProperty("downloadUrl").GetString();
        (await client.GetByteArrayAsync(download)).Length.Should().BeGreaterThan(8);

        await Seed.OperadorAsync(7902, "upload7902@teste.local", new[] { "PecaGerenciar" });
        using var otherFactory = new WlApiFactory(_db, 7902);
        using var other = await otherFactory.ClienteAutenticadoAsync("upload7902@teste.local", Seed.SenhaPadrao);
        (await other.GetAsync(download)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.GetAsync(route + "s")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var otherPhoto = Photo();
        (await other.PostAsync(route, otherPhoto)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        otherFactory.Uploads.Files.Should().BeEmpty();
    }

    [Fact]
    public async Task Falha_storage_nao_persiste_referencia_e_permite_retry_sem_falso_sucesso()
    {
        const int tenant = 7903;
        await Seed.OperadorAsync(tenant, "retry7903@teste.local", new[] { "PecaGerenciar" });
        var local = await Seed.LocalAsync(tenant, "UL7903");
        var peca = await Seed.PecaAsync(local, "UP7903");
        using var factory = new WlApiFactory(_db, tenant);
        using var client = await factory.ClienteAutenticadoAsync("retry7903@teste.local", Seed.SenhaPadrao);
        var route = $"/api/wl/pecas/locais/{local}/pecas/{peca}/foto";
        factory.Uploads.FailPut = true;
        using var form = Photo();
        (await client.PostAsync(route, form)).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await client.GetFromJsonAsync<JsonElement[]>(route + "s")).Should().BeEmpty();
        factory.Uploads.FailPut = false;
        using var retry = Photo();
        (await client.PostAsync(route, retry)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetFromJsonAsync<JsonElement[]>(route + "s")).Should().ContainSingle();
    }

    [Fact]
    public async Task Perfil_comercial_nao_pode_enviar_nem_listar_fotos()
    {
        const int tenant = 7904;
        await Seed.OperadorAsync(tenant, "comercial7904@teste.local", new[] { "PedidoReservaGerenciar" });
        using var factory = new WlApiFactory(_db, tenant);
        using var client = await factory.ClienteAutenticadoAsync("comercial7904@teste.local", Seed.SenhaPadrao);
        foreach (var route in new[] { "/api/wl/pecas/locais/1/pecas/1/foto", "/api/wl/checking/enviar-foto/1" })
        {
            using var body = Photo();
            (await client.PostAsync(route, body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        (await client.GetAsync("/api/wl/checking/item/1/fotos")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        factory.Uploads.Files.Should().BeEmpty();
    }
}
