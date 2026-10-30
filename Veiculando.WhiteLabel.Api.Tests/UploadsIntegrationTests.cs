using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Data.Entity;
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

    private static readonly byte[] Jpg = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0xFF, 0xD9 };

    private static MultipartFormDataContent PhotoJpg(string name = "../../Foto.JPG")
    {
        var form = new MultipartFormDataContent();
        var bytes = new ByteArrayContent(Jpg);
        bytes.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(bytes, "foto", name);
        return form;
    }

    [Fact]
    public async Task Peca_grava_foto_no_fileserver_com_o_nome_do_core_sem_vazar_tenant()
    {
        const int tenant = 7901;
        await Seed.OperadorAsync(tenant, "upload7901@teste.local", new[] { "PecaGerenciar" });
        var local = await Seed.LocalAsync(tenant, "UL7901");
        var peca = await Seed.PecaAsync(local, "UP7901");
        using var factory = new WlApiFactory(_db, tenant);
        using var client = await factory.ClienteAutenticadoAsync("upload7901@teste.local", Seed.SenhaPadrao);
        var route = $"/api/wl/pecas/locais/{local}/pecas/{peca}/foto";
        using var form = PhotoJpg();
        (await client.PostAsync(route, form)).StatusCode.Should().Be(HttpStatusCode.OK);

        // Uma foto só por peça: o mesmo arquivo e o mesmo nome que o Core grava
        // (`{Codigo}.jpg` em minúsculas), nada no storage privado do WL.
        factory.FileServer.FotosPeca.Keys.Should().ContainSingle().Which.Should().Be("up7901.jpg");
        factory.Uploads.Files.Should().BeEmpty();
        (await FotoNoBancoAsync(peca)).Should().Be("up7901.jpg");

        var fotos = await client.GetFromJsonAsync<JsonElement[]>(route + "s");
        fotos.Should().ContainSingle();
        fotos[0].GetProperty("fileName").GetString().Should().Be("up7901.jpg");
        var download = fotos[0].GetProperty("downloadUrl").GetString();
        (await client.GetByteArrayAsync(download)).Should().Equal(Jpg);

        await Seed.OperadorAsync(7902, "upload7902@teste.local", new[] { "PecaGerenciar" });
        using var otherFactory = new WlApiFactory(_db, 7902);
        using var other = await otherFactory.ClienteAutenticadoAsync("upload7902@teste.local", Seed.SenhaPadrao);
        (await other.GetAsync(download)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.GetAsync(route + "s")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var otherPhoto = PhotoJpg();
        (await other.PostAsync(route, otherPhoto)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        otherFactory.FileServer.FotosPeca.Should().BeEmpty();
    }

    [Fact]
    public async Task Foto_de_peca_so_aceita_jpg_como_o_core()
    {
        const int tenant = 7905;
        await Seed.OperadorAsync(tenant, "png7905@teste.local", new[] { "PecaGerenciar" });
        var local = await Seed.LocalAsync(tenant, "UL7905");
        var peca = await Seed.PecaAsync(local, "UP7905");
        using var factory = new WlApiFactory(_db, tenant);
        using var client = await factory.ClienteAutenticadoAsync("png7905@teste.local", Seed.SenhaPadrao);
        using var png = Photo();
        (await client.PostAsync($"/api/wl/pecas/locais/{local}/pecas/{peca}/foto", png)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        factory.FileServer.FotosPeca.Should().BeEmpty();
    }

    [Fact]
    public async Task Falha_do_fileserver_nao_persiste_referencia_e_permite_retry_sem_falso_sucesso()
    {
        const int tenant = 7903;
        await Seed.OperadorAsync(tenant, "retry7903@teste.local", new[] { "PecaGerenciar" });
        var local = await Seed.LocalAsync(tenant, "UL7903");
        var peca = await Seed.PecaAsync(local, "UP7903");
        using var factory = new WlApiFactory(_db, tenant);
        using var client = await factory.ClienteAutenticadoAsync("retry7903@teste.local", Seed.SenhaPadrao);
        var route = $"/api/wl/pecas/locais/{local}/pecas/{peca}/foto";
        factory.FileServer.FalharUploadFoto = true;
        using var form = PhotoJpg();
        (await client.PostAsync(route, form)).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await FotoNoBancoAsync(peca)).Should().BeNull();
        (await client.GetFromJsonAsync<JsonElement[]>(route + "s")).Should().BeEmpty();
        factory.FileServer.FalharUploadFoto = false;
        using var retry = PhotoJpg();
        (await client.PostAsync(route, retry)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetFromJsonAsync<JsonElement[]>(route + "s")).Should().ContainSingle();
    }

    private static async Task<string> FotoNoBancoAsync(int pecaId)
    {
        using var ctx = new Veiculando.Data.Contexts.VeiculandoDataContext();
        return await ctx.Database.SqlQuery<string>("SELECT Foto FROM Peca WHERE Id = @p0", pecaId).SingleAsync();
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
