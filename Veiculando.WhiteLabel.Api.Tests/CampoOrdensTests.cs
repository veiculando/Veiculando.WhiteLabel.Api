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
using Veiculando.WhiteLabel.Api.Services;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests;

[Collection(DatabaseCollection.Nome)]
public class CampoOrdensTests
{
    private const int Afiliada = 9870;
    private readonly SqlServerFixture _db;
    public CampoOrdensTests(SqlServerFixture db) => _db = db;

    private async Task<(WlApiFactory Factory, HttpClient Client, int Operador, int Peca)> Preparar(string sufixo)
    {
        var local = await Seed.LocalAsync(Afiliada, "LC" + sufixo);
        var peca = await Seed.PecaAsync(local, "PC" + sufixo);
        await Seed.ReservaAsync(Afiliada, "RC" + sufixo, peca);
        using var ctx = new VeiculandoDataContext();
        var email = $"campo-{sufixo}@exemplo.com";
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

    private static async Task<int> Criar(int operador, int pecaId, bool rascunho = false, int afiliadaId = Afiliada)
    {
        await Seed.AfiliadaAsync(afiliadaId);
        using var ctx = new VeiculandoDataContext();
        var peca = await ctx.Pecas.Include(p => p.Local.Cidade).SingleAsync(p => p.Id == pecaId);
        var ordem = new OrdemServico(await ctx.Afiliadas.FindAsync(afiliadaId), await ctx.Periodos.FindAsync(1),
            1000 + await ctx.OrdensServico.CountAsync(o => o.IdAfiliada == afiliadaId),
            new[] { peca.Local.Cidade }, new[] { peca }, await ctx.Usuarios.FindAsync(1));
        if (!rascunho) ordem.DefinirAtribuidos(operador, operador);
        ctx.OrdensServico.Add(ordem);
        await ctx.SaveChangesAsync();
        return ordem.Id;
    }

    [Fact]
    public async Task Lista_pagina_no_sql_e_so_inclui_atribuicao_ou_historico()
    {
        var (factory, client, op, peca) = await Preparar("lst");
        using var f = factory; using var c = client;
        var atual = await Criar(op, peca);
        var historico = await Criar(op, peca);
        await Criar(op, peca, rascunho: true);
        await Criar(1, peca); // Mesmo tenant, sem papel nem historico do operador.
        using (var ctx = new VeiculandoDataContext())
        {
            (await ctx.OrdensServico.FindAsync(historico)).Desatribuir(op);
            await ctx.SaveChangesAsync();
        }
        var pagina = await client.GetFromJsonAsync<JsonElement>("/api/wl/campo/ordens?ordenacao=antigo&tamanho=1");
        pagina.GetProperty("total").GetInt32().Should().Be(2);
        var itens = pagina.GetProperty("itens").EnumerateArray().ToArray();
        itens.Should().ContainSingle();
        itens[0].GetProperty("id").GetInt32().Should().Be(atual);
        itens[0].GetProperty("papeis").EnumerateArray().Select(x => x.GetString()).Should().Equal("colador", "fiscal");
        itens[0].GetProperty("acao").GetString().Should().Be("Iniciar");
        var segunda = await client.GetFromJsonAsync<JsonElement>("/api/wl/campo/ordens?ordenacao=antigo&tamanho=1&pagina=2");
        segunda.GetProperty("itens")[0].GetProperty("id").GetInt32().Should().Be(historico);
        segunda.GetProperty("itens")[0].GetProperty("acao").GetString().Should().Be("Consulta");
    }

    [Fact]
    public async Task Detalhe_oculta_locais_antes_do_inicio_e_preserva_hora_da_colagem()
    {
        var (factory, client, op, peca) = await Preparar("det");
        using var f = factory; using var c = client;
        var id = await Criar(op, peca);
        var antes = await client.GetFromJsonAsync<JsonElement>($"/api/wl/campo/ordens/{id}");
        antes.GetProperty("locais").GetArrayLength().Should().Be(0);
        antes.GetProperty("periodo").GetProperty("id").GetInt32().Should().Be(1);
        var hora = new DateTimeOffset(2026, 10, 28, 15, 32, 17, TimeSpan.FromHours(-3));
        using (var ctx = new VeiculandoDataContext())
        {
            var ordem = await ctx.OrdensServico.Include(o => o.Pecas).SingleAsync(o => o.Id == id);
            ordem.Iniciar(op, -23.55, -46.63, hora).Should().BeNull();
            ordem.MarcarColagem(peca, op, hora.AddSeconds(1)).Should().BeNull();
            await ctx.SaveChangesAsync();
        }
        var depois = await client.GetFromJsonAsync<JsonElement>($"/api/wl/campo/ordens/{id}");
        var local = depois.GetProperty("locais")[0];
        local.GetProperty("codigo").GetString().Should().Be("LCdet");
        var item = local.GetProperty("pecas")[0];
        item.GetProperty("codigo").GetString().Should().Be("PCdet");
        item.GetProperty("latitude").GetDouble().Should().BeApproximately(-23.55, 0.001);
        item.GetProperty("dataColagem").GetDateTimeOffset().Should().Be(hora.AddSeconds(1));
        item.GetProperty("fotoAnteriorUrl").ValueKind.Should().Be(JsonValueKind.Null);
        depois.GetProperty("progresso").GetProperty("tipo").GetString().Should().Be("fotos");
    }

    [Fact]
    public async Task Ordem_sem_vinculo_e_rascunho_retornam_404()
    {
        var (factory, client, op, peca) = await Preparar("nv");
        using var f = factory; using var c = client;
        var semVinculo = await Criar(1, peca);
        var rascunho = await Criar(op, peca, rascunho: true);
        foreach (var id in new[] { semVinculo, rascunho, int.MaxValue })
            (await client.GetAsync($"/api/wl/campo/ordens/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Ordem_de_outra_afiliada_e_oculta_e_token_nao_cruza_host()
    {
        var (factory, client, op, _) = await Preparar("iso");
        using var f = factory; using var c = client;
        var localB = await Seed.LocalAsync(Afiliada + 1, "LCisob");
        var pecaB = await Seed.PecaAsync(localB, "PCisob");
        var idB = await Criar(op, pecaB, afiliadaId: Afiliada + 1);
        (await client.GetAsync($"/api/wl/campo/ordens/{idB}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var lista = await client.GetFromJsonAsync<JsonElement>("/api/wl/campo/ordens");
        lista.GetProperty("total").GetInt32().Should().Be(0);
        await Seed.DominioAsync(Afiliada + 1, "campo-b.teste", true);
        client.DefaultRequestHeaders.Host = "campo-b.teste";
        (await client.GetAsync("/api/wl/campo/ordens")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Busca_periodo_estado_e_ordenacao_sao_aplicados_no_servidor()
    {
        var (factory, client, op, peca) = await Preparar("flt");
        using var f = factory; using var c = client;
        var antigo = await Criar(op, peca);
        var recente = await Criar(op, peca);
        using (var ctx = new VeiculandoDataContext())
        {
            await ctx.Database.ExecuteSqlCommandAsync("UPDATE OrdemServico SET DataCadastro=@p0 WHERE Id=@p1", new DateTime(2026, 1, 1), antigo);
            await ctx.Database.ExecuteSqlCommandAsync("UPDATE OrdemServico SET DataCadastro=@p0 WHERE Id=@p1", new DateTime(2026, 2, 1), recente);
        }
        var lista = await client.GetFromJsonAsync<JsonElement>("/api/wl/campo/ordens?busca=paulo&ordenacao=recente&estado=Pendente");
        lista.GetProperty("total").GetInt32().Should().Be(2);
        lista.GetProperty("itens")[0].GetProperty("id").GetInt32().Should().Be(recente);
        var fora = await client.GetFromJsonAsync<JsonElement>("/api/wl/campo/ordens?de=2099-01-01");
        fora.GetProperty("total").GetInt32().Should().Be(0);
        var proximo = await client.GetFromJsonAsync<JsonElement>("/api/wl/campo/ordens?ordenacao=proximo&lat=-23.55&lng=-46.63");
        proximo.GetProperty("total").GetInt32().Should().Be(2);
        var estado = await client.GetFromJsonAsync<JsonElement>("/api/wl/campo/ordens?estado=Cancelada");
        estado.GetProperty("total").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Detalhe_resolve_campanhas_motivo_e_urls_autenticadas_sem_expor_arquivos()
    {
        var (factory, client, op, pecaId) = await Preparar("img");
        using var f = factory; using var c = client;
        var id = await Criar(op, pecaId);
        const int anteriorId = 19871;
        await Seed.PeriodoAsync(anteriorId, "CAMPO-ANT");
        var piId = await Seed.InsercaoAsync(Afiliada, "CIMG", pecaId);
        int arteId, fotoId;
        using (var ctx = new VeiculandoDataContext())
        {
            var atual = await ctx.Periodos.FindAsync(1);
            await ctx.Database.ExecuteSqlCommandAsync("UPDATE Periodo SET DataInicio=@p0, DataFim=@p1, Periodicidade=(SELECT Periodicidade FROM Periodo WHERE Id=1) WHERE Id=@p2",
                atual.DataInicio.AddDays(-30), atual.DataInicio.AddDays(-16), anteriorId);
            await ctx.Database.ExecuteSqlCommandAsync(@"UPDATE PedidoItem SET IdPeriodo=@p0
                WHERE Id IN (SELECT IdPedidoItem FROM PedidoInsercaoItem WHERE IdPedidoInsercao=@p1);
                UPDATE Pedido SET IdPeriodo=@p0 WHERE Id=(SELECT IdPedido FROM PedidoInsercao WHERE Id=@p1)", anteriorId, piId);
            var peca = await ctx.Pecas.Include(p => p.Local).SingleAsync(p => p.Id == pecaId);
            var user = await ctx.Usuarios.FindAsync(1);
            var campanha = await ctx.Campanhas.Include(x => x.Motivos).SingleAsync(x => x.Id == 1);
            var formato = new Veiculando.Domain.Entities.Formato(Veiculando.Domain.Enums.MidiaTipoEnum.Fisica,
                9, 3, 100, 100, 0, "", "", new[] { ".jpg" }, user);
            var motivo = new Veiculando.Domain.Entities.Arte.Motivo(campanha, "Motivo Campo", "", await ctx.UsuariosAnunciantes.FindAsync(1));
            var arte = new Veiculando.Domain.Entities.Arte.ArteFinal(motivo, formato, "arte-final/cli/camp/privado-alta.jpg", "temp.jpg", user);
            var arteAntiga = new Veiculando.Domain.Entities.Arte.ArteFinal(motivo, formato, "arte-final/cli/camp/privado-antiga.jpg", "temp.jpg", user);
            ctx.ArtesFinais.Add(arteAntiga);
            ctx.ArtesFinais.Add(arte);
            var pedidoAtual = await ctx.Pedidos.Where(x => x.IdPeriodo == 1 && x.Itens.Any(i => i.IdPeca == pecaId)).FirstAsync();
            ctx.PecaPeriodoStatus.Add(new Veiculando.Domain.Entities.PecaPeriodoStatus(peca, atual, pedidoAtual));
            var pi = await ctx.PedidosInsercao.Include(x => x.Pedido)
                .Include(x => x.Itens.Select(i => i.PedidoItem.Peca.Local)).SingleAsync(x => x.Id == piId);
            ctx.PecaPeriodoStatus.Add(new Veiculando.Domain.Entities.PecaPeriodoStatus(peca, await ctx.Periodos.FindAsync(anteriorId), pi.Pedido));
            var checking = new Veiculando.Domain.Entities.Checking(pi, user);
            var item = new Veiculando.Domain.Entities.CheckingItem(checking, pi.Itens.Single(), user);
            ctx.Checkings.Add(checking);
            var foto = new Veiculando.Domain.Entities.CheckingFoto(item, "https://file.invalido/conteudo/checking/privado-anterior.jpg",
                DateTime.UtcNow, 100, new Veiculando.Domain.ValueObjects.Geolocalizacao(-23.55, -46.63),
                new System.Collections.Generic.Dictionary<string, string> { ["Modelo"] = "Aparelho de teste" }, "", user);
            ctx.CheckingFotos.Add(foto);
            (await ctx.OrdensServico.Include(x => x.Pecas).SingleAsync(x => x.Id == id))
                .Iniciar(op, -23.55, -46.63, DateTimeOffset.UtcNow).Should().BeNull();
            var erros = ctx.GetValidationErrors().SelectMany(e => e.ValidationErrors.Select(v =>
                e.Entry.Entity.GetType().Name + "." + v.PropertyName + ": " + v.ErrorMessage)).ToArray();
            erros.Should().BeEmpty("o grafo de campanha/arte/checking do teste precisa ser valido");
            await ctx.SaveChangesAsync();
            await ctx.Database.ExecuteSqlCommandAsync("UPDATE Peca SET IdFormatoArteFinal=@p0 WHERE Id=@p1", formato.Id, pecaId);
            await ctx.Database.ExecuteSqlCommandAsync("UPDATE ArteFinal SET DataCadastro=@p0 WHERE Id=@p1", new DateTime(2020, 1, 1), arteAntiga.Id);
            arteId = arte.Id; fotoId = foto.Id;
        }
        var resp = await client.GetAsync($"/api/wl/campo/ordens/{id}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var texto = await resp.Content.ReadAsStringAsync();
        texto.Should().NotContain("privado-");
        var dto = JsonDocument.Parse(texto).RootElement;
        var p = dto.GetProperty("locais")[0].GetProperty("pecas")[0];
        p.GetProperty("anterior").GetString().Should().Be("Campanha Teste");
        p.GetProperty("atual").GetString().Should().Be("Campanha Teste");
        p.GetProperty("motivo").GetString().Should().Be("Motivo Campo");
        p.GetProperty("fotoAnteriorUrl").GetString().Should().Be($"/api/wl/campo/imagens/checking/{fotoId}");
        p.GetProperty("arteAtualUrl").GetString().Should().Be($"/api/wl/campo/imagens/arte/{arteId}");

        // 154d: as duas URLs servem a imagem pelo BFF, sem expor o FileServer.
        factory.FileServer.Conteudo = new byte[] { 0xFF, 0xD8, 0xFF };
        foreach (var url in new[] { $"/api/wl/campo/imagens/checking/{fotoId}", $"/api/wl/campo/imagens/arte/{arteId}" })
        {
            var img = await client.GetAsync(url);
            img.StatusCode.Should().Be(HttpStatusCode.OK, url);
            img.Content.Headers.ContentType!.MediaType.Should().Be("image/jpeg");
            (await img.Content.ReadAsByteArrayAsync()).Should().Equal(0xFF, 0xD8, 0xFF);
            img.Headers.CacheControl!.NoStore.Should().BeTrue();
        }
        factory.FileServer.Rotas.Should().Equal(
            "http://fileserver.invalido/conteudo/checking/privado-anterior.jpg",
            "http://fileserver.invalido/conteudo/arte-final/cli/camp/privado-alta.jpg");

        var pacote = await client.GetFromJsonAsync<JsonElement>("/api/wl/campo/pacote");
        var noPacote = pacote.GetProperty("itens").EnumerateArray().Single(o => o.GetProperty("id").GetInt32() == id);
        noPacote.GetProperty("locais")[0].GetProperty("pecas")[0].GetProperty("arteAtualUrl").GetString()
            .Should().Be($"/api/wl/campo/imagens/arte/{arteId}");

        // Outra afiliada, id inexistente ou sem token: nada chega ao FileServer.
        await Seed.DominioAsync(Afiliada + 1, "campo-img-b.teste", true);
        var antes = factory.FileServer.Rotas.Count;
        (await client.GetAsync("/api/wl/campo/imagens/checking/" + int.MaxValue)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync("/api/wl/campo/imagens/arte/" + int.MaxValue)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        client.DefaultRequestHeaders.Host = "campo-img-b.teste";
        (await client.GetAsync($"/api/wl/campo/imagens/arte/{arteId}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        client.DefaultRequestHeaders.Host = null;
        client.DefaultRequestHeaders.Authorization = null;
        (await client.GetAsync($"/api/wl/campo/imagens/checking/{fotoId}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        factory.FileServer.Rotas.Count.Should().Be(antes);
    }

    [Fact]
    public async Task Imagem_de_ordem_nao_iniciada_ou_sem_vinculo_e_404_sem_chamar_o_fileserver()
    {
        var (factory, client, op, peca) = await Preparar("imv");
        using var f = factory; using var c = client;
        await Criar(op, peca); // Pendente: o detalhe ainda nao mostra imagens.
        int fotoId;
        using (var ctx = new VeiculandoDataContext())
        {
            var piId = await Seed.InsercaoAsync(Afiliada, "CIMV", peca);
            var pi = await ctx.PedidosInsercao.Include(x => x.Pedido)
                .Include(x => x.Itens.Select(i => i.PedidoItem.Peca.Local)).SingleAsync(x => x.Id == piId);
            var user = await ctx.Usuarios.FindAsync(1);
            var checking = new Veiculando.Domain.Entities.Checking(pi, user);
            var item = new Veiculando.Domain.Entities.CheckingItem(checking, pi.Itens.Single(), user);
            ctx.Checkings.Add(checking);
            var foto = new Veiculando.Domain.Entities.CheckingFoto(item, "https://file.invalido/conteudo/checking/imv.jpg",
                DateTime.UtcNow, 100, new Veiculando.Domain.ValueObjects.Geolocalizacao(-23.55, -46.63),
                new System.Collections.Generic.Dictionary<string, string> { ["Modelo"] = "Aparelho de teste" }, "", user);
            ctx.CheckingFotos.Add(foto);
            await ctx.SaveChangesAsync();
            fotoId = foto.Id;
        }
        (await client.GetAsync($"/api/wl/campo/imagens/checking/{fotoId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        factory.FileServer.Rotas.Should().BeEmpty();
    }

    [Fact]
    public async Task Filtros_invalidos_sao_400_e_rotas_sem_token_sao_401()
    {
        var (factory, client, _, _) = await Preparar("inv");
        using var f = factory; using var c = client;
        foreach (var query in new[] { "ordenacao=proximo", "ordenacao=x", "estado=999", "tamanho=101",
            "pagina=0", "pagina=2147483647&tamanho=100", "lat=91", "de=2026-10-28&ate=2026-10-01" })
            (await client.GetAsync("/api/wl/campo/ordens?" + query)).StatusCode.Should().Be(HttpStatusCode.BadRequest, query);
        client.DefaultRequestHeaders.Authorization = null;
        (await client.GetAsync("/api/wl/campo/ordens")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
