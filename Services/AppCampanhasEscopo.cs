using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Linq.Expressions;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities;
using Veiculando.Domain.Entities.Pedidos;
using Veiculando.Domain.Entities.WhiteLabel;
using Veiculando.Domain.Enums;
using Veiculando.WhiteLabel.Api.Middleware;

namespace Veiculando.WhiteLabel.Api.Services;

/// <summary>
/// Resultado de <see cref="AppCampanhasEscopo.ResolverAsync"/>. Quando <see cref="StatusErro"/>
/// vem preenchido, a sessão não enxerga campanha nenhuma e o controller devolve o erro.
/// </summary>
/// <param name="Campanhas">Campanhas que a sessão enxerga, sem filtro de status nem de carteira.
/// É o que o contexto da compra usa.</param>
/// <param name="Clientes">Conjunto de clientes da sessão: o mesmo do <c>GET /api/wl/app/clients</c>.</param>
/// <param name="Perfil">"agencia", "carteira" ou "anunciante_direto".</param>
public sealed record EscopoCampanhasApp(
    int? StatusErro,
    string MensagemErro,
    WlAppOnboarding Onboarding,
    WlUsuarioAnunciante Comprador,
    SessaoApp Sessao,
    IQueryable<Campanha> Campanhas,
    IQueryable<int> Clientes,
    string Perfil)
{
    public static EscopoCampanhasApp Erro(int status, string mensagem = null) =>
        new(status, mensagem, null, null, null, null, null, null);

    /// <summary>
    /// Campanhas de Minhas Campanhas: o escopo da sessão restrito ao conjunto de clientes
    /// (peer review B2). Cliente com vínculo expirado sai da carteira e as campanhas dele somem
    /// junto (decisão do PO, 09/11). O contexto da compra continua usando <see cref="Campanhas"/>.
    /// </summary>
    public IQueryable<Campanha> MinhasCampanhas =>
        Campanhas.Where(c => Clientes.Contains(c.IdCliente));

    /// <summary>
    /// A campanha foi criada pela identidade desta sessão. Para ad, ag e carteira o escopo já
    /// exige isso; no pj o escopo é o CNPJ, e uma agência pode ter criado a campanha para ele
    /// (peer review I4: o pj vê, mas não edita).
    /// </summary>
    public Expression<Func<Campanha, bool>> CriadaPelaSessao
    {
        get
        {
            var email = Comprador.Email.Endereco;
            return c => c.AnuncianteResponsavel.Email.Endereco == email;
        }
    }
}

/// <summary>Uma linha por item de pedido da exibidora do tenant, já sem item Deletado.</summary>
public sealed class ItemCampanhaApp
{
    public int IdCampanha { get; set; }
    public int IdPedido { get; set; }
    public string CodigoPedido { get; set; }
    public StatusPedidoEnum StatusPedido { get; set; }
    public string Estado { get; set; }
    public string Cidade { get; set; }
    public string CodigoPeriodo { get; set; }
    public DateTime InicioPeriodo { get; set; }
    public DateTime FimPeriodo { get; set; }
    public StatusPedidoItemEnum Status { get; set; }
    public decimal ValorBruto { get; set; }
}

/// <summary>Valores e contagens de uma campanha, só com itens desta exibidora.</summary>
public sealed record ResumoCampanhaApp(
    decimal PedidosConfirmados,
    decimal PedidosPendentes,
    int TotalPecas,
    int PecasConfirmadas,
    int PecasPendentes,
    int PecasPublicadas)
{
    public decimal Comprometido => PedidosConfirmados + PedidosPendentes;

    /// <summary>
    /// Verba − confirmados − pendentes desta exibidora (peer review I1). Sem verba devolve null,
    /// e o valor nunca sai negativo (decisão do PO, 09/11).
    /// </summary>
    public decimal? VerbaDisponivel(decimal? verba) =>
        verba.HasValue ? Math.Max(0m, verba.Value - Comprometido) : null;
}

/// <summary>
/// Quais campanhas e quais clientes a sessão do App enxerga (VEI-RD-45h, 67e). Antes a regra
/// vivia dentro do contexto da compra; Minhas Campanhas e a carteira precisam da mesma, então
/// fica num lugar só.
/// </summary>
/// <remarks>
/// <para>Sem filtro de status: o contexto da compra tira canceladas e aprovadas; Minhas
/// Campanhas mostra tudo como histórico. Cada um aplica o seu por cima.</para>
/// <para>A sessão de Prospecção é decidida ANTES do ramo por TipoConta (VEI-RD-157, I1):
/// senão a vendedora cai no "ad" da casa e enxerga as campanhas da própria exibidora.</para>
/// </remarks>
public sealed class AppCampanhasEscopo
{
    public const string MensagemSemIdentidade = "Identidade comercial ainda não provisionada.";

    /// <summary>Status em que a campanha ainda é rascunho e pode ser editada (P3).</summary>
    public static readonly StatusCampanhaEnum[] Rascunho =
        { StatusCampanhaEnum.Criada, StatusCampanhaEnum.Simulacao, StatusCampanhaEnum.PedidoEmAberto };

