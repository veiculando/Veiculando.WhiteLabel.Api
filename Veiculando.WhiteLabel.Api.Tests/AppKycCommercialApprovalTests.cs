using System;
using System.Data.Entity;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities.WhiteLabel;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests;

[Collection(DatabaseCollection.Nome)]
public sealed class AppKycCommercialApprovalTests
{
    private readonly SqlServerFixture _db;
    public AppKycCommercialApprovalTests(SqlServerFixture db) => _db = db;

    [Fact]
    public async Task Aprovacao_reutiliza_cliente_core_e_cria_identidade_comercial_sem_duplicar_cnpj()
    {
        const int tenant = 897;
        const string email = "app-kyc-897@exemplo.com";
        var local = await Seed.LocalAsync(tenant, "KY897A");
        var piece = await Seed.PecaAsync(local, "P-KY-897-A");
        await Seed.ReservaAsync(tenant, "RES-KY-897", piece);
        await Seed.AnuncianteAsync(tenant, email);
        await Seed.OperadorAsync(tenant, "revisor-897@exemplo.com", new[] { "ClienteGerenciar" });
        using (var setup = new VeiculandoDataContext())
        {
            await setup.Database.ExecuteSqlCommandAsync(@"
UPDATE Afiliada SET IdUsuarioCadastro = 1 WHERE Id = @p0;
IF NOT EXISTS (SELECT 1 FROM PerfilUsuario WHERE Codigo = 'UsuarioAnunciante')
    INSERT PerfilUsuario (Nome, Codigo, DataCadastro, DataAtualizacao, StatusExibicao)
    VALUES ('Anunciante WL', 'UsuarioAnunciante', GETUTCDATE(), GETUTCDATE(), 1);", tenant);
            var wl = await setup.WlUsuariosAnunciante.SingleAsync(u => u.AfiliadaId == tenant && u.Email.Endereco == email);
            var identity = new WlAppIdentidade(wl, "Responsável Teste", "11999999999", "v1", "v1");
            identity.ConfirmarPorConvite();
            setup.WlAppIdentidades.Add(identity);
            await setup.SaveChangesAsync();
        }
        using var factory = new WlApiFactory(_db, tenant);
        using var applicant = factory.ClienteAnonimo();
        var login = await applicant.PostAsJsonAsync("/api/wl/app/auth/login", new { email, password = Seed.SenhaPadrao });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = (await login.Content.ReadFromJsonAsync<TokenResponse>()).Token;
        applicant.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var submit = await applicant.PostAsJsonAsync("/api/wl/app/kyc", new
        {
            accountType = "ad", tradeName = "Cliente Teste", legalName = "Cliente Teste Ltda",
            document = "00000000000191", phone = "11999999999", city = "São Paulo", state = "SP",
            street = "Rua Teste", number = "100", district = "Centro", zipCode = "01001000",
            representativeName = "Responsável Teste", representativeCpf = "52998224725",
            representativeEmail = email, representativePhone = "11999999999"
        });
        submit.StatusCode.Should().Be(HttpStatusCode.OK, await submit.Content.ReadAsStringAsync());

        foreach (var kind in new[] { "corporate", "representative" })
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(kind), "type");
            var document = new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<<>>\nendobj"));
            document.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            form.Add(document, "file", kind + ".pdf");
            var uploaded = await applicant.PostAsync("/api/wl/app/kyc/documents", form);
            uploaded.StatusCode.Should().Be(HttpStatusCode.OK, await uploaded.Content.ReadAsStringAsync());
        }
        using var ctx = new VeiculandoDataContext();
        var onboarding = await ctx.WlAppOnboardings.SingleAsync(o => o.AfiliadaId == tenant && o.Usuario.Email.Endereco == email);
        using var reviewer = await factory.ClienteAutenticadoAsync("revisor-897@exemplo.com", Seed.SenhaPadrao);
        (await reviewer.PostAsync($"/api/wl/kyc/app/{onboarding.Id}/review", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        var approval = await reviewer.PostAsync($"/api/wl/kyc/app/{onboarding.Id}/approve", null);
        approval.StatusCode.Should().Be(HttpStatusCode.OK, await approval.Content.ReadAsStringAsync());
        (await reviewer.PostAsync($"/api/wl/kyc/app/{onboarding.Id}/approve", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var verify = new VeiculandoDataContext();
        (await verify.Clientes.CountAsync(c => c.Cnpj.Numero == "00000000000191")).Should().Be(1);
        var account = await verify.WlUsuariosAnunciante.SingleAsync(u => u.AfiliadaId == tenant && u.Email.Endereco == email);
        account.ClienteId.Should().Be(1);
        account.AgenciaId.Should().Be(1);
        (await verify.UsuariosAnunciantes.CountAsync(u => u.Email.Endereco == email)).Should().Be(1);
        (await verify.AfiliadaClientes.CountAsync(v => v.IdAfiliada == tenant && v.IdCliente == 1)).Should().Be(1);
    }

    [Fact]
    public async Task Aprovacao_de_agencia_cria_agencia_e_usuario_sem_criar_cliente_ou_contrato()
    {
        const int tenant = 898;
        const string email = "agencia-kyc-898@exemplo.com";
        const string cnpj = "11222333000181";
        await Seed.AfiliadaAsync(tenant);
        await Seed.AnuncianteAsync(tenant, email);
        await Seed.OperadorAsync(tenant, "revisor-898@exemplo.com", new[] { "ClienteGerenciar" });
        using (var setup = new VeiculandoDataContext())
        {
            await setup.Database.ExecuteSqlCommandAsync(@"
UPDATE Afiliada SET IdUsuarioCadastro = 1 WHERE Id = @p0;
IF NOT EXISTS (SELECT 1 FROM PerfilUsuario WHERE Codigo = 'UsuarioAnunciante')
    INSERT PerfilUsuario (Nome, Codigo, DataCadastro, DataAtualizacao, StatusExibicao)
    VALUES ('Anunciante WL', 'UsuarioAnunciante', GETUTCDATE(), GETUTCDATE(), 1);", tenant);
            var wl = await setup.WlUsuariosAnunciante.SingleAsync(u => u.AfiliadaId == tenant && u.Email.Endereco == email);
            var identity = new WlAppIdentidade(wl, "Agente Teste", "11999999999", "v1", "v1");
            identity.ConfirmarPorConvite();
            setup.WlAppIdentidades.Add(identity);
            await setup.SaveChangesAsync();
        }
        using var factory = new WlApiFactory(_db, tenant);
        using var applicant = factory.ClienteAnonimo();
        var login = await applicant.PostAsJsonAsync("/api/wl/app/auth/login", new { email, password = Seed.SenhaPadrao });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        applicant.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            (await login.Content.ReadFromJsonAsync<TokenResponse>()).Token);
        var submitted = await applicant.PostAsJsonAsync("/api/wl/app/kyc", new
        {
            accountType = "ag", tradeName = "Agência Teste", legalName = "Agência Teste de Publicidade Ltda",
            document = cnpj, phone = "11999999999", city = "São Paulo", state = "SP",
            street = "Rua da Agência", number = "100", district = "Centro", zipCode = "01001000",
            representativeName = "Agente Teste", representativeCpf = "52998224725",
            representativeEmail = email, representativePhone = "11999999999"
        });
        submitted.StatusCode.Should().Be(HttpStatusCode.OK, await submitted.Content.ReadAsStringAsync());
        foreach (var kind in new[] { "corporate", "representative" })
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(kind), "type");
            var document = new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<<>>\nendobj"));
            document.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            form.Add(document, "file", kind + ".pdf");
            var uploaded = await applicant.PostAsync("/api/wl/app/kyc/documents", form);
            uploaded.StatusCode.Should().Be(HttpStatusCode.OK, await uploaded.Content.ReadAsStringAsync());
        }
        using var ctx = new VeiculandoDataContext();
        var onboarding = await ctx.WlAppOnboardings.SingleAsync(o => o.AfiliadaId == tenant && o.Usuario.Email.Endereco == email);
        using var reviewer = await factory.ClienteAutenticadoAsync("revisor-898@exemplo.com", Seed.SenhaPadrao);
        (await reviewer.PostAsync($"/api/wl/kyc/app/{onboarding.Id}/review", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        var approved = await reviewer.PostAsync($"/api/wl/kyc/app/{onboarding.Id}/approve", null);
        approved.StatusCode.Should().Be(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync());

        using var verify = new VeiculandoDataContext();
        var agency = await verify.Agencias.SingleAsync(a => a.Cnpj.Numero == cnpj);
        var account = await verify.WlUsuariosAnunciante.SingleAsync(u => u.AfiliadaId == tenant && u.Email.Endereco == email);
        account.AgenciaId.Should().Be(agency.Id);
        account.ClienteId.Should().BeNull();
        (await verify.Clientes.CountAsync(c => c.Cnpj.Numero == cnpj)).Should().Be(0);
        (await verify.UsuariosAnunciantes.CountAsync(u => u.Email.Endereco == email)).Should().Be(1);
        (await verify.AfiliadaAgencias.CountAsync(v => v.IdAfiliada == tenant && v.IdAgencia == agency.Id)).Should().Be(1);
    }

    private sealed record TokenResponse(string Token);
}
