using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Data.Entity;
using System.Data.Entity.Infrastructure;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities;
using Veiculando.Domain.Entities.Arte;
using Veiculando.Domain.Entities.OrdensServico;
using Veiculando.Domain.Enums;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;

namespace Veiculando.WhiteLabel.Api.Controllers;

public sealed class CampoOrdensFiltro
{
    public string Busca { get; set; }
    public string Estado { get; set; }
    public DateTime? De { get; set; }
    public DateTime? Ate { get; set; }
    public string Ordenacao { get; set; } = "recente";
    [Range(-90d, 90d)] public double? Lat { get; set; }
    [Range(-180d, 180d)] public double? Lng { get; set; }
    [Range(1, int.MaxValue)] public int Pagina { get; set; } = 1;
    [Range(1, 100)] public int Tamanho { get; set; } = WlPaginacao.PageSizePadrao;
}

public sealed class CampoAcaoPedido
{
    [Required] public Guid? IdCliente { get; set; }
    [Required] public DateTimeOffset? Hora { get; set; }
    [Range(-90d, 90d)] public double? Latitude { get; set; }
    [Range(-180d, 180d)] public double? Longitude { get; set; }
}

[Route("api/wl/campo/ordens")]
public sealed class CampoOrdensController : CampoControllerBase
{
    private readonly VeiculandoDataContext _db;
    private readonly ITenantQueries _queries;

    public CampoOrdensController(VeiculandoDataContext db, ITenantQueries queries, ITenantContext tenant) : base(tenant)
    {
        _db = db;
        _queries = queries;
    }

    // O filtro do Core inclui atribuicao atual/legada e historico delimitado.
    // A afiliada vem do Host e o operador do JWT, nunca da query string.
    private IQueryable<OrdemServico> Visiveis(FiltroListaCampo filtro) => Visiveis(_queries, AfiliadaId, filtro);

    internal static IQueryable<OrdemServico> Visiveis(ITenantQueries queries, int afiliadaId, FiltroListaCampo filtro) =>
        ListaCampoConsulta.Aplicar(queries.OrdensServico.AsNoTracking()
            .Where(o => o.Pecas.All(p => p.Peca.Local.IdAfiliada == afiliadaId)), filtro);

    internal static IQueryable<OrdemServico> ComResumo(IQueryable<OrdemServico> consulta) => consulta
        .Include(o => o.Periodo)
        .Include(o => o.Cidades.Select(c => c.Cidade))
        .Include(o => o.Pecas.Select(p => p.Peca.Local.Cidade))
        .Include(o => o.Pecas.Select(p => p.Peca.Suporte));

    [HttpGet]
    public async Task<IActionResult> Listar([FromQuery] CampoOrdensFiltro pedido, CancellationToken ct)
    {
        var erro = MontarFiltro(pedido, out var filtro);
        if (erro != null) return erro;
        var (ordens, total) = await Pagina(filtro, pedido, ct);
        return Ok(new { itens = ordens.Select(o => CampoOrdensProjecao.Linha(o, OperadorId)), total,
            pagina = pedido.Pagina, tamanho = pedido.Tamanho });
    }

    // Pacote offline: a primeira página da lista, cada ordem com o detalhe completo.
    [HttpGet("~/api/wl/campo/pacote")]
    public async Task<IActionResult> Pacote([FromQuery] CampoOrdensFiltro pedido, CancellationToken ct)
    {
        pedido.Pagina = 1;
        var erro = MontarFiltro(pedido, out var filtro);
        if (erro != null) return erro;
        var (ordens, total) = await Pagina(filtro, pedido, ct);
        var itens = await Detalhes(ordens, ct);
        return Ok(new { itens, total, tamanho = pedido.Tamanho, geradoEm = DateTimeOffset.UtcNow });
    }

    private async Task<(List<OrdemServico> Ordens, int Total)> Pagina(FiltroListaCampo filtro, CampoOrdensFiltro pedido,
        CancellationToken ct)
    {
        var consulta = Visiveis(filtro);
        var total = await consulta.CountAsync(ct);
        // Fecha os empates de distancia/data da consulta do Core antes do OFFSET.
        var ordenada = ((IOrderedQueryable<OrdemServico>)consulta).ThenBy(o => o.Id);
        var ordens = await ComResumo(ordenada.Skip((pedido.Pagina - 1) * pedido.Tamanho).Take(pedido.Tamanho)).ToListAsync(ct);
        return (ordens, total);
    }

