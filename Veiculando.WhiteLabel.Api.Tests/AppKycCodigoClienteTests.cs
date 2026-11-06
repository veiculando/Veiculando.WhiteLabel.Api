using System;
using System.Data.Entity;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FluentAssertions;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities;
using Veiculando.Domain.Entities.WhiteLabel;
using Veiculando.Domain.Enums;
using Veiculando.Domain.ValueObjects;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests;

/// <summary>
/// VEI-SUP-37: a aprovação do KYC "ad" com CNPJ que não existe no Core criava o Cliente com
/// os 6 primeiros dígitos do CNPJ como código. O Core exige código começando com letra, então
/// a aprovação caía em 409. E a busca do cliente existente comparava o CNPJ cru, sem achar
/// cliente gravado com máscara.
/// </summary>
[Collection(DatabaseCollection.Nome)]
public sealed class AppKycCodigoClienteTests
{
    private readonly SqlServerFixture _db;
    public AppKycCodigoClienteTests(SqlServerFixture db) => _db = db;

    [Fact]
    public async Task Aprovacao_ad_com_cnpj_novo_cria_cliente_com_codigo_valido()
    {
        const int tenant = 8968;
        const string cnpj = "37096800000168";
        var (approval, email) = await AprovarKycAdAsync(tenant, cnpj, null);

        approval.StatusCode.Should().Be(HttpStatusCode.OK, await approval.Content.ReadAsStringAsync());
        using var verify = new VeiculandoDataContext();
        var account = await verify.WlUsuariosAnunciante.SingleAsync(u => u.AfiliadaId == tenant && u.Email.Endereco == email);
        var cliente = await verify.Clientes.SingleAsync(c => c.Id == account.ClienteId);
        Regex.IsMatch(cliente.Codigo, "^W[A-Z2-9]{5}$").Should().BeTrue($"o código \"{cliente.Codigo}\" precisa começar com letra");
        cliente.Cnpj.Numero.Should().Be(cnpj);
        (await verify.Clientes.CountAsync(c => c.Cnpj.Numero == cnpj)).Should().Be(1);
    }

    [Fact]
    public async Task Aprovacao_ad_reaproveita_cliente_do_core_gravado_com_mascara()
    {
        const int tenant = 8969;
        const string cnpj = "37096900000194";
        var (approval, email) = await AprovarKycAdAsync(tenant, cnpj, "37.096.900/0001-94");

        approval.StatusCode.Should().Be(HttpStatusCode.OK, await approval.Content.ReadAsStringAsync());
        using var verify = new VeiculandoDataContext();
        var existente = await verify.Clientes.SingleAsync(c => c.Codigo == "MSK969");
        var account = await verify.WlUsuariosAnunciante.SingleAsync(u => u.AfiliadaId == tenant && u.Email.Endereco == email);
        account.ClienteId.Should().Be(existente.Id, "o CNPJ é o mesmo, só a máscara muda");
        (await verify.Clientes.CountAsync(c => c.Cnpj.Numero == cnpj || c.Cnpj.Numero == "37.096.900/0001-94")).Should().Be(1);
    }

    [Fact]
    public async Task Aprovacao_ad_reativa_cliente_inativado_pela_carteira()
    {
        // VEI-SUP-40: a vendedora cadastrou o CNPJ, o cadastro foi reprovado (cliente inativado);
        // depois a própria empresa passa pelo KYC. A aprovação reaproveita o cliente, sem 409.
        const int tenant = 8964;
        var cnpj = ProspeccaoCarteiraTests.Cnpj(tenant, 7);
        int clienteId = 0;
        var (approval, _) = await AprovarKycAdAsync(tenant, cnpj, null, async (db, afiliada) =>
        {
            var cliente = new Cliente("WSUP40", "Da Carteira", "Da Carteira Ltda",
                new Endereco("Centro", "Rua C", "3", null, null, new Cep("01001-000")), "São Paulo", "SP",
                new Telefone("11999999999"), new Cnpj(cnpj), null, null, "São Paulo", null, null, null,
                string.Empty, 0m, afiliada.UsuarioCadastro);
            db.Clientes.Add(cliente);
            await db.SaveChangesAsync();
            clienteId = cliente.Id;
            db.WlCarteiraClientes.Add(new WlCarteiraCliente(tenant, cliente.Id, 999, WlCarteiraOrigem.Cadastro));
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlCommandAsync("UPDATE Cliente SET StatusExibicao = 0 WHERE Id = @p0", cliente.Id);
        });
        approval.StatusCode.Should().Be(HttpStatusCode.OK, await approval.Content.ReadAsStringAsync());
        using var verify = new VeiculandoDataContext();
        (await verify.Clientes.SingleAsync(c => c.Id == clienteId)).StatusExibicao.Should().Be(StatusExibicaoEnum.Ativo);
        (await verify.Clientes.CountAsync(c => c.Cnpj.Numero == cnpj)).Should().Be(1);
    }

