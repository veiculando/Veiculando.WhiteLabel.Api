using System;
using System.Data.Entity;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities.WhiteLabel;
using Veiculando.WhiteLabel.Api.Services;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests;

/// <summary>VEI-RD-155a: e-mail de campo leva à página que abre o app; o app conclui pelo token.</summary>
[Collection(DatabaseCollection.Nome)]
public class CampoAcessoTests
{
    private const int Afiliada = 9874;
    private const string NovaSenha = "NovaSenhaCampo1";
    private readonly SqlServerFixture _db;
    public CampoAcessoTests(SqlServerFixture db) => _db = db;

    private static async Task CriarOperadorWl(string email, bool comSenha)
    {
        await Seed.AfiliadaAsync(Afiliada);
        using var ctx = new VeiculandoDataContext();
        await ctx.Database.ExecuteSqlCommandAsync(@"
INSERT Usuario (Nome, Email, Senha, StatusAprovacao, Acessos, DataUltimoLogin,
 EmailConfirmado, DataCadastro, DataAtualizacao, StatusExibicao, IdPerfil)
VALUES ('Campo WL', @p0, @p2, 1, 0, GETUTCDATE(), 1, GETUTCDATE(), GETUTCDATE(), 1, 1);
INSERT Usuario_Operador (Id, IdAfiliada, Colador, Fiscal) VALUES (SCOPE_IDENTITY(), @p1, 1, 1);",
            email, Afiliada, SenhaUsuarioCore.Hash("senha-do-core-nao-usada"));
        var core = await ctx.UsuariosOperador.SingleAsync(u => u.Email.Endereco == email);
        var wl = new WLUsuario_Operador(email, comSenha ? BCrypt.Net.BCrypt.HashPassword(Seed.SenhaPadrao) : null, Afiliada);
        wl.ApontarPara(core);
        ctx.WlUsuariosOperador.Add(wl);
        await ctx.SaveChangesAsync();
    }

    private static (string Token, string Email) LerLink(string link, string tipo)
    {
        var uri = new Uri(link);
        uri.AbsolutePath.Should().Be("/api/wl/campo/acesso");
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        query["tipo"].Should().Be(tipo);
        return (query["token"], query["email"]);
    }

    [Fact]
    public async Task Primeiro_acesso_pelo_link_do_app_cria_a_senha_e_permite_login()
    {
        var email = $"pa-{Guid.NewGuid().ToString("N")[..12]}@exemplo.com";
        await CriarOperadorWl(email, comSenha: false);
        using var factory = new WlApiFactory(_db, Afiliada);
        var client = factory.ClienteAnonimo();

        (await client.PostAsJsonAsync("/api/wl/campo/auth/primeiro-acesso", new { email })).EnsureSuccessStatusCode();
        var link = factory.EmailSender.Convites.Should().ContainSingle().Which.LinkPrimeiroAcesso;
        link.Should().StartWith($"https://{factory.Host}/api/wl/campo/acesso?");
        var (token, emailLink) = LerLink(link, CampoAcesso.PrimeiroAcesso);

        var pagina = await client.GetAsync(new Uri(link).PathAndQuery);
        pagina.StatusCode.Should().Be(HttpStatusCode.OK);
        pagina.Headers.CacheControl!.NoStore.Should().BeTrue();
        var html = await pagina.Content.ReadAsStringAsync();
        html.Should().Contain($"veiculandooperador://acesso?host={Uri.EscapeDataString(factory.Host)}&amp;tipo=primeiro-acesso&amp;token={Uri.EscapeDataString(token)}");
        html.Should().Contain(factory.Host);

        var concluir = await client.PostAsJsonAsync("/api/wl/campo/auth/primeiro-acesso/concluir",
            new { email = emailLink, token, novaSenha = NovaSenha });
        concluir.StatusCode.Should().Be(HttpStatusCode.OK, await concluir.Content.ReadAsStringAsync());
        (await client.PostAsJsonAsync("/api/wl/campo/auth/primeiro-acesso/concluir",
            new { email = emailLink, token, novaSenha = NovaSenha })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await client.PostAsJsonAsync("/api/wl/campo/auth/login", new { email, senha = NovaSenha }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Esqueci_senha_pelo_link_do_app_troca_a_senha_uma_vez()
    {
        var email = $"es-{Guid.NewGuid().ToString("N")[..12]}@exemplo.com";
        await CriarOperadorWl(email, comSenha: true);
        using var factory = new WlApiFactory(_db, Afiliada);
        var client = factory.ClienteAnonimo();

        (await client.PostAsJsonAsync("/api/wl/campo/auth/esqueci-senha", new { email })).EnsureSuccessStatusCode();
        var link = factory.EmailSender.Envios.Should().ContainSingle().Which.LinkReset;
        var (token, emailLink) = LerLink(link, CampoAcesso.AlterarSenha);

        (await client.PostAsJsonAsync("/api/wl/campo/auth/alterar-senha", new { email = emailLink, token, novaSenha = "curta" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("/api/wl/campo/auth/alterar-senha", new { email = emailLink, token = "errado", novaSenha = NovaSenha }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var troca = await client.PostAsJsonAsync("/api/wl/campo/auth/alterar-senha", new { email = emailLink, token, novaSenha = NovaSenha });
        troca.StatusCode.Should().Be(HttpStatusCode.OK, await troca.Content.ReadAsStringAsync());
        (await client.PostAsJsonAsync("/api/wl/campo/auth/alterar-senha", new { email = emailLink, token, novaSenha = NovaSenha }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await client.PostAsJsonAsync("/api/wl/campo/auth/login", new { email, senha = Seed.SenhaPadrao }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.PostAsJsonAsync("/api/wl/campo/auth/login", new { email, senha = NovaSenha }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("?tipo=outro&token=t&email=e%40x.com")]
    [InlineData("?tipo=alterar-senha&email=e%40x.com")]
    [InlineData("")]
    public async Task Link_incompleto_ou_tipo_desconhecido_nao_gera_deep_link(string query)
    {
        await Seed.AfiliadaAsync(Afiliada);
        using var factory = new WlApiFactory(_db, Afiliada);
        var resposta = await factory.ClienteAnonimo().GetAsync("/api/wl/campo/acesso" + query);

        resposta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resposta.Content.ReadAsStringAsync()).Should().NotContain("veiculandooperador://");
        resposta.Headers.GetValues("Content-Security-Policy").Single().Should().Contain("script-src 'none'");
    }
}
