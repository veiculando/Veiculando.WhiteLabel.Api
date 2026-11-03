using System;
using System.Data.Entity;
using System.Threading;
using System.Threading.Tasks;
using Veiculando.Data.Contexts;

namespace Veiculando.WhiteLabel.Api.Services
{
    /// <summary>
    /// Serializa, entre todas as instâncias do BFF, as trocas de foto de uma peça.
    /// </summary>
    /// <remarks>
    /// <para>A foto vive no FileServer, fora de qualquer transação: sem a trava, duas
    /// trocas leriam a mesma foto anterior e uma poderia desfazer a outra.</para>
    ///
    /// <para>Usa <c>sp_getapplock</c> com dono <c>Transaction</c>, numa conexão própria:
    /// o dono <c>Session</c> só soltaria a trava quando o pool reutilizasse a conexão.
    /// A transação não toca em dados, e por isso não bloqueia nenhuma leitura.</para>
    ///
    /// <para>O Core não usa esta trava, então uma troca feita por lá ainda pode cair
    /// no meio de uma troca do WL. Ver VEI-RD-156.</para>
    /// </remarks>
    public sealed class FotoPecaTrava
    {
        private const int EsperaMs = 15_000;

        /// <returns>A trava, ou <c>null</c> se outra troca não terminou dentro da espera.</returns>
        public async Task<IDisposable> AdquirirAsync(int pecaId, CancellationToken ct)
        {
            var db = new VeiculandoDataContext();
            DbContextTransaction transacao = null;
            try
            {
                transacao = db.Database.BeginTransaction();
                var resultado = await db.Database.SqlQuery<int>(
                    "DECLARE @r int; EXEC @r = sp_getapplock @Resource = @p0, @LockMode = 'Exclusive', " +
                    "@LockOwner = 'Transaction', @LockTimeout = @p1; SELECT @r",
                    $"wl-foto-peca-{pecaId}", EsperaMs).SingleAsync(ct);
                if (resultado >= 0) return new Trava(db, transacao);
            }
            catch
            {
                transacao?.Dispose();
                db.Dispose();
                throw;
            }
            transacao.Dispose();
            db.Dispose();
            return null;
        }

        private sealed class Trava : IDisposable
        {
            private readonly VeiculandoDataContext _db;
            private readonly DbContextTransaction _transacao;

            public Trava(VeiculandoDataContext db, DbContextTransaction transacao) => (_db, _transacao) = (db, transacao);

            public void Dispose()
            {
                _transacao.Dispose();
                _db.Dispose();
            }
        }
    }
}
