using System;
using System.Data.Entity;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Enums;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests
{
    [Collection(DatabaseCollection.Nome)]
    public class UsuariosExcluidosTests
    {
        private readonly SqlServerFixture _db;
        public UsuariosExcluidosTests(SqlServerFixture db) => _db = db;

        [Fact]
        public async Task Excluidos_sao_opt_in_do_tenant_e_expoem_data_sem_segredos()
        {
            const int tenant = 8900;
            var admin = "admin-excluidos@exemplo.com";
            await Seed.OperadorAsync(tenant, admin, new[] { "UsuarioAfiliadaGerenciar" });
            var excluido = await Seed.OperadorAsync(tenant, "excluido@exemplo.com");
            var outro = await Seed.OperadorAsync(8901, "outro-excluido@exemplo.com");
            using (var db = new VeiculandoDataContext())
            {
                (await db.WlUsuariosAfiliada.SingleAsync(u => u.Id == outro)).Deletar();
                await db.SaveChangesAsync();
            }
            using var factory = new WlApiFactory(_db, tenant);
            using var client = await factory.ClienteAutenticadoAsync(admin, Seed.SenhaPadrao);
            (await client.DeleteAsync($"/api/wl/usuarios/{excluido}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

            var ativos = await client.GetFromJsonAsync<Usuario[]>("/api/wl/usuarios");
            ativos.Should().NotContain(u => u.Id == excluido || u.Id == outro);
            ativos.Should().OnlyContain(u => !u.Excluido && u.DataExclusao == null);
            var response = await client.GetAsync("/api/wl/usuarios?incluirExcluidos=true");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var todos = await response.Content.ReadFromJsonAsync<Usuario[]>();
            var removido = todos.Should().ContainSingle(u => u.Id == excluido).Which;
            removido.Excluido.Should().BeTrue();
            removido.DataExclusao.Should().NotBeNull();
            removido.DataExclusao.Value.Kind.Should().Be(DateTimeKind.Utc);
            todos.Should().NotContain(u => u.Id == outro);
            todos.Should().Contain(u => u.Email == admin && !u.Excluido);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            foreach (var row in json.RootElement.EnumerateArray())
            {
                row.TryGetProperty("senhaHash", out _).Should().BeFalse();
                row.TryGetProperty("tokenConviteHash", out _).Should().BeFalse();
                row.TryGetProperty("tokenRecuperacaoHash", out _).Should().BeFalse();
            }
            using var check = new VeiculandoDataContext();
            var persisted = await check.WlUsuariosAfiliada.SingleAsync(u => u.Id == excluido);
            persisted.StatusExibicao.Should().Be(StatusExibicaoEnum.Deletado);
            persisted.DataExclusao.Should().NotBeNull();
        }

        [Fact]
        public async Task Consulta_de_excluidos_nao_libera_mutacoes_nem_reutilizacao_de_email()
        {
            const int tenant = 8902;
            var admin = "admin-leitura-excluido@exemplo.com";
            var email = "historico@exemplo.com";
            await Seed.OperadorAsync(tenant, admin, new[] { "UsuarioAfiliadaGerenciar" });
            var id = await Seed.OperadorAsync(tenant, email);
            using var factory = new WlApiFactory(_db, tenant);
            using var client = await factory.ClienteAutenticadoAsync(admin, Seed.SenhaPadrao);
            (await client.DeleteAsync($"/api/wl/usuarios/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
            (await client.GetAsync($"/api/wl/usuarios/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await client.PutAsJsonAsync($"/api/wl/usuarios/{id}", new { Nome = "Alterado" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await client.PostAsJsonAsync($"/api/wl/usuarios/{id}/reenviar-convite", new { })).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await client.DeleteAsync($"/api/wl/usuarios/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await client.PostAsJsonAsync("/api/wl/usuarios", new { Nome = "Novo", Email = email })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Fact]
        public async Task Incluir_excluidos_exige_autenticacao_e_permissao_de_operadores()
        {
            const int tenant = 8903;
            var email = "sem-permissao-excluidos@exemplo.com";
            await Seed.OperadorAsync(tenant, email);
            using var factory = new WlApiFactory(_db, tenant);
            using var anonimo = factory.ClienteAnonimo();
            (await anonimo.GetAsync("/api/wl/usuarios?incluirExcluidos=true")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            using var client = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);
            (await client.GetAsync("/api/wl/usuarios?incluirExcluidos=true")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        private sealed record Usuario(int Id, string Email, bool Excluido, DateTime? DataExclusao);
    }
}