    private IActionResult MontarFiltro(CampoOrdensFiltro pedido, out FiltroListaCampo filtro)
    {
        filtro = null;
        if (pedido.De > pedido.Ate)
            return BadRequest(new { message = "O período inicial deve ser anterior ou igual ao final." });
        if ((long)(pedido.Pagina - 1) * pedido.Tamanho > int.MaxValue)
            return BadRequest(new { message = "Página fora do limite de consulta." });
        filtro = new FiltroListaCampo(AfiliadaId, OperadorId) {
            Busca = pedido.Busca, De = pedido.De, Ate = pedido.Ate,
            Latitude = pedido.Lat, Longitude = pedido.Lng
        };
        if (!string.IsNullOrWhiteSpace(pedido.Estado))
        {
            if (!Enum.TryParse<StatusOrdemServicoEnum>(pedido.Estado, true, out var estado)
                || !Enum.IsDefined(estado) || estado == StatusOrdemServicoEnum.Rascunho)
                return BadRequest(new { message = "Estado de campo inválido." });
            filtro.Estado = estado;
        }
        switch (pedido.Ordenacao?.ToLowerInvariant())
        {
            case null: case "": case "recente": filtro.Ordenacao = OrdenacaoListaCampo.MaisRecente; break;
            case "antigo": filtro.Ordenacao = OrdenacaoListaCampo.MaisAntigo; break;
            case "proximo":
                if (!pedido.Lat.HasValue || !pedido.Lng.HasValue)
                    return BadRequest(new { message = "Informe latitude e longitude para ordenar por proximidade." });
                filtro.Ordenacao = OrdenacaoListaCampo.Proximidade;
                break;
            default: return BadRequest(new { message = "Ordenação inválida. Use proximo, recente ou antigo." });
        }
        return null;
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Detalhar(int id, CancellationToken ct)
    {
        var ordem = await ComResumo(Visiveis(new FiltroListaCampo(AfiliadaId, OperadorId)))
            .FirstOrDefaultAsync(o => o.Id == id, ct);
        if (ordem == null) return NotFound(new { message = "Ordem de serviço não encontrada." });
        return Ok((await Detalhes(new List<OrdemServico> { ordem }, ct))[0]);
    }

    // Pacote offline: as consultas sao feitas uma vez para a pagina inteira,
    // nao por ordem. O periodo anterior e consultado por periodo distinto.
    private async Task<List<CampoOrdemDto>> Detalhes(List<OrdemServico> ordens, CancellationToken ct)
    {
        var iniciadas = ordens.Where(o => o.DataInicio != null).ToList();
        var anteriores = new Dictionary<int, Periodo>();
        foreach (var periodo in iniciadas.Select(o => o.Periodo).GroupBy(p => p.Id).Select(g => g.First()))
            anteriores[periodo.Id] = await _db.Periodos.AsNoTracking()
                .Where(p => p.Periodicidade.Tipo == periodo.Periodicidade.Tipo && p.DataInicio < periodo.DataInicio)
                .OrderByDescending(p => p.DataInicio).ThenByDescending(p => p.Id).FirstOrDefaultAsync(ct);

        var pecas = iniciadas.SelectMany(o => o.Pecas.Select(p => p.IdPeca)).Distinct().ToArray();
        var periodos = iniciadas.Select(o => o.IdPeriodo)
            .Concat(anteriores.Values.Where(p => p != null).Select(p => p.Id)).Distinct().ToArray();
        var status = pecas.Length == 0 ? new List<PecaPeriodoStatus>() : await _queries.PecaPeriodoStatus.AsNoTracking()
            .Where(p => pecas.Contains(p.IdPeca) && periodos.Contains(p.IdPeriodo)
                && (p.PedidoInsercao == null || p.PedidoInsercao.IdAfiliada == AfiliadaId)
                && (p.PedidoReserva == null || p.PedidoReserva.IdAfiliada == AfiliadaId))
            .Include(p => p.Pedido.Campanha).Include(p => p.PedidoReserva.Pedido.Campanha)
            .Include(p => p.PedidoInsercao.Pedido.Campanha).ToListAsync(ct);
        // Status vigente de cada peca em cada periodo: o de maior Id.
        var vigente = status.GroupBy(p => (p.IdPeca, p.IdPeriodo))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.Id).First());
        PecaPeriodoStatus Status(int idPeca, int? idPeriodo) =>
            idPeriodo != null && vigente.TryGetValue((idPeca, idPeriodo.Value), out var s) ? s : null;

