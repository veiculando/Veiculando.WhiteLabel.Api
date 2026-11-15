using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Veiculando.WhiteLabel.Api.Middleware;

namespace Veiculando.WhiteLabel.Api.Services
{
    /// <summary>
    /// Como a equipe da exibidora reconhece um usuário WL (VEI-RD-160): pelo nome cadastrado
    /// em WL_UsuarioAfiliada; sem nome, pelo e-mail. Usado onde a carteira e o relatório
    /// mostram a vendedora.
    /// </summary>
    public static class NomesUsuarioWl
    {
        public static async Task<Dictionary<int, string>> ResolverAsync(
            ITenantQueries tenant, IEnumerable<int?> ids, CancellationToken ct)
        {
            var lista = ids.Where(id => id.HasValue).Select(id => id.Value).Distinct().ToList();
            if (lista.Count == 0) return new Dictionary<int, string>();

            var emails = await tenant.Usuarios.Where(u => lista.Contains(u.Id))
                .Select(u => new { u.Id, Email = u.Email.Endereco }).ToListAsync(ct);
            var nomes = await tenant.UsuariosAfiliada.Where(u => lista.Contains(u.Id))
                .Select(u => new { u.Id, u.Nome }).ToDictionaryAsync(u => u.Id, u => u.Nome, ct);

            return emails.ToDictionary(u => u.Id,
                u => nomes.TryGetValue(u.Id, out var nome) && !string.IsNullOrWhiteSpace(nome) ? nome.Trim() : u.Email);
        }

        public static async Task<string> ResolverAsync(ITenantQueries tenant, int? id, CancellationToken ct) =>
            id.HasValue && (await ResolverAsync(tenant, new[] { id }, ct)).TryGetValue(id.Value, out var nome) ? nome : null;
    }
}