    public static readonly StatusPedidoEnum[] PedidosConfirmados =
    {
        StatusPedidoEnum.Confirmado, StatusPedidoEnum.Aprovado, StatusPedidoEnum.Checking,
        StatusPedidoEnum.Finalizado, StatusPedidoEnum.Veiculado
    };

    public static readonly StatusPedidoEnum[] PedidosPendentes =
        { StatusPedidoEnum.Solicitado, StatusPedidoEnum.ItensIndisponiveis };

    private static readonly StatusPedidoEnum[] PedidosForaDoHistorico =
        { StatusPedidoEnum.Cancelado, StatusPedidoEnum.Revisado };

    private readonly VeiculandoDataContext _db;
    private readonly ITenantQueries _tenant;
    private readonly CarteiraProspeccao _carteira;

    public AppCampanhasEscopo(VeiculandoDataContext db, ITenantQueries tenant, CarteiraProspeccao carteira)
        => (_db, _tenant, _carteira) = (db, tenant, carteira);

    /// <summary>
    /// Vínculo da agência com o cliente vale agora: ativo e dentro do contrato. É o ÚNICO
    /// predicado de vigência do App: carteira da Prospecção, clientes da agência, Minhas
    /// Campanhas e a criação de campanha usam este (peer review B2).
    /// </summary>
    public static Expression<Func<AgenciaCliente, bool>> AgenciaClienteVigente(int idAgencia, DateTime agora) =>
        l => l.IdAgencia == idAgencia && l.StatusExibicao == StatusExibicaoEnum.Ativo &&
             l.DataInicioContrato <= agora && l.DataExpiracaoContrato >= agora;

    /// <summary>
    /// Item de pedido que pertence a esta exibidora e não foi deletado. Pedido pode ter itens de
    /// várias afiliadas (peer review B1): somas, pedidos e contadores partem só destes itens.
    /// </summary>
    public static Expression<Func<PedidoItem, bool>> ItemDaExibidora(int afiliadaId) =>
        i => i.Peca.Local.IdAfiliada == afiliadaId && i.Status != StatusPedidoItemEnum.Deletado;

    /// <summary>
    /// Itens desta exibidora nas campanhas informadas, fora pedidos cancelados e revisados.
    /// Lista e detalhe usam este método e <see cref="Resumir"/>, por isso batem.
    /// </summary>
    public Task<List<ItemCampanhaApp>> ItensDaExibidoraAsync(IQueryable<Campanha> campanhas, CancellationToken ct)
    {
        var ids = campanhas.Select(c => c.Id);
        return _db.PedidoItens.AsNoTracking()
            .Where(ItemDaExibidora(_tenant.AfiliadaId))
            .Where(i => ids.Contains(i.Pedido.IdCampanha) && !PedidosForaDoHistorico.Contains(i.Pedido.Status))
            .Select(i => new ItemCampanhaApp
            {
                IdCampanha = i.Pedido.IdCampanha,
                IdPedido = i.IdPedido,
                CodigoPedido = i.Pedido.Codigo,
                StatusPedido = i.Pedido.Status,
                Estado = i.Pedido.Cidade.Estado.Sigla,
                Cidade = i.Pedido.Cidade.Nome,
                CodigoPeriodo = i.Pedido.Periodo.Codigo,
                InicioPeriodo = i.Pedido.Periodo.DataInicio,
                FimPeriodo = i.Pedido.Periodo.DataFim,
                Status = i.Status,
                ValorBruto = i.ValorBruto
            })
            .ToListAsync(ct);
    }

    /// <summary>Valores em R$ e contagens de peças pelas regras do <c>CampanhaDetalheResult</c> do Core.</summary>
    public static ResumoCampanhaApp Resumir(IEnumerable<ItemCampanhaApp> itens)
    {
        var lista = itens.ToList();
        // Itens "efetivos" do Core: fora simulação (de pedido ou de item).
        var efetivos = lista.Where(i => i.StatusPedido != StatusPedidoEnum.Simulacao &&
                                        i.Status != StatusPedidoItemEnum.Simulacao &&
                                        i.Status != StatusPedidoItemEnum.SimulacaoAprovado).ToList();
        return new ResumoCampanhaApp(
            lista.Where(i => PedidosConfirmados.Contains(i.StatusPedido)).Sum(i => i.ValorBruto),
            lista.Where(i => PedidosPendentes.Contains(i.StatusPedido)).Sum(i => i.ValorBruto),
            efetivos.Count,
            efetivos.Count(i => i.Status == StatusPedidoItemEnum.Confirmado ||
                                i.Status == StatusPedidoItemEnum.CheckingRecebido ||
                                i.Status == StatusPedidoItemEnum.CheckingAprovado),
            efetivos.Count(i => i.Status == StatusPedidoItemEnum.Reservado ||
                                i.Status == StatusPedidoItemEnum.Solicitado ||
                                i.Status == StatusPedidoItemEnum.Indisponivel),
            efetivos.Count(i => i.Status == StatusPedidoItemEnum.CheckingAprovado));
    }

