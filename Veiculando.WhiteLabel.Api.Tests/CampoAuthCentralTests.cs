using System;
using System.Data.Entity;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities.WhiteLabel;
using Veiculando.WhiteLabel.Api.Services;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests;

/// <summary>
/// VEI-RD-181: o app de campo entra só com e-mail e senha. As rotas centrais
/// procuram o e-mail em todas as afiliadas, seja qual for o Host da chamada.
/// </summary>
[Collection(DatabaseCollection.Nome)]
public class CampoAuthCentralTests
{
    // A e B têm operadores; C é só o Host fixo do app; D não tem domínio ativo.
    private const int AfiliadaA = 8981;
    private const int AfiliadaB = 8982;
    private const int AfiliadaC = 8983;
    private const int AfiliadaD = 8984;
    private const string HostA = "exibidora.central-a.teste";
    private const string HostB = "exibidora.central-b.teste";
    private const string HostC = "exibidora.central-c.teste";
    private const string OutraSenha = "OutraSenhaCampo1";

    private readonly SqlServerFixture _db;
    public CampoAuthCentralTests(SqlServerFixture db) => _db = db;

    private static string NovoEmail(string prefixo) => $"{prefixo}-{Guid.NewGuid().ToString("N")[..12]}@exemplo.com";

    private static async Task PrepararExibidoras()
    {
        await Seed.DominioAsync(AfiliadaA, HostA, ativo: true);
        await Seed.BrandingAsync(AfiliadaA, "Exibidora Alfa", "https://cdn.teste/alfa.png", "#112233");
        await Seed.DominioAsync(AfiliadaB, HostB, ativo: true);
        await Seed.BrandingAsync(AfiliadaB, "Exibidora Beta", "https://cdn.teste/beta.png", "#445566");
        await Seed.DominioAsync(AfiliadaC, HostC, ativo: true);
        await Seed.AfiliadaAsync(AfiliadaD);
    }

    private static async Task<int> CriarCore(string email, int afiliada, string senhaCore)
    {
        using var ctx = new VeiculandoDataContext();
        // Perfil 1 garantido aqui: a suíte pode rodar este arquivo sozinho.
        await ctx.Database.ExecuteSqlCommandAsync(@"
IF NOT EXISTS (SELECT 1 FROM PerfilUsuario WHERE Id = 1)
BEGIN
    SET IDENTITY_INSERT PerfilUsuario ON;
    INSERT INTO PerfilUsuario (Id, Nome, Codigo, DataCadastro, DataAtualizacao, StatusExibicao)
    VALUES (1, 'Anunciante', 'ANUN', GETDATE(), GETDATE(), 1);
    SET IDENTITY_INSERT PerfilUsuario OFF;
END
INSERT Usuario (Nome, Email, Senha, StatusAprovacao, Acessos, DataUltimoLogin,
 EmailConfirmado, DataCadastro, DataAtualizacao, StatusExibicao, IdPerfil)
VALUES ('Operador Central', @p0, @p2, 1, 0, GETUTCDATE(), 1, GETUTCDATE(), GETUTCDATE(), 1, 1);
INSERT Usuario_Operador (Id, IdAfiliada, Colador, Fiscal) VALUES (SCOPE_IDENTITY(), @p1, 1, 1);",
            email, afiliada, SenhaUsuarioCore.Hash(senhaCore));
        return await ctx.UsuariosOperador.Where(u => u.Email.Endereco == email && u.IdAfiliada == afiliada)
            .Select(u => u.Id).SingleAsync();
    }

    // UK_Usuario_Email: o e-mail é único no Core. O mesmo e-mail em duas exibidoras
    // só existe com as duas linhas WL apontando para o mesmo Usuario_Operador.
    private static async Task CriarOperadorWl(string email, int afiliada, string? senha, int? coreExistente = null)
    {
        var coreId = coreExistente ?? await CriarCore(email, afiliada, "senha-do-core-nao-usada");
        using var ctx = new VeiculandoDataContext();
        var core = await ctx.UsuariosOperador.SingleAsync(u => u.Id == coreId);
        var wl = new WLUsuario_Operador(email, senha == null ? null : BCrypt.Net.BCrypt.HashPassword(senha), afiliada);
        wl.ApontarPara(core);
        ctx.WlUsuariosOperador.Add(wl);
        await ctx.SaveChangesAsync();
    }

