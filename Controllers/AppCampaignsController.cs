using System;
using System.Data.Entity;
using System.Data.Entity.Infrastructure;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities.Pedidos;
using Veiculando.Domain.Enums;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;

namespace Veiculando.WhiteLabel.Api.Controllers;

/// <summary>
/// Minhas Campanhas do App (VEI-RD-45, 68e): painel, detalhe e edição de rascunho.
/// </summary>
/// <remarks>
/// <para>Quem enxerga o quê vem de <see cref="AppCampanhasEscopo"/>: o escopo da sessão
/// restrito ao mesmo conjunto de clientes do <c>GET /api/wl/app/clients</c>. Canceladas e
/// aprovadas aparecem como histórico.</para>
/// <para>Valores e contagens contam só itens desta exibidora (um pedido pode ter itens de várias
/// afiliadas) e saem do mesmo cálculo na lista e no detalhe, para os dois baterem.</para>
/// <para>Campanha fora do escopo dá 404, nunca 403: 403 confirmaria que ela existe.</para>
/// </remarks>
[ApiController]
[Route("api/wl/app/campaigns")]
[Authorize]
public sealed class AppCampaignsController : ControllerBase
{
    private const string MensagemSemKyc = "A aprovação do cadastro é necessária para ver suas campanhas.";
    private const string NaoEncontrada = "Campanha não encontrada.";

    private readonly VeiculandoDataContext _db;
    private readonly AppCampanhasEscopo _escopo;
    private readonly ProducaoCampanhaApp _producao;
    private readonly ITenantQueries _tenant;

    public AppCampaignsController(VeiculandoDataContext db, AppCampanhasEscopo escopo, ProducaoCampanhaApp producao,
        ITenantQueries tenant)
        => (_db, _escopo, _producao, _tenant) = (db, escopo, producao, tenant);

    /// <param name="clienteId">Cliente escolhido no gateway. Fora do conjunto da sessão → 404.</param>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int? clienteId, CancellationToken ct)
    {
        if (!TryUser(out var userId)) return Unauthorized();
        var escopo = await _escopo.ResolverAsync(User, userId, MensagemSemKyc, ct);
        if (escopo.StatusErro.HasValue) return Erro(escopo);

        var campanhas = escopo.MinhasCampanhas;
        if (clienteId.HasValue)
        {
            if (!await escopo.Clientes.AnyAsync(id => id == clienteId.Value, ct))
                return NotFound(new { message = "Anunciante não encontrado." });
            campanhas = campanhas.Where(c => c.IdCliente == clienteId.Value);
        }

        var linhas = await campanhas
            .OrderByDescending(c => c.DataInicioPrevisto).ThenByDescending(c => c.Id)
            .Select(c => new
            {
                c.Id,
                c.Codigo,
                c.Nome,
                c.Status,
                c.DataInicioPrevisto,
                c.DataFimPrevisto,
                c.Verba,
                c.IdCliente,
                Cliente = c.Cliente.Nome,
                Cnpj = c.Cliente.Cnpj.Numero
            })
            .ToListAsync(ct);
        var itens = (await _escopo.ItensDaExibidoraAsync(campanhas, ct)).ToLookup(i => i.IdCampanha);

        return Ok(linhas.Select(c =>
        {
            var resumo = AppCampanhasEscopo.Resumir(itens[c.Id]);
            return new
            {
                codigo = c.Codigo,
                nome = c.Nome,
                status = StatusCampanha(c.Status),
                anunciante = new { id = c.IdCliente, nome = c.Cliente, cnpj = c.Cnpj },
                dataInicio = c.DataInicioPrevisto,
                dataFim = c.DataFimPrevisto,
                budgetTotal = c.Verba ?? 0m,
                pedidosConfirmados = resumo.PedidosConfirmados,
                pedidosPendentes = resumo.PedidosPendentes
            };
        }));
    }