    /// <summary>CNPJ só com dígitos: em produção o Core tem CNPJ gravado com máscara.</summary>
    public static string SoDigitos(string documento) =>
        documento == null ? null : new string(documento.Where(char.IsDigit).ToArray());

    public async Task<EscopoCampanhasApp> ResolverAsync(ClaimsPrincipal user, int userId, string mensagemSemKyc,
        CancellationToken ct)
    {
        var onboarding = await _db.WlAppOnboardings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.UsuarioId == userId && x.AfiliadaId == _tenant.AfiliadaId, ct);
        if (onboarding == null || onboarding.Status != WlAppKycStatus.Aprovado || string.IsNullOrWhiteSpace(onboarding.Documento))
            return EscopoCampanhasApp.Erro(403, mensagemSemKyc);
        var buyer = await _tenant.UsuariosAnunciante.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, ct);
        if (buyer == null) return EscopoCampanhasApp.Erro(401);
        var sessao = await _carteira.ResolverAsync(user, buyer, ct);
        if (sessao.Modo == ModoSessaoApp.Recusada)
            return EscopoCampanhasApp.Erro(403, SessaoApp.MensagemRecusada);
        if ((onboarding.TipoConta == "ad" || onboarding.TipoConta == "ag") &&
            (!buyer.AgenciaId.HasValue || !await _db.AfiliadaAgencias.AsNoTracking().AnyAsync(link =>
                link.IdAfiliada == _tenant.AfiliadaId && link.IdAgencia == buyer.AgenciaId.Value &&
                link.Status == StatusVinculoEnum.Ativo, ct)))
            return EscopoCampanhasApp.Erro(403, "O vínculo da agência com a exibidora está inativo.");

        var afiliadaId = _tenant.AfiliadaId;
        var clientesDaExibidora = _db.Clientes.AsNoTracking()
            .Where(c => c.StatusExibicao == StatusExibicaoEnum.Ativo &&
                        c.AfiliadasVinculadas.Any(v => v.IdAfiliada == afiliadaId && v.Status == StatusVinculoEnum.Ativo));
        var query = _db.Campanhas.AsNoTracking()
            .Where(x => x.StatusExibicao == StatusExibicaoEnum.Ativo &&
                        x.Cliente.AfiliadasVinculadas.Any(v => v.IdAfiliada == afiliadaId &&
                            v.Status == StatusVinculoEnum.Ativo));
        IQueryable<int> clientes;
        string perfil;
        if (sessao.Modo == ModoSessaoApp.Carteira)
        {
            if (!buyer.AgenciaId.HasValue) return EscopoCampanhasApp.Erro(403, MensagemSemIdentidade);
            // Só a carteira aprovada da vendedora; a própria exibidora (cliente da casa) fica de fora.
            var liberados = _carteira.ClientesLiberados(sessao.VendedorId, buyer.AgenciaId.Value);
            query = query.Where(x => x.IdAgencia == buyer.AgenciaId.Value && liberados.Contains(x.IdCliente) &&
                x.AnuncianteResponsavel.Email.Endereco == buyer.Email.Endereco);
            clientes = liberados;
            perfil = "carteira";
        }
        else if (onboarding.TipoConta == "ag" && buyer.AgenciaId.HasValue)
        {
            query = query.Where(x => x.IdAgencia == buyer.AgenciaId.Value &&
                x.AnuncianteResponsavel.Email.Endereco == buyer.Email.Endereco);
            var vigentes = _db.AgenciaClientes.Where(AgenciaClienteVigente(buyer.AgenciaId.Value, DateTime.UtcNow))
                .Select(l => l.IdCliente);
            clientes = clientesDaExibidora.Where(c => vigentes.Contains(c.Id)).Select(c => c.Id);
            perfil = "agencia";
        }
        else if (onboarding.TipoConta == "ad" && buyer.ClienteId.HasValue && buyer.AgenciaId.HasValue)
        {
            query = query.Where(x => x.IdCliente == buyer.ClienteId.Value && x.IdAgencia == buyer.AgenciaId.Value &&
                x.AnuncianteResponsavel.Email.Endereco == buyer.Email.Endereco);
            var proprio = buyer.ClienteId.Value;
            clientes = clientesDaExibidora.Where(c => c.Id == proprio).Select(c => c.Id);
            perfil = "anunciante_direto";
        }
        else if (onboarding.TipoConta == "pj")
        {
            // Compara só dígitos dos dois lados: o Core tem CNPJ com máscara (peer review I4).
            var documento = SoDigitos(onboarding.Documento);
            var doCnpj = clientesDaExibidora.Where(c =>
                c.Cnpj.Numero.Replace(".", "").Replace("/", "").Replace("-", "").Replace(" ", "") == documento);
            var ids = doCnpj.Select(c => c.Id);
            query = query.Where(x => ids.Contains(x.IdCliente));
            clientes = ids;
            perfil = "anunciante_direto";
        }
        else return EscopoCampanhasApp.Erro(403, MensagemSemIdentidade);

        return new EscopoCampanhasApp(null, null, onboarding, buyer, sessao, query, clientes, perfil);
    }
}