    private async Task<(HttpResponseMessage Approval, string Email)> AprovarKycAdAsync(int tenant, string cnpj, string cnpjCoreComMascara,
        Func<VeiculandoDataContext, Afiliada, Task> preparar = null)
    {
        var email = $"app-kyc-{tenant}@exemplo.com";
        var local = await Seed.LocalAsync(tenant, $"KY{tenant}");
        var piece = await Seed.PecaAsync(local, $"P-KY-{tenant}");
        await Seed.ReservaAsync(tenant, $"RES-KY-{tenant}", piece);
        await Seed.AnuncianteAsync(tenant, email);
        await Seed.OperadorAsync(tenant, $"revisor-{tenant}@exemplo.com", new[] { "ClienteGerenciar" });
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
            if (cnpjCoreComMascara != null)
            {
                var afiliada = await setup.Afiliadas.Include(a => a.UsuarioCadastro).SingleAsync(a => a.Id == tenant);
                setup.Clientes.Add(new Cliente($"MSK{tenant % 1000}", "Cliente Mascarado", "Cliente Mascarado Ltda",
                    new Endereco("Centro", "Rua B", "20", null, null, new Cep("01001-000")), "São Paulo", "SP",
                    new Telefone("11999999999"), new Cnpj(cnpjCoreComMascara), null, null, "São Paulo", null, null, null,
                    string.Empty, 0m, afiliada.UsuarioCadastro));
            }
            await setup.SaveChangesAsync();
            if (preparar != null)
                await preparar(setup, await setup.Afiliadas.Include(a => a.UsuarioCadastro).SingleAsync(a => a.Id == tenant));
        }

        using var factory = new WlApiFactory(_db, tenant);
        using var applicant = factory.ClienteAnonimo();
        var login = await applicant.PostAsJsonAsync("/api/wl/app/auth/login", new { email, password = Seed.SenhaPadrao });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        applicant.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            (await login.Content.ReadFromJsonAsync<TokenResponse>()).Token);
        var submit = await applicant.PostAsJsonAsync("/api/wl/app/kyc", new
        {
            accountType = "ad", tradeName = "Anunciante Novo", legalName = "Anunciante Novo Ltda",
            document = cnpj, phone = "11999999999", city = "São Paulo", state = "SP",
            street = "Rua Teste", number = "100", district = "Centro", zipCode = "01001000",
            representativeName = "Responsável Teste", representativeCpf = "52998224725",
            representativeEmail = email, representativePhone = "11999999999"
        });
        submit.StatusCode.Should().Be(HttpStatusCode.OK, await submit.Content.ReadAsStringAsync());
        using (var form = new MultipartFormDataContent())
        {
            foreach (var kind in new[] { "corporate", "representative" })
            {
                form.Add(new StringContent(kind), "types");
                var document = new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<<>>\nendobj"));
                document.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
                form.Add(document, "files", kind + ".pdf");
            }
            (await applicant.PostAsync("/api/wl/app/kyc/documents/batch", form)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var reviewer = await factory.ClienteAutenticadoAsync($"revisor-{tenant}@exemplo.com", Seed.SenhaPadrao);
        Guid onboardingId;
        using (var ctx = new VeiculandoDataContext())
            onboardingId = (await ctx.WlAppOnboardings.SingleAsync(o => o.AfiliadaId == tenant && o.Usuario.Email.Endereco == email)).Id;
        (await reviewer.PostAsync($"/api/wl/kyc/app/{onboardingId}/review", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        return (await reviewer.PostAsync($"/api/wl/kyc/app/{onboardingId}/approve", null), email);
    }

    private sealed record TokenResponse(string Token);
}
