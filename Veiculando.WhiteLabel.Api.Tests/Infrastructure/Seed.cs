using System.Threading.Tasks;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities.WhiteLabel;
using BC = BCrypt.Net.BCrypt;

namespace Veiculando.WhiteLabel.Api.Tests.Infrastructure
{
    /// <summary>
    /// Dados de apoio para os testes.
    /// </summary>
    /// <remarks>
    /// Cada teste semeia o que precisa, com ids de afiliada proprios, em vez de
    /// existir um seed global. O banco e compartilhado por toda a suite (subir o
    /// container e caro) e xUnit roda classes em paralelo: seed global viraria
    /// interferencia entre testes, do tipo que so aparece quando a ordem muda.
    /// </remarks>
    public static class Seed
    {
        /// <summary>Senha usada por todos os operadores de teste.</summary>
        public const string SenhaPadrao = "SenhaDeTeste123";

        /// <summary>
        /// Cria um operador da exibidora e devolve o id.
        /// </summary>
        public static async Task<int> OperadorAsync(
            int afiliadaId,
            string email,
            string[]? permissoes = null,
            string nome = "Operador de Teste")
        {
            using var ctx = new VeiculandoDataContext();

            var operador = new WlUsuarioAfiliada(
                nome: nome,
                email: email,
                senhaHash: BC.HashPassword(SenhaPadrao),
                afiliadaId: afiliadaId,
                permissoes: permissoes ?? new string[0]);

            ctx.WlUsuariosAfiliada.Add(operador);
            await ctx.SaveChangesAsync();

            return operador.Id;
        }
    }
}
