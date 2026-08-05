using System.Linq;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities;
using Veiculando.Domain.Entities.Pedidos;
using Veiculando.Domain.Entities.WhiteLabel;

namespace Veiculando.WhiteLabel.Api.Middleware
{
    /// <summary>
    /// Superfície de consulta já recortada pela afiliada da instância.
    /// </summary>
    /// <remarks>
    /// <para><b>O problema que isto resolve.</b> O isolamento entre exibidoras
    /// dependia de cada endpoint lembrar de escrever <c>IdAfiliada == afiliadaId</c>
    /// no <c>Where</c> — mais de uma dezena de lugares, cada um uma chance de
    /// esquecer. Estava correto em todos na revisão da Sprint 9.0, mas correção
    /// por disciplina se degrada: basta um endpoint novo escrito às pressas.</para>
    ///
    /// <para>Com esta interface o filtro deixa de ser lembrado e passa a ser
    /// estrutural. Os controllers não recebem mais o <see cref="VeiculandoDataContext"/>
    /// para leitura, e sim esta superfície — não há como consultar sem recorte
    /// porque o <c>DbSet</c> cru não está ao alcance.</para>
    ///
    /// <para><b>Por que não um global query filter.</b> É a solução natural, mas é
    /// recurso do EF Core. Este projeto usa EF6, que não tem
    /// <c>HasQueryFilter</c>. Interceptar comandos no nível do ADO seria frágil e
    /// invisível; uma superfície explícita é mais chata de escrever e muito mais
    /// fácil de auditar.</para>
    ///
    /// <para><b>Escrita continua no contexto.</b> <c>SaveChangesAsync</c> e o
    /// rastreamento de entidades seguem no <see cref="VeiculandoDataContext"/>,
    /// que os controllers ainda injetam quando precisam gravar. Esta interface
    /// cuida do recorte de LEITURA, que é onde o vazamento aconteceria.</para>
    /// </remarks>
    public interface ITenantQueries
    {
        /// <summary>Afiliada desta instância, para os poucos casos que precisam do valor.</summary>
        int AfiliadaId { get; }

        /// <summary>Operadores da exibidora (hierarquia concreta).</summary>
        IQueryable<WlUsuarioAfiliada> UsuariosAfiliada { get; }

        /// <summary>Base da hierarquia — usada na checagem de e-mail duplicado.</summary>
        IQueryable<WlUsuario> Usuarios { get; }

        /// <summary>Locais da exibidora, em qualquer status.</summary>
        IQueryable<Local> Locais { get; }

        /// <summary>Peças cujo local pertence à exibidora.</summary>
        IQueryable<Peca> Pecas { get; }

        IQueryable<PedidoReserva> PedidosReserva { get; }

        IQueryable<PedidoInsercao> PedidosInsercao { get; }

        /// <summary>Itens de PI, recortados pela afiliada do pedido pai.</summary>
        IQueryable<PedidoInsercaoItem> PedidoInsercaoItens { get; }

        /// <summary>Grade de disponibilidade, recortada pela afiliada do local da peça.</summary>
        IQueryable<PecaPeriodoStatus> PecaPeriodoStatus { get; }
    }

    /// <inheritdoc />
    public sealed class TenantQueries : ITenantQueries
    {
        private readonly VeiculandoDataContext _db;
        private readonly ITenantContext _tenant;

        public TenantQueries(VeiculandoDataContext db, ITenantContext tenant)
        {
            _db = db;
            _tenant = tenant;
        }

        public int AfiliadaId => _tenant.AfiliadaId;

        public IQueryable<WlUsuarioAfiliada> UsuariosAfiliada =>
            _db.WlUsuariosAfiliada.Where(u => u.AfiliadaId == AfiliadaId);

        public IQueryable<WlUsuario> Usuarios =>
            _db.WlUsuarios.Where(u => u.AfiliadaId == AfiliadaId);

        public IQueryable<Local> Locais =>
            _db.Locais.Where(l => l.IdAfiliada == AfiliadaId);

        // A peça não carrega IdAfiliada: ela pertence a um Local, e é por ele que
        // o recorte acontece. Repetir esse caminho em cada endpoint era uma das
        // formas mais fáceis de errar.
        public IQueryable<Peca> Pecas =>
            _db.Pecas.Where(p => p.Local.IdAfiliada == AfiliadaId);

        public IQueryable<PedidoReserva> PedidosReserva =>
            _db.PedidosReserva.Where(pr => pr.IdAfiliada == AfiliadaId);

        public IQueryable<PedidoInsercao> PedidosInsercao =>
            _db.PedidosInsercao.Where(pi => pi.IdAfiliada == AfiliadaId);

        public IQueryable<PedidoInsercaoItem> PedidoInsercaoItens =>
            _db.PedidoInsercaoItens.Where(i => i.PedidoInsercao.IdAfiliada == AfiliadaId);

        public IQueryable<PecaPeriodoStatus> PecaPeriodoStatus =>
            _db.PecaPeriodoStatus.Where(pps => pps.Peca.Local.IdAfiliada == AfiliadaId);
    }
}
