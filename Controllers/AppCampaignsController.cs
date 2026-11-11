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
using Veiculando.Domain.Enums;
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

    public AppCampaignsController(VeiculandoDataContext db, AppCampanhasEscopo escopo)
        => (_db, _escopo) = (db, escopo);

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
