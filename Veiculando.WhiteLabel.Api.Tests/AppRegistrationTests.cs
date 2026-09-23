using System.Data.Entity;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Veiculando.Data.Contexts;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests;

[Collection(DatabaseCollection.Nome)]
public sealed class AppRegistrationTests
{
    private readonly SqlServerFixture _db;
    public AppRegistrationTests(SqlServerFixture db) => _db = db;

    [Fact]
    public async Task Cadastro_persiste_consentimento_e_OTP_consumido_uma_vez()
    {
        const string email = "cadastro-850@exemplo.com";
        using var factory = new WlApiFactory(_db, 850);
        using var client = factory.ClienteAnonimo();
        var request = Registration(email);
        var registered = await client.PostAsJsonAsync("/api/wl/app/auth/register", request);
        registered.StatusCode.Should().Be(HttpStatusCode.OK);
        var code = factory.EmailSender.Codigos.Should().ContainSingle().Which.Codigo;
        using (var db = new VeiculandoDataContext())
        {
            var identity = await db.WlAppIdentidades.SingleAsync(i => i.Usuario.Email.Endereco == email && i.Usuario.AfiliadaId == 850);
            identity.EmailConfirmado.Should().BeFalse();
            identity.TermosVersao.Should().Be("1.0");
            identity.CodigoHash.Should().HaveLength(64).And.NotBe(code);
            identity.AceiteEm.Should().BeAfter(System.DateTime.UtcNow.AddMinutes(-2));
        }
        (await client.PostAsJsonAsync("/api/wl/app/auth/login", new { email, password = "SenhaSegura850" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var confirmed = await client.PostAsJsonAsync("/api/wl/app/auth/confirm-email", new { email, code });
        confirmed.StatusCode.Should().Be(HttpStatusCode.OK);
        var session = await confirmed.Content.ReadFromJsonAsync<Session>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        (await client.GetAsync("/api/wl/app/auth/me")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync("/api/wl/app/auth/confirm-email", new { email, code })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsync("/api/wl/app/auth/logout", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetAsync("/api/wl/app/auth/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Cadastro_repetido_nao_enumera_email_nem_duplica_identidade()
    {
        const string email = "cadastro-851@exemplo.com";
        using var factory = new WlApiFactory(_db, 851);
        using var client = factory.ClienteAnonimo();
        var first = await client.PostAsJsonAsync("/api/wl/app/auth/register", Registration(email));
        var repeated = await client.PostAsJsonAsync("/api/wl/app/auth/register", Registration(email));
        repeated.StatusCode.Should().Be(first.StatusCode);
        (await repeated.Content.ReadAsStringAsync()).Should().Be(await first.Content.ReadAsStringAsync());
        factory.EmailSender.Codigos.Should().ContainSingle();
        using var db = new VeiculandoDataContext();
        (await db.WlAppIdentidades.CountAsync(i => i.Usuario.Email.Endereco == email && i.Usuario.AfiliadaId == 851)).Should().Be(1);
    }

    [Fact]
    public async Task Codigo_de_outro_tenant_nao_confirma_a_identidade()
    {
        const string email = "cadastro-852@exemplo.com";
        using var origin = new WlApiFactory(_db, 852);
        using var client = origin.ClienteAnonimo();
        (await client.PostAsJsonAsync("/api/wl/app/auth/register", Registration(email))).StatusCode.Should().Be(HttpStatusCode.OK);
        var code = origin.EmailSender.Codigos.Single().Codigo;
        using var other = new WlApiFactory(_db, 853);
        using var otherClient = other.ClienteAnonimo();
        (await otherClient.PostAsJsonAsync("/api/wl/app/auth/confirm-email", new { email, code })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var db = new VeiculandoDataContext();
        (await db.WlAppIdentidades.SingleAsync(i => i.Usuario.Email.Endereco == email && i.Usuario.AfiliadaId == 852)).EmailConfirmado.Should().BeFalse();
    }

    [Fact]
    public async Task Versao_de_termos_antiga_nao_cria_conta()
    {
        using var factory = new WlApiFactory(_db, 854);
        using var client = factory.ClienteAnonimo();
        var request = Registration("cadastro-854@exemplo.com") with { TermsVersion = "obsoleta" };
        (await client.PostAsJsonAsync("/api/wl/app/auth/register", request)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        factory.EmailSender.Codigos.Should().BeEmpty();
    }

    [Fact]
    public async Task Falha_no_envio_do_codigo_nao_pode_ser_apresentada_como_cadastro_entregue()
    {
        const string email = "cadastro-855@exemplo.com";
        using var factory = new WlApiFactory(_db, 855);
        factory.EmailSender.FalharProximoEnvio = true;
        using var client = factory.ClienteAnonimo();
        var response = await client.PostAsJsonAsync("/api/wl/app/auth/register", Registration(email));
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        using var db = new VeiculandoDataContext();
        var identity = await db.WlAppIdentidades.SingleAsync(i => i.Usuario.Email.Endereco == email && i.Usuario.AfiliadaId == 855);
        identity.CodigoHash.Should().BeNull("um código não entregue não pode confirmar a conta");
    }

    private static RegistrationBody Registration(string email) => new("Pessoa de Teste", email, "SenhaSegura850", "11987654321", true, "1.0", "1.0");
    private sealed record RegistrationBody(string Name, string Email, string Password, string Phone, bool AcceptedTerms, string TermsVersion, string PrivacyVersion);
    private sealed record Session(string Token);
}
