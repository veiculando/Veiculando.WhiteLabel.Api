using System.Data.Entity;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities.WhiteLabel;
using Veiculando.Domain.Enums;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;
using BC = BCrypt.Net.BCrypt;

namespace Veiculando.WhiteLabel.Api.Tests
{
    /// <summary>
    /// Usuário da Exibidora antiga entrando no WL com a senha de sempre: no
    /// primeiro login vira WlUsuarioAfiliada; dali em diante é um usuário WL comum.
    /// </summary>
    [Collection(DatabaseCollection.Nome)]
    public class LoginUsuarioLegadoTests
    {
        private const int Afiliada = 7900;
        private readonly SqlServerFixture _db;

        public LoginUsuarioLegadoTests(SqlServerFixture db) => _db = db;

        [Fact]
        public async Task Primeiro_login_migra_o_legado_com_todas_as_permissoes()
        {
            const string email = "legado-ok@exemplo.com";
            await Seed.AfiliadaAsync(Afiliada);
            await Seed.UsuarioExibidoraLegadoAsync(Afiliada, email);

            using var factory = new WlApiFactory(_db, Afiliada);
            using var client = factory.ClienteAnonimo();

            var resposta = await client.PostAsJsonAsync("/api/wl/auth/login",
                new { Email = email.ToUpperInvariant(), Senha = Seed.SenhaPadrao });

            resposta.StatusCode.Should().Be(HttpStatusCode.OK);
            var corpo = await resposta.Content.ReadFromJsonAsync<LoginResposta>();
            corpo!.Permissoes.Should().BeEquivalentTo(WlPermissoesValidas.Lista);

            using var ctx = new VeiculandoDataContext();
            var wl = await ctx.WlUsuariosAfiliada.SingleAsync(u => u.Email.Endereco == email && u.AfiliadaId == Afiliada);
            wl.StatusConvite.Should().Be(StatusConviteWlEnum.Aceito);
            wl.Nome.Should().Be("Usuario Legado");
            wl.Cargo.Should().Be("Diretor");
            BC.Verify(Seed.SenhaPadrao, wl.SenhaHash).Should().BeTrue();

            // Segundo login já cai no fluxo WL, sem duplicar o registro.
            (await client.PostAsJsonAsync("/api/wl/auth/login", new { Email = email, Senha = Seed.SenhaPadrao }))
                .StatusCode.Should().Be(HttpStatusCode.OK);
            (await ctx.WlUsuariosAfiliada.CountAsync(u => u.Email.Endereco == email)).Should().Be(1);
        }

        [Fact]
        public async Task Senha_errada_nao_migra()
        {
            const string email = "legado-senha@exemplo.com";
            await Seed.AfiliadaAsync(Afiliada + 1);
            await Seed.UsuarioExibidoraLegadoAsync(Afiliada + 1, email);

            using var factory = new WlApiFactory(_db, Afiliada + 1);
            using var client = factory.ClienteAnonimo();

            var resposta = await client.PostAsJsonAsync("/api/wl/auth/login",
                new { Email = email, Senha = "outra-senha-qualquer" });

            resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            using var ctx = new VeiculandoDataContext();
            (await ctx.WlUsuarios.AnyAsync(u => u.Email.Endereco == email)).Should().BeFalse();
        }

        [Fact]
        public async Task Legado_de_outra_afiliada_nao_entra()
        {
            const string email = "legado-outra@exemplo.com";
            await Seed.AfiliadaAsync(Afiliada + 2);
            await Seed.AfiliadaAsync(Afiliada + 3);
            await Seed.UsuarioExibidoraLegadoAsync(Afiliada + 2, email);

            using var factory = new WlApiFactory(_db, Afiliada + 3);
            using var client = factory.ClienteAnonimo();

            var resposta = await client.PostAsJsonAsync("/api/wl/auth/login",
                new { Email = email, Senha = Seed.SenhaPadrao });

            resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task Legado_nao_aprovado_nao_entra()
        {
            const string email = "legado-pendente@exemplo.com";
            await Seed.AfiliadaAsync(Afiliada + 4);
            await Seed.UsuarioExibidoraLegadoAsync(Afiliada + 4, email, aprovado: false);

            using var factory = new WlApiFactory(_db, Afiliada + 4);
            using var client = factory.ClienteAnonimo();

            var resposta = await client.PostAsJsonAsync("/api/wl/auth/login",
                new { Email = email, Senha = Seed.SenhaPadrao });

            resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task Usuario_wl_excluido_nao_volta_pelo_legado()
        {
            const string email = "legado-excluido@exemplo.com";
            await Seed.AfiliadaAsync(Afiliada + 5);
            await Seed.UsuarioExibidoraLegadoAsync(Afiliada + 5, email);
            var wlId = await Seed.OperadorAsync(Afiliada + 5, email);
            using (var ctx = new VeiculandoDataContext())
            {
                var wl = await ctx.WlUsuariosAfiliada.SingleAsync(u => u.Id == wlId);
                wl.Deletar();
                await ctx.SaveChangesAsync();
            }

            using var factory = new WlApiFactory(_db, Afiliada + 5);
            using var client = factory.ClienteAnonimo();

            var resposta = await client.PostAsJsonAsync("/api/wl/auth/login",
                new { Email = email, Senha = Seed.SenhaPadrao });

            resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        private sealed record LoginResposta(string Token, string[] Permissoes);
    }
}
