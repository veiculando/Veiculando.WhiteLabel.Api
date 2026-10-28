using System;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Veiculando.Domain.Entities.OrdensServico;
using Veiculando.WhiteLabel.Api.Controllers;
using Veiculando.WhiteLabel.Api.Middleware;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests
{
    public class CampoControllerBaseTests
    {
        [Fact]
        public void Base_exige_a_policy_de_campo()
        {
            var policy = typeof(CampoControllerBase).GetCustomAttributes(typeof(AuthorizeAttribute), true)
                .Cast<AuthorizeAttribute>().Single();
            policy.Policy.Should().Be("Campo");
        }

        [Fact]
        public void Identidade_usa_o_operador_do_token_e_a_afiliada_resolvida_no_servidor()
        {
            var tenant = new TenantContext();
            tenant.Definir(new WlTenantInfo { AfiliadaId = 13 });
            var controller = new SondaCampo(tenant) {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[] {
                        new Claim(CampoAuthController.ClaimUsuarioOperadorId, "44"),
                        new Claim("WlUsuarioId", "99"), new Claim("AfiliadaId", "13")
                    }, "Teste"))
                }}
            };
            controller.Identidade.Should().Be((44, 13));
        }

        [Theory]
        [InlineData(RecusaCampo.SomenteHistorico)]
        [InlineData(RecusaCampo.OrdemCancelada)]
        [InlineData(RecusaCampo.NaoAtribuido)]
        [InlineData(RecusaCampo.OrdemConcluida)]
        [InlineData(RecusaCampo.ForaDePendente)]
        [InlineData(RecusaCampo.ForaDeColagem)]
        [InlineData(RecusaCampo.PecaInexistente)]
        [InlineData(RecusaCampo.PecaNaoColada)]
        public void Recusa_preserva_codigo_do_core_com_409_e_mensagem(string codigo)
        {
            var resposta = new SondaCampo(new TenantContext()).Recusar(codigo);
            resposta.StatusCode.Should().Be(StatusCodes.Status409Conflict);
            var corpo = JsonSerializer.SerializeToElement(resposta.Value);
            corpo.GetProperty("codigo").GetString().Should().Be(codigo);
            corpo.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        }

        private sealed class SondaCampo : CampoControllerBase
        {
            public SondaCampo(ITenantContext tenant) : base(tenant) { }
            public (int, int) Identidade => (OperadorId, AfiliadaId);
            public ConflictObjectResult Recusar(string codigo) => RecusarCampo(codigo);
        }
    }
}
