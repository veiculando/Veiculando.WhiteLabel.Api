using System;
using System.Data.Entity;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities.OrdensServico;
using Veiculando.Domain.Enums;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;

namespace Veiculando.WhiteLabel.Api.Controllers;

// VEI-RD-154d. Cada imagem so sai se o detalhe de alguma ordem visivel ao
// operador a referenciaria; senao 404, antes de tocar storage ou FileServer.
[Route("api/wl/campo/imagens")]
public sealed class CampoImagensController : CampoControllerBase
{
    private readonly VeiculandoDataContext _db;
    private readonly ITenantQueries _queries;
    private readonly IWlUploadStorage _storage;
    private readonly IWlArquivoLegado _legado;

    public CampoImagensController(VeiculandoDataContext db, ITenantQueries queries, ITenantContext tenant,
        IWlUploadStorage storage, IWlArquivoLegado legado) : base(tenant)
    {
        _db = db;
        _queries = queries;
        _storage = storage;
        _legado = legado;
    }

    private IQueryable<OrdemServico> Iniciadas() =>
        CampoOrdensController.Visiveis(_queries, AfiliadaId, new FiltroListaCampo(AfiliadaId, OperadorId))
            .Where(o => o.DataInicio != null);

    [HttpGet("checking/{id:int}")]
    public async Task<IActionResult> Checking(int id, CancellationToken ct)
    {
        var ordens = Iniciadas();
        var foto = await _db.CheckingFotos.AsNoTracking()
            .Where(f => f.Id == id
                && f.CheckingItem.Peca.Local.IdAfiliada == AfiliadaId
                && f.CheckingItem.Checking.PedidoInsercao.IdAfiliada == AfiliadaId
                && (f.StatusExibicao == StatusExibicaoEnum.Ativo || f.StatusExibicao == StatusExibicaoEnum.AprovacaoPendente)
                && ordens.Any(o => o.Pecas.Any(p => p.IdPeca == f.CheckingItem.IdPeca)
                    && f.CheckingItem.PedidoInsercaoItem.PedidoItem.Periodo.DataInicio < o.Periodo.DataInicio))
            .Select(f => f.UrlArquivo).FirstOrDefaultAsync(ct);
        var origem = CampoImagemCaminho.Checking(foto);
        if (origem == null) return NotFound();
        return origem.Value.ChaveWl != null ? await DoStorage(origem.Value.ChaveWl, ct) : await DoLegado(origem.Value.Legado, ct);
    }

    [HttpGet("arte/{id:int}")]
    public async Task<IActionResult> Arte(int id, CancellationToken ct)
    {
        var arte = await _db.ArtesFinais.AsNoTracking()
            .Where(a => a.Id == id && a.StatusExibicao == StatusExibicaoEnum.Ativo
                && a.Motivo.StatusExibicao == StatusExibicaoEnum.Ativo)
            .Select(a => new { a.IdFormato, a.Motivo.IdCampanha, Arquivo = a.ArquivoAlta.Nome }).FirstOrDefaultAsync(ct);
        var caminho = CampoImagemCaminho.Arte(arte?.Arquivo);
        if (caminho == null) return NotFound();
        // A arte e da campanha atual de uma peca, do mesmo formato, numa ordem visivel.
        var visivel = await Iniciadas().SelectMany(o => o.Pecas.Select(p => new { p.IdPeca, o.IdPeriodo, p.Peca.IdFormatoArteFinal }))
            .Where(x => x.IdFormatoArteFinal == arte.IdFormato)
            .AnyAsync(x => _queries.PecaPeriodoStatus.Any(s => s.IdPeca == x.IdPeca && s.IdPeriodo == x.IdPeriodo
                && (s.PedidoInsercao.Pedido.Campanha.Id == arte.IdCampanha
                    || s.PedidoReserva.Pedido.Campanha.Id == arte.IdCampanha
                    || s.Pedido.Campanha.Id == arte.IdCampanha)), ct);
        return visivel ? await DoLegado(caminho, ct) : NotFound();
    }

    private async Task<IActionResult> DoStorage(string chave, CancellationToken ct)
    {
        var arquivo = await _storage.InfoAsync(chave, ct);
        return Arquivo(await _storage.ReadAsync(chave, ct), arquivo.ContentType);
    }

    private async Task<IActionResult> DoLegado(string caminho, CancellationToken ct)
    {
        var imagem = await _legado.AbrirAsync(caminho, ct);
        return imagem == null ? NotFound() : Arquivo(imagem.Conteudo, imagem.ContentType);
    }

    private IActionResult Arquivo(System.IO.Stream conteudo, string contentType)
    {
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(conteudo, contentType);
    }
}
