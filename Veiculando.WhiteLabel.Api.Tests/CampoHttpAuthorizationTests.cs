using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Veiculando.WhiteLabel.Api.Configurations;
using Veiculando.WhiteLabel.Api.Controllers;
using Veiculando.WhiteLabel.Api.Middleware;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests
{
    public class CampoHttpAuthorizationTests
    {
        private static readonly string ChaveTeste = "campo-tests-" + new string('x', 40);

        [Theory]
        [InlineData("a.teste", "10", "13", 200)]
        [InlineData("b.teste", "10", "13", 401)]
        [InlineData("a.teste", null, "13", 403)]
        [InlineData("a.teste", "abc", "13", 403)]
        [InlineData("a.teste", "0", "13", 403)]
        [InlineData("a.teste", "10", null, 401)]
        public async Task Rota_herda_policy_e_valida_jwt_contra_host(
            string host, string operador, string afiliada, int status)
        {
            using var server = CriarServidor();
            using var client = server.CreateClient();
            client.DefaultRequestHeaders.Host = host;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(operador, afiliada));
            var resposta = await client.GetAsync("/api/wl/campo/sonda");
            ((int)resposta.StatusCode).Should().Be(status);
            if (status == 200)
                (await resposta.Content.ReadAsStringAsync()).Should().Be("{\"operadorId\":10,\"afiliadaId\":13}");
        }

        [Fact]
        public async Task Rota_de_campo_sem_token_retorna_401()
        {
            using var server = CriarServidor();
            using var client = server.CreateClient();
            client.DefaultRequestHeaders.Host = "a.teste";
            (await client.GetAsync("/api/wl/campo/sonda")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        private static TestServer CriarServidor() => new TestServer(new WebHostBuilder()
            .ConfigureServices(services => {
                var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> {
                    ["JwtSettings:Secret"] = ChaveTeste
                }).Build();
                services.AddJwtLocalAuthentication(config);
                services.AddScoped<ITenantContext, TenantContext>();
                services.AddWlAuthorization();
                services.AddControllers().AddApplicationPart(typeof(CampoSondaController).Assembly);
            })
            .Configure(app => {
                app.UseRouting();
                app.Use(async (context, next) => {
                    context.RequestServices.GetRequiredService<ITenantContext>().Definir(new WlTenantInfo {
                        AfiliadaId = context.Request.Host.Host == "a.teste" ? 13 : 46
                    });
                    await next();
                });
                app.UseAuthentication();
                app.UseMiddleware<TenantBindingMiddleware>();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapControllers());
            }));

        private static string Token(string operador, string afiliada)
        {
            var claims = new List<Claim> { new Claim(AuthorizationSetup.ClaimPermissao, AuthorizationSetup.PecaGerenciar) };
            if (operador != null) claims.Add(new Claim(CampoAuthController.ClaimUsuarioOperadorId, operador));
            if (afiliada != null) claims.Add(new Claim("AfiliadaId", afiliada));
            return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
                claims: claims, expires: DateTime.UtcNow.AddMinutes(5),
                signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.ASCII.GetBytes(ChaveTeste)), SecurityAlgorithms.HmacSha256)));
        }
    }

    [Route("api/wl/campo/sonda")]
    public sealed class CampoSondaController : CampoControllerBase
    {
        public CampoSondaController(ITenantContext tenant) : base(tenant) { }
        [HttpGet]
        public IActionResult Get() => Ok(new { operadorId = OperadorId, afiliadaId = AfiliadaId });
    }
}
