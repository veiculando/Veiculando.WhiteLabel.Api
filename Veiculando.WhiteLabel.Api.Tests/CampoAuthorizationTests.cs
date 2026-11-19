using System.Security.Claims;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Veiculando.WhiteLabel.Api.Configurations;
using Veiculando.WhiteLabel.Api.Controllers;
using Veiculando.WhiteLabel.Api.Middleware;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests
{
    public class CampoAuthorizationTests
    {
        [Theory]
        [InlineData("10", "13", true, true)]
        [InlineData(null, "13", true, false)]
        [InlineData("", "13", true, false)]
        [InlineData("abc", "13", true, false)]
        [InlineData("0", "13", true, false)]
        [InlineData("-1", "13", true, false)]
        [InlineData("10", null, true, false)]
        [InlineData("10", "abc", true, false)]
        [InlineData("10", "46", true, false)]
        [InlineData("10", "13", false, false)]
        public async Task Campo_exige_operador_valido_autenticado_e_a_afiliada_do_host(
            string operador, string afiliada, bool autenticado, bool esperado)
        {
            var tenant = new TenantContext();
            tenant.Definir(new WlTenantInfo { AfiliadaId = 13 });
            using var provider = new ServiceCollection().AddLogging()
                .AddSingleton<ITenantContext>(tenant).AddWlAuthorization().BuildServiceProvider();
            var identity = new ClaimsIdentity(autenticado ? "Teste" : null);
            if (operador != null) identity.AddClaim(new Claim(CampoAuthController.ClaimUsuarioOperadorId, operador));
            if (afiliada != null) identity.AddClaim(new Claim("AfiliadaId", afiliada));
            var resultado = await provider.GetRequiredService<IAuthorizationService>()
                .AuthorizeAsync(new ClaimsPrincipal(identity), "Campo");
            resultado.Succeeded.Should().Be(esperado);
        }

        [Fact]
        public async Task Campo_recusa_tenant_nao_resolvido()
        {
            using var provider = new ServiceCollection().AddLogging()
                .AddSingleton<ITenantContext>(new TenantContext()).AddWlAuthorization().BuildServiceProvider();
            var user = new ClaimsPrincipal(new ClaimsIdentity(new[] {
                new Claim(CampoAuthController.ClaimUsuarioOperadorId, "10"), new Claim("AfiliadaId", "0")
            }, "Teste"));
            (await provider.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, "Campo"))
                .Succeeded.Should().BeFalse();
        }
    }
}