    [HttpGet("{code}")]
    public async Task<IActionResult> Detail(string code, CancellationToken ct)
    {
        if (!TryUser(out var userId)) return Unauthorized();
        if (!CodigoValido(code)) return NotFound(new { message = NaoEncontrada });
        var escopo = await _escopo.ResolverAsync(User, userId, MensagemSemKyc, ct);
        if (escopo.StatusErro.HasValue) return Erro(escopo);

        var daCampanha = escopo.MinhasCampanhas.Where(c => c.Codigo == code);
        var campanha = await daCampanha
            .Select(c => new
            {
                c.Id,
                c.Codigo,
                c.Nome,
                c.Produto,
                c.Job,
                c.Status,
                c.DataInicioPrevisto,
                c.DataFimPrevisto,
                c.Verba,
                c.IdCliente,
                Cliente = c.Cliente.Nome,
                Cnpj = c.Cliente.Cnpj.Numero,
                Agencia = c.Agencia == null ? null : c.Agencia.Nome
            })
            .FirstOrDefaultAsync(ct);
        if (campanha == null) return NotFound(new { message = NaoEncontrada });

        var itens = await _escopo.ItensDaExibidoraAsync(daCampanha, ct);
        var resumo = AppCampanhasEscopo.Resumir(itens);
        var criadaPelaSessao = await daCampanha.AnyAsync(escopo.CriadaPelaSessao, ct);

        return Ok(new
        {
            codigo = campanha.Codigo,
            nome = campanha.Nome,
            produto = campanha.Produto,
            job = campanha.Job,
            status = StatusCampanha(campanha.Status),
            anunciante = new { id = campanha.IdCliente, nome = campanha.Cliente, cnpj = campanha.Cnpj },
            agencia = campanha.Agencia,
            dataInicio = campanha.DataInicioPrevisto,
            dataFim = campanha.DataFimPrevisto,
            budgetTotal = campanha.Verba ?? 0m,
            pedidosConfirmados = resumo.PedidosConfirmados,
            pedidosPendentes = resumo.PedidosPendentes,
            verbaDisponivel = resumo.VerbaDisponivel(campanha.Verba),
            totalPecas = resumo.TotalPecas,
            pecasConfirmadas = resumo.PecasConfirmadas,
            pecasPendentes = resumo.PecasPendentes,
            pecasPublicadas = resumo.PecasPublicadas,
            // Relatório de Locais e Ver no Mapa ficam para depois (sem permissão no App ainda).
            podeVerRelatorio = false,
            // Rascunho criado pela própria sessão; pj não edita campanha que uma agência criou (PO, 09/11).
            podeEditar = criadaPelaSessao && AppCampanhasEscopo.Rascunho.Contains(campanha.Status),
            pedidos = itens
                .GroupBy(i => i.IdPedido)
                .Select(g => g.First())
                .OrderBy(p => p.Estado).ThenBy(p => p.Cidade).ThenBy(p => p.InicioPeriodo)
                .Select(p =>
                {
                    var doPedido = itens.Where(i => i.IdPedido == p.IdPedido).ToList();
                    return new
                    {
                        estado = p.Estado,
                        cidade = p.Cidade,
                        codigo = p.CodigoPedido,
                        periodo = new { codigo = p.CodigoPeriodo, dataInicio = p.InicioPeriodo, dataFim = p.FimPeriodo },
                        status = StatusPedido(p.StatusPedido),
                        simulacao = p.StatusPedido == StatusPedidoEnum.Simulacao,
                        // VEI-SUP-60: a exibidora confirmou; falta a agência aprovar para nascer o PI.
                        podeAprovar = criadaPelaSessao && p.StatusPedido == StatusPedidoEnum.Confirmado,
                        pecas = doPedido.Count,
                        valor = doPedido.Sum(i => i.ValorBruto)
                    };
                }),
            // Abas Arte Final e Produção ficam para o VEI-RD-71f (decisão P5): arrays vazios.
            motivos = Array.Empty<object>(),
            layouts = Array.Empty<object>(),
            producao = Array.Empty<object>(),
            checking = new
            {
                estado = campanha.Status == StatusCampanhaEnum.Veiculada ? "concluido"
                    : campanha.Status == StatusCampanhaEnum.Checking ? "parcial" : "indisponivel"
            }
        });
    }

    /// <summary>
    /// Aba Produção (VEI-RD-165a): peças a produzir, por suporte e formato, só desta exibidora.
    /// </summary>
    /// <param name="filtro">1 aprovados (padrão); 2 aprovados e em andamento; 3 com simulações.</param>
    [HttpGet("{code}/producao")]
    public async Task<IActionResult> Producao(string code, [FromQuery] int filtro = 1, CancellationToken ct = default)
    {
        var (erro, campanha, _) = await CampanhaDaProducaoAsync(code, filtro, ct);
        if (erro != null) return erro;
        return Ok(await _producao.ListarAsync(campanha, filtro, ct));
    }

