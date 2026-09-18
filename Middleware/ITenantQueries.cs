using System.Linq;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities;
using Veiculando.Domain.Entities.Pedidos;
using Veiculando.Domain.Entities.WhiteLabel;
using Veiculando.Domain.Enums;

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

        /// <summary>Anunciantes do App, sempre limitados à afiliada resolvida pelo Host.</summary>
        IQueryable<WlUsuarioAnunciante> UsuariosAnunciante { get; }

        /// <summary>Base da hierarquia — usada na checagem de e-mail duplicado.</summary>
        IQueryable<WlUsuario> Usuarios { get; }

        /// <summary>Locais da exibidora, em qualquer status.</summary>
        IQueryable<Local> Locais { get; }

        /// <summary>
        /// Anunciantes (Clientes) vinculados à afiliada via AfiliadaCliente
        /// (PRD §6.3) — tabela associativa, não IdAfiliada em Cliente. Em
        /// qualquer status de vínculo (Ativo/Inativo); o filtro por aba
        /// Todos/Ativo/Inativo é do controller, igual ao padrão de Locais.
        /// </summary>
        IQueryable<Cliente> Clientes { get; }

        /// <summary>
        /// As linhas de vínculo em si (não os Clientes) — para inativar/reativar
        /// e para checar Status sem carregar o Cliente inteiro.
        /// </summary>
        IQueryable<AfiliadaCliente> AfiliadaClientes { get; }

        /// <summary>
        /// Agências vinculadas à afiliada via AfiliadaAgencia (VEI-RD-79) — mesmo
        /// desenho de Clientes: tabela associativa, não IdAfiliada em Agencia.
        /// Em qualquer status de vínculo; a aba Todos/Ativo/Inativo é do controller.
        /// </summary>
        IQueryable<Agencia> Agencias { get; }

        /// <summary>As linhas de vínculo de agência, para inativar/reativar.</summary>
        IQueryable<AfiliadaAgencia> AfiliadaAgencias { get; }

        /// <summary>
        /// Onboardings de organização (KYC) da afiliada — VEI-RD-80/81. Inclui todos
        /// os estados; tirar Rascunho da FILA é decisão do controller, porque o
        /// detalhe e o histórico precisam enxergar o estado inteiro.
        /// </summary>
        IQueryable<OrganizacaoOnboarding> Onboardings { get; }

        /// <summary>
        /// Documentos de KYC, recortados pela afiliada do onboarding pai. É o recorte
        /// que faz <c>GET /kyc/documentos/{id}/url</c> devolver 404 — e não o arquivo —
        /// para documento de outra exibidora.
        /// </summary>
        IQueryable<OrganizacaoOnboardingDocumento> OnboardingDocumentos { get; }

        /// <summary>Decisões de KYC (append-only), recortadas pelo onboarding pai.</summary>
        IQueryable<OrganizacaoOnboardingDecisao> OnboardingDecisoes { get; }

        /// <summary>
        /// Campanhas visíveis à afiliada — VEI-RD-51. <c>Campanha</c> não tem
        /// <c>IdAfiliada</c>: a visibilidade é resolvida no servidor pela cadeia
        /// <c>Campanha → Pedido → PedidoReserva|PedidoInsercao</c>. Uma campanha é
        /// visível quando tem ao menos um Pedido com Reserva ou PI desta afiliada.
        /// </summary>
        IQueryable<Campanha> Campanhas { get; }

        /// <summary>Configuração da afiliada (uma linha) — VEI-RD-82.</summary>
        IQueryable<AfiliadaConfiguracao> Configuracoes { get; }

        /// <summary>Histórico append-only de alterações de configuração.</summary>
        IQueryable<AfiliadaConfiguracaoHistorico> ConfiguracaoHistorico { get; }

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

        public IQueryable<WlUsuarioAnunciante> UsuariosAnunciante =>
            _db.WlUsuariosAnunciante.Where(u => u.AfiliadaId == AfiliadaId);

        public IQueryable<WlUsuario> Usuarios =>
            _db.WlUsuarios.Where(u => u.AfiliadaId == AfiliadaId);

        public IQueryable<Local> Locais =>
            _db.Locais.Where(l => l.IdAfiliada == AfiliadaId);

        public IQueryable<Cliente> Clientes =>
            _db.Clientes.Where(c => c.AfiliadasVinculadas.Any(v => v.IdAfiliada == AfiliadaId));

        public IQueryable<AfiliadaCliente> AfiliadaClientes =>
            _db.AfiliadaClientes.Where(v => v.IdAfiliada == AfiliadaId);

        public IQueryable<Agencia> Agencias =>
            _db.Agencias.Where(a => a.AfiliadasVinculadas.Any(v => v.IdAfiliada == AfiliadaId));

        public IQueryable<AfiliadaAgencia> AfiliadaAgencias =>
            _db.AfiliadaAgencias.Where(v => v.IdAfiliada == AfiliadaId);

        public IQueryable<OrganizacaoOnboarding> Onboardings =>
            _db.OrganizacaoOnboardings.Where(o => o.IdAfiliada == AfiliadaId);

        public IQueryable<OrganizacaoOnboardingDocumento> OnboardingDocumentos =>
            _db.OrganizacaoOnboardingDocumentos.Where(d => d.Onboarding.IdAfiliada == AfiliadaId);

        public IQueryable<OrganizacaoOnboardingDecisao> OnboardingDecisoes =>
            _db.OrganizacaoOnboardingDecisoes.Where(d => d.Onboarding.IdAfiliada == AfiliadaId);

        // A campanha nao carrega IdAfiliada (Campanha.cs). O caminho ate a afiliada
        // passa pelo pedido: sem este recorte central, cada endpoint de campanha teria
        // de reescrever a cadeia inteira - e bastaria um esquecer para vazar a campanha
        // de outra exibidora.
        public IQueryable<Campanha> Campanhas =>
            _db.Campanhas.Where(c => c.Pedidos.Any(
                p => p.PedidosReserva.Any(r => r.IdAfiliada == AfiliadaId)
                  || p.PedidosInsercao.Any(i => i.IdAfiliada == AfiliadaId)));

        public IQueryable<AfiliadaConfiguracao> Configuracoes =>
            _db.AfiliadaConfiguracoes.Where(c => c.IdAfiliada == AfiliadaId);

        public IQueryable<AfiliadaConfiguracaoHistorico> ConfiguracaoHistorico =>
            _db.AfiliadaConfiguracaoHistoricos.Where(h => h.IdAfiliada == AfiliadaId);

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