        var campanhas = iniciadas.SelectMany(o => o.Pecas.Select(p => CampanhaDe(Status(p.IdPeca, o.IdPeriodo))))
            .Where(c => c != null).Select(c => c.Id).Distinct().ToArray();
        // So entram arquivos que a rota de imagens consegue servir (CampoImagemCaminho).
        var artes = campanhas.Length == 0 ? new List<ArteFinal>() : (await _db.ArtesFinais.AsNoTracking()
                .Include(a => a.Motivo)
                .Where(a => campanhas.Contains(a.Motivo.IdCampanha) && a.StatusExibicao == StatusExibicaoEnum.Ativo
                    && a.Motivo.StatusExibicao == StatusExibicaoEnum.Ativo && a.ArquivoAlta.Nome != null)
                .OrderByDescending(a => a.DataCadastro).ThenByDescending(a => a.Id).ToListAsync(ct))
            .Where(a => CampoImagemCaminho.Arte(a.ArquivoAlta.Nome) != null).ToList();
        var limite = iniciadas.Count == 0 ? DateTime.MinValue : iniciadas.Max(o => o.Periodo.DataInicio);
        var fotos = pecas.Length == 0 ? new List<FotoAnterior>() : (await _db.CheckingFotos.AsNoTracking()
            .Where(f => pecas.Contains(f.CheckingItem.IdPeca)
                && f.CheckingItem.Peca.Local.IdAfiliada == AfiliadaId
                && f.CheckingItem.Checking.PedidoInsercao.IdAfiliada == AfiliadaId
                && f.CheckingItem.PedidoInsercaoItem.PedidoItem.Periodo.DataInicio < limite
                && f.UrlArquivo != null && f.UrlArquivo != ""
                && (f.StatusExibicao == StatusExibicaoEnum.Ativo || f.StatusExibicao == StatusExibicaoEnum.AprovacaoPendente))
            .Select(f => new FotoAnterior { Id = f.Id, IdPeca = f.CheckingItem.IdPeca, DataCadastro = f.DataCadastro,
                UrlArquivo = f.UrlArquivo, Periodo = f.CheckingItem.PedidoInsercaoItem.PedidoItem.Periodo.DataInicio })
            .ToListAsync(ct)).Where(f => CampoImagemCaminho.Checking(f.UrlArquivo) != null).ToList();