    /// <summary>PDF da aba Produção, gerado no BFF (o relatório do FileServer não recorta tenant).</summary>
    [HttpGet("{code}/producao/pdf")]
    public async Task<IActionResult> ProducaoPdf(string code, [FromQuery] int filtro = 1, CancellationToken ct = default)
    {
        var (erro, campanha, nome) = await CampanhaDaProducaoAsync(code, filtro, ct);
        if (erro != null) return erro;
        var linhas = await _producao.ListarAsync(campanha, filtro, ct);
        return File(ProducaoCampanhaApp.GerarPdf(code, nome, filtro, linhas), "application/pdf", $"Producao-{code}.pdf");
    }

    private async Task<(IActionResult Erro, IQueryable<Domain.Entities.Campanha> Campanha, string Nome)> CampanhaDaProducaoAsync(
        string code, int filtro, CancellationToken ct)
    {
        if (!TryUser(out var userId)) return (Unauthorized(), null, null);
        if (!CodigoValido(code)) return (NotFound(new { message = NaoEncontrada }), null, null);
        if (!ProducaoCampanhaApp.Filtros.ContainsKey(filtro))
            return (BadRequest(new { message = "Filtro de status inválido. Use 1, 2 ou 3." }), null, null);
        var escopo = await _escopo.ResolverAsync(User, userId, MensagemSemKyc, ct);
        if (escopo.StatusErro.HasValue) return (Erro(escopo), null, null);

        var daCampanha = escopo.MinhasCampanhas.Where(c => c.Codigo == code);
        var nome = await daCampanha.Select(c => c.Nome).FirstOrDefaultAsync(ct);
        return nome == null ? (NotFound(new { message = NaoEncontrada }), null, null) : (null, daCampanha, nome);
    }

