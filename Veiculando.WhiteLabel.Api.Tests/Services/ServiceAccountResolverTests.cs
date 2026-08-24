using Moq;
using Veiculando.Domain.Entities;
using Veiculando.Domain.Repositories;
using Veiculando.WhiteLabel.Api.Configurations;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;
using Veiculando.WhiteLabel.Api.Tests.TestHelpers;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests.Services
{
    public class ServiceAccountResolverTests
    {
        private static UsuarioAfiliada BuildUsuarioAfiliada(int idAfiliada)
        {
            // UsuarioAfiliada não tem construtor público trivial no domínio real;
            // usamos reflection para popular só o que o teste precisa validar.
            var usuario = ReflectionTestHelpers.CreateUninitialized<UsuarioAfiliada>();
            ReflectionTestHelpers.SetPrivate(usuario, "IdAfiliada", idAfiliada);
            ReflectionTestHelpers.SetPrivate(usuario, "Id", 42);
            return usuario;
        }

        [Fact]
        public void Resolve_retorna_usuario_quando_email_do_seed_pertence_ao_tenant_da_instancia()
        {
            var usuario = BuildUsuarioAfiliada(idAfiliada: 7);
            var repoMock = new Mock<IUsuarioAfiliadaRepository>();
            repoMock.Setup(r => r.RetornaPorEmail("seed@wl.local")).Returns(usuario);

            var tenantMock = new Mock<ITenantContext>();
            tenantMock.SetupGet(t => t.AfiliadaId).Returns(7);

            var options = Microsoft.Extensions.Options.Options.Create(new SeedAccountOptions { Email = "seed@wl.local" });
            var resolver = new ServiceAccountResolver(repoMock.Object, tenantMock.Object, options);

            var resolved = resolver.Resolve();

            Assert.NotNull(resolved);
            Assert.Equal(42, resolved!.Id);
        }

        [Fact]
        public void Resolve_lanca_quando_conta_de_servico_nao_e_UsuarioAfiliada_do_tenant_da_instancia()
        {
            // ADR-WL-004: se a conta de serviço não for UsuarioAfiliada do
            // AfiliadaId da instância, o Core silenciosamente aprova o local
            // (SetAprovado) em vez de enfileirar — e a guarda de tenant do
            // core é pulada. Isso deve falhar alto, nunca ser ignorado.
            var repoMock = new Mock<IUsuarioAfiliadaRepository>();
            repoMock.Setup(r => r.RetornaPorEmail(It.IsAny<string>())).Returns((UsuarioAfiliada)null);

            var tenantMock = new Mock<ITenantContext>();
            tenantMock.SetupGet(t => t.AfiliadaId).Returns(7);

            var options = Microsoft.Extensions.Options.Options.Create(new SeedAccountOptions { Email = "seed@wl.local" });
            var resolver = new ServiceAccountResolver(repoMock.Object, tenantMock.Object, options);

            Assert.Throws<System.InvalidOperationException>(() => resolver.Resolve());
        }

        [Fact]
        public void Resolve_lanca_quando_usuario_encontrado_pertence_a_outra_afiliada()
        {
            var usuario = BuildUsuarioAfiliada(idAfiliada: 99); // afiliada errada
            var repoMock = new Mock<IUsuarioAfiliadaRepository>();
            repoMock.Setup(r => r.RetornaPorEmail("seed@wl.local")).Returns(usuario);

            var tenantMock = new Mock<ITenantContext>();
            tenantMock.SetupGet(t => t.AfiliadaId).Returns(7);

            var options = Microsoft.Extensions.Options.Options.Create(new SeedAccountOptions { Email = "seed@wl.local" });
            var resolver = new ServiceAccountResolver(repoMock.Object, tenantMock.Object, options);

            Assert.Throws<System.InvalidOperationException>(() => resolver.Resolve());
        }
    }
}
