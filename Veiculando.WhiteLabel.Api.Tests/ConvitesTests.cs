using System;
using System.Data.Entity;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Veiculando.Data.Contexts;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests
{
    [Collection(DatabaseCollection.Nome)]
    public class ConvitesTests
    {
        private readonly SqlServerFixture _db;
        public ConvitesTests(SqlServerFixture db) => _db = db;

        [Fact]
        public async Task Reenvio_revoga_token_antigo_e_nao_reconvida_usuario_aceito()
        {
            await Seed.OperadorAsync(7801, "admin-convites@exemplo.com", new[] { "UsuarioAfiliadaGerenciar" });
            using var factory = new WlApiFactory(_db, 7801);
            using var admin = await factory.ClienteAutenticadoAsync("admin-convites@exemplo.com", Seed.SenhaPadrao);
            var criacao = await admin.PostAsJsonAsync("/api/wl/usuarios", new { Nome = "Convidado", Email = "reenvio@exemplo.com" });
            var id = (await criacao.Content.ReadFromJsonAsync<Criado>()).Id;
            var antigo = Token(factory.EmailSender.Convites.Single().LinkPrimeiroAcesso);
            var reenvio = await admin.PostAsJsonAsync($"/api/wl/usuarios/{id}/reenviar-convite", new { });
            reenvio.StatusCode.Should().Be(HttpStatusCode.OK);
            var novo = Token(factory.EmailSender.Convites.Last().LinkPrimeiroAcesso);
            novo.Should().NotBe(antigo);

            using var anonimo = factory.ClienteAnonimo();
            var rejeitado = await anonimo.PostAsJsonAsync("/api/wl/auth/primeiro-acesso",
                new { Email = "reenvio@exemplo.com", Token = antigo, NovaSenha = "SenhaNova123!" });
            rejeitado.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var aceito = await anonimo.PostAsJsonAsync("/api/wl/auth/primeiro-acesso",
                new { Email = "reenvio@exemplo.com", Token = novo, NovaSenha = "SenhaNova123!" });
            aceito.StatusCode.Should().Be(HttpStatusCode.OK);
            var reconvite = await admin.PostAsJsonAsync($"/api/wl/usuarios/{id}/reenviar-convite", new { });
            reconvite.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }

        [Fact]
        public async Task Mesmo_email_em_dois_hosts_nao_permite_usar_convite_do_outro_tenant()
        {
            const string email = "igual-convite@exemplo.com";
            await Seed.OperadorAsync(7802, "admin-a@exemplo.com", new[] { "UsuarioAfiliadaGerenciar" });
            await Seed.OperadorAsync(7803, "admin-b@exemplo.com", new[] { "UsuarioAfiliadaGerenciar" });
            using var a = new WlApiFactory(_db, 7802);
            using var b = new WlApiFactory(_db, 7803);
            using var adminA = await a.ClienteAutenticadoAsync("admin-a@exemplo.com", Seed.SenhaPadrao);
            using var adminB = await b.ClienteAutenticadoAsync("admin-b@exemplo.com", Seed.SenhaPadrao);
            var createdA = await adminA.PostAsJsonAsync("/api/wl/usuarios", new { Nome = "A", Email = email });
            var createdB = await adminB.PostAsJsonAsync("/api/wl/usuarios", new { Nome = "B", Email = email });
            createdA.StatusCode.Should().Be(HttpStatusCode.Created);
            createdB.StatusCode.Should().Be(HttpStatusCode.Created);
            var idA = (await createdA.Content.ReadFromJsonAsync<Criado>()).Id;
            var idor = await adminB.PostAsJsonAsync($"/api/wl/usuarios/{idA}/reenviar-convite", new { });
            idor.StatusCode.Should().Be(HttpStatusCode.NotFound);
            using var anonB = b.ClienteAnonimo();
            var tokenA = Token(a.EmailSender.Convites.Single().LinkPrimeiroAcesso);
            var cross = await anonB.PostAsJsonAsync("/api/wl/auth/primeiro-acesso",
                new { Email = email, Token = tokenA, NovaSenha = "SenhaNova123!" });
            var inexistente = await anonB.PostAsJsonAsync("/api/wl/auth/primeiro-acesso",
                new { Email = "inexistente@exemplo.com", Token = tokenA, NovaSenha = "SenhaNova123!" });
            cross.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await cross.Content.ReadAsStringAsync()).Should().Be(await inexistente.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task Falha_de_reenvio_invalida_token_sem_perder_usuario_e_expirado_nao_aceita()
        {
            await Seed.OperadorAsync(7804, "admin-falha2@exemplo.com", new[] { "UsuarioAfiliadaGerenciar" });
            using var factory = new WlApiFactory(_db, 7804);
            using var admin = await factory.ClienteAutenticadoAsync("admin-falha2@exemplo.com", Seed.SenhaPadrao);
            var criacao = await admin.PostAsJsonAsync("/api/wl/usuarios", new { Nome = "Convidado", Email = "falha2@exemplo.com" });
            var id = (await criacao.Content.ReadFromJsonAsync<Criado>()).Id;
            factory.EmailSender.FalharProximoEnvio = true;
            var falha = await admin.PostAsJsonAsync($"/api/wl/usuarios/{id}/reenviar-convite", new { });
            falha.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            using (var db = new VeiculandoDataContext())
            {
                var usuario = await db.WlUsuariosAfiliada.SingleAsync(u => u.Id == id);
                usuario.TokenConviteHash.Should().BeNull();
                usuario.SenhaHash.Should().BeNull();
            }
            var retry = await admin.PostAsJsonAsync($"/api/wl/usuarios/{id}/reenviar-convite", new { });
            retry.StatusCode.Should().Be(HttpStatusCode.OK);
            var token = Token(factory.EmailSender.Convites.Last().LinkPrimeiroAcesso);
            using (var db = new VeiculandoDataContext())
                await db.Database.ExecuteSqlCommandAsync("UPDATE dbo.WL_Usuario SET ValidadeTokenConvite=DATEADD(hour,-1,GETUTCDATE()) WHERE Id=@p0", id);
            using var anonimo = factory.ClienteAnonimo();
            var expirado = await anonimo.PostAsJsonAsync("/api/wl/auth/primeiro-acesso",
                new { Email = "falha2@exemplo.com", Token = token, NovaSenha = "SenhaNova123!" });
            expirado.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        private static string Token(string link) => QueryHelpers.ParseQuery(new Uri(link).Query)["token"].ToString();

        [Fact]
        public async Task Aceites_concorrentes_consumem_o_convite_uma_unica_vez_sem_500()
        {
            await Seed.OperadorAsync(7805, "admin-race@exemplo.com", new[] { "UsuarioAfiliadaGerenciar" });
            using var factory = new WlApiFactory(_db, 7805);
            using var admin = await factory.ClienteAutenticadoAsync("admin-race@exemplo.com", Seed.SenhaPadrao);
            var criacao = await admin.PostAsJsonAsync("/api/wl/usuarios", new { Nome = "Concorrente", Email = "race@exemplo.com" });
            criacao.StatusCode.Should().Be(HttpStatusCode.Created);
            var token = Token(factory.EmailSender.Convites.Single().LinkPrimeiroAcesso);
            using (var db = new VeiculandoDataContext())
            {
                var pendente = await db.WlUsuariosAfiliada.SingleAsync(u => u.Email.Endereco == "race@exemplo.com" && u.AfiliadaId == 7805);
                pendente.TokenConviteHash.Should().NotBe(token);
                pendente.DataEnvioConvite.Should().NotBeNull();
            }
            using var primeiro = factory.ClienteAnonimo();
            using var segundo = factory.ClienteAnonimo();
            var respostas = await Task.WhenAll(
                primeiro.PostAsJsonAsync("/api/wl/auth/primeiro-acesso", new { Email = "race@exemplo.com", Token = token, NovaSenha = "PrimeiraSenha123!" }),
                segundo.PostAsJsonAsync("/api/wl/auth/primeiro-acesso", new { Email = "race@exemplo.com", Token = token, NovaSenha = "SegundaSenha123!" }));
            respostas.Select(r => r.StatusCode).Should().BeEquivalentTo(new[] { HttpStatusCode.OK, HttpStatusCode.BadRequest });
            foreach (var resposta in respostas)
                (await resposta.Content.ReadAsStringAsync()).Should().NotContain("\"token\"");
        }

        private sealed record Criado(int Id);
    }
}
