using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Veiculando.Domain.Entities.WhiteLabel;
using Veiculando.WhiteLabel.Api.Configurations;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests
{
    /// <summary>
    /// As policies do BFF espelham <c>WlPermissoesValidas</c> item a item: uma permissão
    /// do domínio sem policy aqui fica concedível na tela e sem efeito na API.
    /// </summary>
    public class AuthorizationSetupTests
    {
        [Fact]
        public void Todas_espelha_a_whitelist_do_dominio()
        {
            AuthorizationSetup.Todas.Should().BeEquivalentTo(WlPermissoesValidas.Lista);
        }

        [Fact]
        public async Task Policy_ConteudoGerenciar_exige_a_propria_claim_e_nao_a_de_admin()
        {
            // VEI-RD-106: marketing é persona própria. Admin de operadores sem
            // ConteudoGerenciar não passa; quem tem só ConteudoGerenciar passa.
            var provider = new ServiceCollection().AddLogging().AddWlAuthorization().BuildServiceProvider();
            var authorization = provider.GetRequiredService<IAuthorizationService>();

            var admin = Operador(AuthorizationSetup.UsuarioAfiliadaGerenciar);
            var marketing = Operador(AuthorizationSetup.ConteudoGerenciar);

            (await authorization.AuthorizeAsync(admin, AuthorizationSetup.ConteudoGerenciar))
                .Succeeded.Should().BeFalse();
            (await authorization.AuthorizeAsync(marketing, AuthorizationSetup.ConteudoGerenciar))
                .Succeeded.Should().BeTrue();
        }

        private static ClaimsPrincipal Operador(params string[] permissoes) =>
            new ClaimsPrincipal(new ClaimsIdentity(
                permissoes.Select(p => new Claim(AuthorizationSetup.ClaimPermissao, p)),
                authenticationType: "Teste"));
    }
}