    private static async Task<int> CoreId(string email)
    {
        using var ctx = new VeiculandoDataContext();
        return await ctx.UsuariosOperador.Where(u => u.Email.Endereco == email).Select(u => u.Id).SingleAsync();
    }

    private static async Task<CampoExibidoraDto[]> Exibidoras(HttpResponseMessage resposta)
    {
        resposta.StatusCode.Should().Be(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        var corpo = await resposta.Content.ReadFromJsonAsync<CampoExibidorasDto>();
        return corpo!.Exibidoras;
    }

    [Fact]
    public async Task Pelo_host_fixo_do_app_acha_a_exibidora_do_operador_e_o_login_segue_nela()
    {
        await PrepararExibidoras();
        var email = NovoEmail("um");
        await CriarOperadorWl(email, AfiliadaA, Seed.SenhaPadrao);
        using var factory = new WlApiFactory(_db, AfiliadaC, HostC);

        var resposta = await factory.ClienteAnonimo(HostC).PostAsJsonAsync("/api/wl/campo/auth/central/exibidoras",
            new { email = "  " + email.ToUpperInvariant() + " ", senha = Seed.SenhaPadrao });

        var exibidora = (await Exibidoras(resposta)).Should().ContainSingle().Which;
        exibidora.Host.Should().Be(HostA);
        exibidora.NomeExibicao.Should().Be("Exibidora Alfa");
        exibidora.LogoUrl.Should().Be("https://cdn.teste/alfa.png");
        exibidora.PrimaryColor.Should().Be("#112233");
        (await resposta.Content.ReadAsStringAsync()).Should().NotContain(AfiliadaA.ToString());

        (await factory.ClienteAnonimo(exibidora.Host).PostAsJsonAsync("/api/wl/campo/auth/login",
            new { email, senha = Seed.SenhaPadrao })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Mesmo_email_em_duas_exibidoras_so_lista_as_em_que_a_senha_confere()
    {
        await PrepararExibidoras();
        var email = NovoEmail("dois");
        await CriarOperadorWl(email, AfiliadaA, Seed.SenhaPadrao);
        await CriarOperadorWl(email, AfiliadaB, Seed.SenhaPadrao, await CoreId(email));
        using var factory = new WlApiFactory(_db, AfiliadaC, HostC);
        var client = factory.ClienteAnonimo(HostC);

        var ambas = await Exibidoras(await client.PostAsJsonAsync("/api/wl/campo/auth/central/exibidoras",
            new { email, senha = Seed.SenhaPadrao }));
        ambas.Select(e => e.Host).Should().Equal(HostA, HostB);

        var outraEmail = NovoEmail("dois-senhas");
        await CriarOperadorWl(outraEmail, AfiliadaA, Seed.SenhaPadrao);
        await CriarOperadorWl(outraEmail, AfiliadaB, OutraSenha, await CoreId(outraEmail));
        var soB = await Exibidoras(await client.PostAsJsonAsync("/api/wl/campo/auth/central/exibidoras",
            new { email = outraEmail, senha = OutraSenha }));
        soB.Select(e => e.Host).Should().Equal(HostB);
    }

    [Fact]
    public async Task Senha_errada_e_email_desconhecido_recebem_a_mesma_recusa()
    {
        await PrepararExibidoras();
        var email = NovoEmail("recusa");
        await CriarOperadorWl(email, AfiliadaA, Seed.SenhaPadrao);
        using var factory = new WlApiFactory(_db, AfiliadaC, HostC);
        var client = factory.ClienteAnonimo(HostC);

        var errada = await client.PostAsJsonAsync("/api/wl/campo/auth/central/exibidoras", new { email, senha = "errada" });
        var desconhecido = await client.PostAsJsonAsync("/api/wl/campo/auth/central/exibidoras",
            new { email = NovoEmail("ninguem"), senha = Seed.SenhaPadrao });
        var vazio = await client.PostAsJsonAsync("/api/wl/campo/auth/central/exibidoras", new { email, senha = "" });

        errada.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        desconhecido.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await errada.Content.ReadAsStringAsync()).Should().Be(await desconhecido.Content.ReadAsStringAsync());
        vazio.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Convite_pendente_e_exibidora_sem_dominio_ativo_nao_entram_na_lista()
    {
        await PrepararExibidoras();
        var pendente = NovoEmail("pendente");
        await CriarOperadorWl(pendente, AfiliadaA, senha: null);
        var semDominio = NovoEmail("sem-dominio");
        await CriarOperadorWl(semDominio, AfiliadaD, Seed.SenhaPadrao);
        using var factory = new WlApiFactory(_db, AfiliadaC, HostC);
        var client = factory.ClienteAnonimo(HostC);

        (await client.PostAsJsonAsync("/api/wl/campo/auth/central/exibidoras", new { email = pendente, senha = Seed.SenhaPadrao }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.PostAsJsonAsync("/api/wl/campo/auth/central/exibidoras", new { email = semDominio, senha = Seed.SenhaPadrao }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Operador_so_do_core_tambem_e_encontrado()
    {
        await PrepararExibidoras();
        var email = NovoEmail("core");
        await CriarCore(email, AfiliadaB, Seed.SenhaPadrao);
        using var factory = new WlApiFactory(_db, AfiliadaC, HostC);

        var exibidoras = await Exibidoras(await factory.ClienteAnonimo(HostC).PostAsJsonAsync(
            "/api/wl/campo/auth/central/exibidoras", new { email, senha = Seed.SenhaPadrao }));

        exibidoras.Select(e => e.Host).Should().Equal(HostB);
    }

    [Fact]
    public async Task Esqueci_senha_central_manda_o_link_no_dominio_da_exibidora_e_a_troca_funciona_nele()
    {
        await PrepararExibidoras();
        var email = NovoEmail("esq");
        await CriarOperadorWl(email, AfiliadaA, Seed.SenhaPadrao);
        using var factory = new WlApiFactory(_db, AfiliadaC, HostC);

        (await factory.ClienteAnonimo(HostC).PostAsJsonAsync("/api/wl/campo/auth/central/esqueci-senha", new { email }))
            .EnsureSuccessStatusCode();

        var link = factory.EmailSender.Envios.Should().ContainSingle().Which.LinkReset;
        link.Should().StartWith($"https://{HostA}/api/wl/campo/acesso?tipo=alterar-senha&");
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(link).Query);
        var hostA = factory.ClienteAnonimo(HostA);
        (await hostA.PostAsJsonAsync("/api/wl/campo/auth/alterar-senha",
            new { email = query["email"], token = query["token"], novaSenha = OutraSenha })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await hostA.PostAsJsonAsync("/api/wl/campo/auth/login", new { email, senha = OutraSenha }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Primeiro_acesso_central_manda_um_convite_por_exibidora_e_responde_igual_para_email_desconhecido()
    {
        await PrepararExibidoras();
        var email = NovoEmail("pa");
        await CriarOperadorWl(email, AfiliadaA, senha: null);
        await CriarOperadorWl(email, AfiliadaB, senha: null, await CoreId(email));
        using var factory = new WlApiFactory(_db, AfiliadaC, HostC);
        var client = factory.ClienteAnonimo(HostC);

        var conhecido = await client.PostAsJsonAsync("/api/wl/campo/auth/central/primeiro-acesso", new { email });
        var desconhecido = await client.PostAsJsonAsync("/api/wl/campo/auth/central/primeiro-acesso", new { email = NovoEmail("x") });

        conhecido.StatusCode.Should().Be(HttpStatusCode.OK);
        (await conhecido.Content.ReadAsStringAsync()).Should().Be(await desconhecido.Content.ReadAsStringAsync());
        factory.EmailSender.Convites.Select(c => new Uri(c.LinkPrimeiroAcesso).Host)
            .Should().BeEquivalentTo(new[] { HostA, HostB });
    }

    private sealed class CampoExibidorasDto
    {
        public CampoExibidoraDto[] Exibidoras { get; set; } = Array.Empty<CampoExibidoraDto>();
    }

    private sealed class CampoExibidoraDto
    {
        public string Host { get; set; } = "";
        public string NomeExibicao { get; set; } = "";
        public string? LogoUrl { get; set; }
        public string? PrimaryColor { get; set; }
    }
}