    /// <summary>
    /// Edita uma campanha em rascunho (VEI-RD-68e). Ordem fixa: fora do escopo → 404; pj em
    /// campanha de agência → 403; fora de rascunho → 409; dados inválidos → 400. O cliente não muda.
    /// </summary>
    [HttpPut("{code}")]
    [EnableRateLimiting(Startup.RateLimitEscrita)]
    public async Task<IActionResult> Update(string code, [FromBody] CampanhaAppRequest request, CancellationToken ct)
    {
        if (!TryUser(out var userId)) return Unauthorized();
        if (!CodigoValido(code)) return NotFound(new { message = NaoEncontrada });
        var escopo = await _escopo.ResolverAsync(User, userId, MensagemSemKyc, ct);
        if (escopo.StatusErro.HasValue) return Erro(escopo);

        var daCampanha = escopo.MinhasCampanhas.Where(c => c.Codigo == code);
        var alvo = await daCampanha.Select(c => new { c.Id, c.Status }).FirstOrDefaultAsync(ct);
        if (alvo == null) return NotFound(new { message = NaoEncontrada });
        if (!await daCampanha.AnyAsync(escopo.CriadaPelaSessao, ct))
            return StatusCode(403, new { message = "Esta campanha foi criada por uma agência e só ela pode editá-la." });
        if (!AppCampanhasEscopo.Rascunho.Contains(alvo.Status))
            return Conflict(new { message = "Só é possível editar campanha em rascunho." });

        var itens = await _escopo.ItensDaExibidoraAsync(daCampanha, ct);
        var resumo = AppCampanhasEscopo.Resumir(itens);
        var comprometidos = itens.Where(i => i.StatusPedido != StatusPedidoEnum.Simulacao).ToList();
        var details = CampanhaAppValidacao.Validar(request, new CampanhaAppValidacao.Comprometido(
            resumo.Comprometido,
            comprometidos.Count == 0 ? null : comprometidos.Min(i => i.InicioPeriodo),
            comprometidos.Count == 0 ? null : comprometidos.Max(i => i.FimPeriodo)));
        if (details.Count > 0) return CampanhaAppValidacao.Erro(CampanhaAppValidacao.Mensagem, details);

        // Edição pelo Campanha.Update do Core (ver CampanhaEdicaoCore). O responsável continua o
        // mesmo: o escopo já garantiu que é a identidade desta sessão. O cliente não muda.
        var campanha = await _db.Campanhas
            .Include(c => c.Cliente)
            .Include(c => c.AnuncianteResponsavel.Agencia)
            .SingleAsync(c => c.Id == alvo.Id, ct);
        CampanhaEdicaoCore.Aplicar(campanha, campanha.AnuncianteResponsavel, request.Name.Trim(), request.Product.Trim(),
            string.IsNullOrWhiteSpace(request.Job) ? null : request.Job.Trim(),
            request.StartDate.Date, request.EndDate.Date, request.Budget);
        if (!campanha.IsValid())
            return CampanhaAppValidacao.Erro(CampanhaAppValidacao.Mensagem, campanha.Notifications.Select(n => n.Message));
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { message = "A campanha foi alterada por outra pessoa. Recarregue e tente de novo." });
        }

        return Ok(new { codigo = campanha.Codigo, nome = campanha.Nome });
    }

    /// <summary>
    /// Aprova o pedido que a exibidora confirmou e gera o PI (VEI-SUP-60). É o
    /// "assinar PI" da agência do Veiculando.Web, que o App não tinha: sem ele a campanha
    /// ficava em rascunho e a exibidora nunca via o pedido de inserção.
    /// </summary>
    /// <remarks>
    /// <para>Mesmos passos do <c>PedidoCadastroHandler.Handle(PedidoAssinarPICommand)</c> do Core:
    /// <c>Pedido.AssinarPIAgencia</c>, <c>Campanha.AtualizaStatusAssinatura</c>,
    /// <c>PedidoInsercao.GerarPedidosDeInsercao</c> e a grade de disponibilidade em Autorizada.</para>
    /// <para>Fica de fora a permissão legada <c>FinaceiroAprovador</c>, que nenhum responsável
    /// comercial do WhiteLabel recebe (mesmo motivo do <see cref="CampanhaEdicaoCore"/>): quem
    /// pode aprovar é decidido pelo escopo da sessão e por ser o responsável do pedido.
    /// O e-mail do Core para a exibidora também fica de fora: o PI aparece no painel.</para>
    /// <para>Ordem: fora do escopo → 404; campanha de outro responsável → 403; fora de Confirmado ou com
    /// itens de outra exibidora → 409.</para>
    /// </remarks>
    [HttpPost("{code}/pedidos/{pedido}/aprovar")]
    [EnableRateLimiting(Startup.RateLimitEscrita)]
    public async Task<IActionResult> Aprovar(string code, string pedido, CancellationToken ct)
    {
        if (!TryUser(out var userId)) return Unauthorized();
        if (!CodigoValido(code) || !CodigoValido(pedido)) return NotFound(new { message = NaoEncontrada });
        var escopo = await _escopo.ResolverAsync(User, userId, MensagemSemKyc, ct);
        if (escopo.StatusErro.HasValue) return Erro(escopo);

        var daCampanha = escopo.MinhasCampanhas.Where(c => c.Codigo == code);
        var idCampanha = await daCampanha.Select(c => (int?)c.Id).FirstOrDefaultAsync(ct);
        if (idCampanha == null) return NotFound(new { message = NaoEncontrada });
        // Quem aprova é o responsável comercial da campanha (o mesmo que o checkout grava no
        // pedido); um pj que só enxerga a campanha de uma agência não aprova por ela.
        if (!await daCampanha.AnyAsync(escopo.CriadaPelaSessao, ct))
            return StatusCode(403, new { message = "Só quem fez o pedido pode aprová-lo." });

        var alvo = await _db.Pedidos
            .Include(p => p.Itens.Select(i => i.Peca.Local.Afiliada))
            .Include(p => p.Itens.Select(i => i.Insercao))
            .Include(p => p.Campanha.Pedidos)
            .Include(p => p.Campanha.AnuncianteResponsavel)
            .SingleOrDefaultAsync(p => p.Codigo == pedido && p.IdCampanha == idCampanha.Value, ct);
        if (alvo == null) return NotFound(new { message = "Pedido não encontrado." });
        if (alvo.IdUsuarioAnunciante != alvo.Campanha.IdUsuarioAnunciante)
            return StatusCode(403, new { message = "Só quem fez o pedido pode aprová-lo." });
        if (alvo.Status != StatusPedidoEnum.Confirmado)
            return Conflict(new { message = "Este pedido não pode mais ser aprovado." });
        if (alvo.Itens.Any(i => i.Peca.Local.IdAfiliada != _tenant.AfiliadaId))
            return Conflict(new { message = "Este pedido tem peças de outra exibidora e não pode ser aprovado por aqui." });

        alvo.AssinarPIAgencia(alvo.Campanha.AnuncianteResponsavel);
        CampanhaAssinaturaCore.AtualizarStatus(alvo.Campanha);
        var pis = PedidoInsercao.GerarPedidosDeInsercao(alvo);

        foreach (var item in pis.SelectMany(pi => pi.Itens.Select(i => (pi, i.PedidoItem))))
        {
            // FirstOrDefault como o RetornaPorPecaPeriodo do Core: linha duplicada não derruba a aprovação.
            var status = await _db.PecaPeriodoStatus.FirstOrDefaultAsync(s =>
                s.IdPeca == item.PedidoItem.IdPeca && s.IdPeriodo == item.PedidoItem.IdPeriodo, ct);
            status?.SetStatusAutorizada(item.pi);
        }
        _db.PedidosInsercao.AddRange(pis);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { message = "O pedido foi alterado por outra pessoa. Recarregue e tente de novo." });
        }

        return Ok(new { codigo = alvo.Codigo, status = StatusPedido(alvo.Status), pedidosInsercao = pis.Select(pi => pi.Codigo) });
    }

    /// <summary>Status do Core para o badge do PRD §4.5.2 (mais "cancelada", que o PRD não cobre).</summary>
    internal static string StatusCampanha(StatusCampanhaEnum status) => status switch
    {
        StatusCampanhaEnum.Cancelada => "cancelada",
        StatusCampanhaEnum.Confirmada or StatusCampanhaEnum.Autorizada or StatusCampanhaEnum.Aprovada => "aguardando_producao",
        StatusCampanhaEnum.Checking => "em_exibicao",
        StatusCampanhaEnum.Veiculada => "concluida",
        _ => "rascunho"
    };

    internal static string StatusPedido(StatusPedidoEnum status) => status switch
    {
        StatusPedidoEnum.Solicitado => "Solicitado",
        StatusPedidoEnum.Confirmado => "Confirmado",
        StatusPedidoEnum.ItensIndisponiveis => "Itens indisponíveis",
        StatusPedidoEnum.Aprovado => "Aprovado",
        StatusPedidoEnum.Checking => "Em checking",
        StatusPedidoEnum.Veiculado => "Veiculado",
        StatusPedidoEnum.Finalizado => "Finalizado",
        StatusPedidoEnum.Simulacao => "Simulação",
        _ => status.ToString()
    };

    private static bool CodigoValido(string code) => !string.IsNullOrWhiteSpace(code) && code.Length <= 50;

    private IActionResult Erro(EscopoCampanhasApp escopo) => escopo.StatusErro == 401
        ? Unauthorized()
        : StatusCode(escopo.StatusErro!.Value, new { message = escopo.MensagemErro });

    private bool TryUser(out int id)
    {
        id = 0;
        return User.FindFirstValue("WlPerfil") == "Anunciante" &&
               int.TryParse(User.FindFirstValue("WlAnuncianteId"), out id);
    }
}