        return ordens.Select(ordem => {
            var resposta = CampoOrdensProjecao.Linha(ordem, OperadorId);
            resposta.Locais = Array.Empty<CampoLocalDto>();
            if (ordem.DataInicio == null) return resposta;
            var anterior = anteriores[ordem.IdPeriodo];
            resposta.Locais = ordem.Pecas.OrderBy(p => p.Peca.Local.Codigo).ThenBy(p => p.Peca.Codigo)
                .GroupBy(p => p.Peca.Local.Id).Select(g => {
                    var local = g.First().Peca.Local;
                    var itens = g.Select(p => {
                        var campanha = CampanhaDe(Status(p.IdPeca, ordem.IdPeriodo));
                        // O modelo liga a arte a campanha/motivo/formato, nao diretamente a peca.
                        var arte = p.Peca.IdFormatoArteFinal == null ? null : artes.FirstOrDefault(a =>
                            a.Motivo.IdCampanha == campanha?.Id && a.IdFormato == p.Peca.IdFormatoArteFinal);
                        var foto = fotos.Where(f => f.IdPeca == p.IdPeca && f.Periodo < ordem.Periodo.DataInicio)
                            .OrderByDescending(f => f.Periodo).ThenByDescending(f => f.DataCadastro)
                            .ThenByDescending(f => f.Id).FirstOrDefault();
                        return new CampoPecaDto(p.IdPeca, p.Peca.Codigo, CampanhaDe(Status(p.IdPeca, anterior?.Id))?.Nome,
                            campanha?.Nome, arte?.Motivo.Nome, p.DataColagem, p.DataFotoCampo, local.GeoLocalizacao?.Latitude,
                            local.GeoLocalizacao?.Longitude, foto == null ? null : $"/api/wl/campo/imagens/checking/{foto.Id}",
                            arte == null ? null : $"/api/wl/campo/imagens/arte/{arte.Id}", p.Peca.Suporte?.Nome);
                    }).ToArray();
                    return new CampoLocalDto(local.Id, local.Endereco?.Numero, local.Codigo,
                        local.Endereco?.Logradouro, local.Endereco?.Bairro, local.Cidade?.Nome, itens);
                }).ToArray();
            return resposta;
        }).ToList();
    }

    private sealed class FotoAnterior
    {
        public int Id { get; set; }
        public int IdPeca { get; set; }
        public DateTime DataCadastro { get; set; }
        public string UrlArquivo { get; set; }
        public DateTime Periodo { get; set; }
    }

    [HttpPost("{id:int}/iniciar")]
    public Task<IActionResult> Iniciar(int id, [FromBody] CampoAcaoPedido pedido, CancellationToken ct)
    {
        if (pedido.Latitude == null || pedido.Longitude == null)
            return Task.FromResult<IActionResult>(BadRequest(new { message = "Informe latitude e longitude do aparelho." }));
        return Executar(id, null, TipoAcaoCampoEnum.Iniciar, pedido, ct);
    }

    [HttpPost("{id:int}/pecas/{idPeca:int}/colagem")]
    public Task<IActionResult> MarcarColagem(int id, int idPeca, [FromBody] CampoAcaoPedido pedido, CancellationToken ct) =>
        Executar(id, idPeca, TipoAcaoCampoEnum.MarcarColagem, pedido, ct);

    // ADR-WL-022: o registro pelo UUID do aparelho e gravado na mesma
    // transacao da ordem; repeticoes devolvem o resultado ja gravado.
    private async Task<IActionResult> Executar(int id, int? idPeca, TipoAcaoCampoEnum tipo, CampoAcaoPedido pedido,
        CancellationToken ct)
    {
        if (pedido.IdCliente == Guid.Empty)
            return BadRequest(new { message = "Informe o identificador da ação." });
        var visiveis = Visiveis(new FiltroListaCampo(AfiliadaId, OperadorId));
        if (!await visiveis.AnyAsync(o => o.Id == id, ct))
            return NotFound(new { message = "Ordem de serviço não encontrada." });

        var idCliente = pedido.IdCliente.Value;
        var registro = await _db.OrdemServicoAcoesCampo.AsNoTracking().FirstOrDefaultAsync(a => a.IdCliente == idCliente, ct);
        if (registro == null)
        {
            var ordem = await _db.OrdensServico.Include(o => o.Pecas).SingleAsync(o => o.Id == id, ct);
            var comando = new ComandoAcaoCampo(idCliente, OperadorId, tipo, idPeca, pedido.Hora.Value,
                pedido.Latitude, pedido.Longitude);
            registro = AcoesCampo.Executar(ordem, null, comando).Registro;
            _db.OrdemServicoAcoesCampo.Add(registro);
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Corrida com o mesmo UUID: a PK recusou esta gravacao.
                registro = await _db.OrdemServicoAcoesCampo.AsNoTracking().FirstOrDefaultAsync(a => a.IdCliente == idCliente, ct);
                if (registro == null) throw;
            }
        }
        if (registro.IdOperador != OperadorId || registro.IdOrdemServico != id || registro.Tipo != tipo
            || registro.IdPeca != idPeca)
            return RecusarCampo(IdClienteReutilizado);
        if (registro.CodigoRecusa != null)
            return RecusarCampo(registro.CodigoRecusa);

        var atual = await ComResumo(visiveis).FirstAsync(o => o.Id == id, ct);
        return Ok(new { idCliente, idPeca, hora = registro.DataAcao, ordem = CampoOrdensProjecao.Linha(atual, OperadorId) });
    }

    private static Campanha CampanhaDe(PecaPeriodoStatus p) =>
        p?.PedidoInsercao?.Pedido?.Campanha ?? p?.PedidoReserva?.Pedido?.Campanha ?? p?.Pedido?.Campanha;
}