/// <summary>
/// Carteira autorizada da sessão do App (VEI-RD-67e): quem o gateway de Minhas Campanhas e o
/// modal de campanha oferecem. É o mesmo conjunto que filtra as campanhas.
/// </summary>
[ApiController]
[Route("api/wl/app/clients")]
[Authorize]
public sealed class AppClientsController : ControllerBase
{
    private readonly VeiculandoDataContext _db;
    private readonly AppCampanhasEscopo _escopo;

    public AppClientsController(VeiculandoDataContext db, AppCampanhasEscopo escopo)
        => (_db, _escopo) = (db, escopo);

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (User.FindFirstValue("WlPerfil") != "Anunciante" ||
            !int.TryParse(User.FindFirstValue("WlAnuncianteId"), out var userId))
            return Unauthorized();
        var escopo = await _escopo.ResolverAsync(User, userId,
            "A aprovação do cadastro é necessária para ver seus anunciantes.", ct);
        if (escopo.StatusErro.HasValue)
            return escopo.StatusErro == 401
                ? Unauthorized()
                : StatusCode(escopo.StatusErro.Value, new { message = escopo.MensagemErro });

        var clientes = await _db.Clientes.AsNoTracking()
            .Where(c => escopo.Clientes.Contains(c.Id))
            .OrderBy(c => c.Nome)
            .Select(c => new { id = c.Id, nome = c.Nome, cnpj = c.Cnpj.Numero })
            .ToListAsync(ct);

        return Ok(new
        {
            perfil = escopo.Perfil,
            // VEI-RD-67d: só a agência adiciona anunciante (AppAnunciantesController).
            podeAdicionarAnunciante = escopo.Perfil == "agencia",
            clientes
        });
    }
}
