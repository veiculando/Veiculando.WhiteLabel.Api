using System;
using System.ComponentModel.DataAnnotations;
using System.Data.Entity;
using System.Data.Entity.Infrastructure;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities;
using Veiculando.Domain.Entities.OrdensServico;
using Veiculando.Domain.Enums;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;

namespace Veiculando.WhiteLabel.Api.Controllers;

public sealed class CampoCheckingPedido
{
    [Required] public IFormFile Foto { get; set; }
    [Required] public Guid? IdCliente { get; set; }
    [Required] public DateTimeOffset? Hora { get; set; }
    [Required, Range(-90d, 90d)] public double? Latitude { get; set; }
    [Required, Range(-180d, 180d)] public double? Longitude { get; set; }
    [MaxLength(100)] public string Modelo { get; set; }
    [MaxLength(100)] public string Sistema { get; set; }
    [MaxLength(100)] public string VersaoApp { get; set; }
}

// VEI-RD-141c. Checking de veiculacao pelo fiscal atual: o arquivo vai para o
// storage privado WL e a regra e a do Core (CheckingColagemCampo). Sucesso so
// depois das duas gravacoes; se o banco falhar, o pipeline apaga o arquivo.
[Route("api/wl/campo/ordens")]
public sealed class CampoCheckingController : CampoControllerBase
{
    public const long LimiteFoto = 10 * 1024 * 1024;
    private const int Tentativas = 3;

    private readonly VeiculandoDataContext _db;
    private readonly ITenantQueries _queries;
    private readonly IFileValidationService _validation;
    private readonly WlUploadPipeline _pipeline;
    private readonly WlUploadReferences _references;
    private readonly ILogger<CampoCheckingController> _logger;

    public CampoCheckingController(VeiculandoDataContext db, ITenantQueries queries, ITenantContext tenant,
        IFileValidationService validation, WlUploadPipeline pipeline, WlUploadReferences references,
        ILogger<CampoCheckingController> logger) : base(tenant)
    {
        _db = db;
        _queries = queries;
        _validation = validation;
        _pipeline = pipeline;
        _references = references;
        _logger = logger;
    }

    [HttpPost("{id:int}/pecas/{idPeca:int}/checking")]
    [RequestSizeLimit(LimiteFoto + 1024 * 1024)]
    public async Task<IActionResult> Registrar(int id, int idPeca, [FromForm] CampoCheckingPedido pedido, CancellationToken ct)
    {
        // Fora da janela de HoraCampo, vale esta hora no lugar da do aparelho.
        var recebido = DateTimeOffset.UtcNow;
        if (pedido.IdCliente == Guid.Empty)
            return BadRequest(new { message = "Informe o identificador da ação." });
        if (!await CampoOrdensController.Visiveis(_queries, AfiliadaId, new FiltroListaCampo(AfiliadaId, OperadorId))
                .AnyAsync(o => o.Id == id, ct))
            return NotFound(new { message = "Ordem de serviço não encontrada." });

        var idCliente = pedido.IdCliente.Value;
        // Repeticao do aparelho: devolve a foto ja gravada sem tocar o storage.
        var existente = await Gravada(idCliente, ct);
        if (existente != null) return await Responder(existente, id, idPeca, ct);

        if (!_validation.IsValidFile(pedido.Foto, LimiteFoto, out var erro)) return BadRequest(new { message = erro });
        if (pedido.Foto.ContentType != "image/jpeg" && pedido.Foto.ContentType != "image/png")
            return BadRequest(new { message = "Envie uma foto JPG ou PNG." });

        // Pre-condicoes do Core antes de subir o arquivo. Sem item, o Core so
        // devolve checking_indisponivel depois de todas as outras recusas.
        var ordemAtual = await _db.OrdensServico.AsNoTracking().Include(o => o.Pecas).SingleAsync(o => o.Id == id, ct);
        var previa = CheckingColagemCampo.Executar(ordemAtual, null, null, null, new ComandoCheckingCampo(idCliente, OperadorId,
            idPeca, pedido.Hora.Value, pedido.Latitude.Value, pedido.Longitude.Value, null, null, null, null, 0, recebido), null);
        if (previa.CodigoRecusa != RecusaCampo.CheckingIndisponivel)
            return RecusarCampo(previa.CodigoRecusa);

        var item = await ItemDoPeriodo(ordemAtual.IdPeriodo, idPeca, ct);
        if (item == null)
        {
            // Anomalia de dados: a peca colada nao tem item de checking no PI do periodo. Nunca criar.
            _logger.LogError("CAMPO_CHECKING_ITEM_AUSENTE tenant={Tenant} ordem={Ordem} peca={Peca}", AfiliadaId, id, idPeca);
            return RecusarCampo(RecusaCampo.CheckingIndisponivel);
        }
        // VEI-RD-143: outro operador ja fez o checking deste item nesta ordem; recusa antes do upload.
        if (await _db.CheckingCampoAutores.AsNoTracking().AnyAsync(a => a.IdPedidoItem == item.IdPedidoItem
                && a.IdOrdemServico == id && a.IdOperador != OperadorId, ct))
            return RecusarCampo(RecusaCampo.CheckingJaRealizado);

        using var corpo = new MemoryStream();
        using (var origem = pedido.Foto.OpenReadStream())
        {
            var buffer = new byte[81920];
            int lidos;
            while ((lidos = await origem.ReadAsync(buffer, ct)) > 0)
            {
                if (corpo.Length + lidos > LimiteFoto)
                    return StatusCode(413, new { message = "Arquivo maior que o limite permitido." });
                await corpo.WriteAsync(buffer.AsMemory(0, lidos), ct);
            }
        }
        if (corpo.Length != pedido.Foto.Length || corpo.Length == 0)
            return BadRequest(new { message = "Upload incompleto. Tire a foto e tente novamente." });
        corpo.Position = 0;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(corpo, ct)).ToLowerInvariant();
        corpo.Position = 0;
        var nome = "wl-" + Guid.NewGuid().ToString("N") + (pedido.Foto.ContentType == "image/png" ? ".png" : ".jpg");
        // Mesmo formato de chave do UploadsController: download e reconciliador ja a reconhecem.
        var arquivo = new WlStoredFile($"tenant-{AfiliadaId}/checking/{item.IdPedidoItem}/{nome}", nome,
            pedido.Foto.ContentType, corpo.Length, hash, DateTimeOffset.UtcNow);
        var comando = new ComandoCheckingCampo(idCliente, OperadorId, idPeca, pedido.Hora.Value, pedido.Latitude.Value,
            pedido.Longitude.Value, pedido.Modelo, pedido.Sistema, pedido.VersaoApp,
            WlUploadReferences.Prefix + arquivo.Key, checked((int)arquivo.Size), recebido);

        try
        {
            await _pipeline.SaveAsync(arquivo, corpo, AtorStorage, () => Gravar(id, item.IdPedidoItem, comando, ct),
                () => _references.ExistsAsync(arquivo.Key, CancellationToken.None), ct);
        }
        catch (RecusaCheckingException recusa)
        {
            // A mesma acao pode ter sido gravada por uma requisicao concorrente,
            // que concluiu a ordem antes desta recarregar o grafo.
            existente = await Gravada(idCliente, ct);
            if (existente == null) return RecusarCampo(recusa.Codigo);
        }
        catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException)
        {
            // Corrida com o mesmo UUID: o indice unico de IdClienteAcao recusou esta gravacao.
            existente = await Gravada(idCliente, ct);
            if (existente == null) throw;
        }
        catch (DbUpdateConcurrencyException)
        {
            return RecusarCampo(OrdemAlterada);
        }
        catch (FalhaGravacaoException falha)
        {
            // Erro de regra/grafo nao melhora com reenvio: 500, para a fila do app nao repetir para sempre.
            _logger.LogError(falha.InnerException, "CAMPO_CHECKING_GRAVACAO_FALHOU tenant={Tenant} operador={Operador}",
                AfiliadaId, OperadorId);
            return StatusCode(500, new { message = "Não foi possível registrar o checking. Avise a exibidora." });
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not DbUpdateException)
        {
            // So chega aqui falha do storage: transitoria, o app reenvia.
            _logger.LogError(ex, "CAMPO_CHECKING_FALHOU tenant={Tenant} operador={Operador}", AfiliadaId, OperadorId);
            return StatusCode(503, new { message = "Não foi possível salvar a foto. Tente novamente." });
        }
        existente ??= await Gravada(idCliente, ct);
        return await Responder(existente, id, idPeca, ct);
    }

    // A ordem tem TimeStamp: outra acao de campo simultanea faz o SaveChanges
    // falhar. Recarrega o grafo e reaplica a regra do Core.
    private async Task Gravar(int id, int idPedidoItem, ComandoCheckingCampo comando, CancellationToken ct)
    {
        try
        {
            await GravarComRetentativa(id, idPedidoItem, comando, ct);
        }
        catch (Exception ex) when (ex is not DbUpdateException && ex is not RecusaCheckingException
            && ex is not OperationCanceledException)
        {
            throw new FalhaGravacaoException(ex);
        }
    }

    private async Task GravarComRetentativa(int id, int idPedidoItem, ComandoCheckingCampo comando, CancellationToken ct)
    {
        for (var tentativa = 1; ; tentativa++)
        {
            var ordem = await _db.OrdensServico.Include(o => o.Pecas).SingleAsync(o => o.Id == id, ct);
            // RegistrarFoto propaga o status ao item de PI, ao item do pedido e ao checking.
            var item = await _db.CheckingItens
                .Include(i => i.Fotos)
                .Include(i => i.Peca.Local) // CheckingFoto mede a distancia ate a peca.
                .Include(i => i.Checking.Itens)
                .Include(i => i.PedidoInsercaoItem.PedidoItem)
                .Include(i => i.PedidoInsercaoItem.PedidoInsercao.Itens.Select(x => x.PedidoItem))
                .Include(i => i.PedidoInsercaoItem.PedidoInsercao.Pedido.Campanha.Pedidos.Select(p => p.PedidosInsercao))
                .SingleAsync(i => i.IdPedidoItem == idPedidoItem, ct);
            var operador = await _db.UsuariosOperador.SingleAsync(u => u.Id == comando.IdOperador, ct);
            var autor = await _db.CheckingCampoAutores.SingleOrDefaultAsync(a => a.IdPedidoItem == idPedidoItem
                && a.IdOrdemServico == id, ct);

            var resultado = CheckingColagemCampo.Executar(ordem, item, null, autor, comando, operador);
            if (resultado.CodigoRecusa != null) throw new RecusaCheckingException(resultado.CodigoRecusa);
            _db.CheckingFotos.Add(resultado.Foto);
            if (resultado.Autor != null) _db.CheckingCampoAutores.Add(resultado.Autor);
            try
            {
                await _db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException ex) when (tentativa < Tentativas)
            {
                foreach (var entrada in _db.ChangeTracker.Entries().ToList())
                    entrada.State = EntityState.Detached;
                // Indice unico do IdClienteAcao: a mesma acao ja foi gravada; quem chamou devolve essa foto.
                if (ex is not DbUpdateConcurrencyException && await Gravada(comando.IdCliente, ct) != null) throw;
                // TimeStamp da ordem ou PK de CheckingCampoAutor (primeiro autor concorrente): reaplica a regra.
            }
        }
    }

    // Item de PI da peca no periodo da ordem, na afiliada do token:
    // PecaPeriodoStatus -> PedidoInsercao -> CheckingItem.
    private async Task<CheckingItem> ItemDoPeriodo(int idPeriodo, int idPeca, CancellationToken ct)
    {
        var idInsercao = await _queries.PecaPeriodoStatus.AsNoTracking()
            .Where(p => p.IdPeca == idPeca && p.IdPeriodo == idPeriodo && p.IdPedidoInsercao != null
                && p.PedidoInsercao.IdAfiliada == AfiliadaId && p.PedidoInsercao.StatusExibicao == StatusExibicaoEnum.Ativo)
            .OrderByDescending(p => p.Id).Select(p => p.IdPedidoInsercao).FirstOrDefaultAsync(ct);
        if (idInsercao == null) return null;
        return await _db.CheckingItens.AsNoTracking()
            .FirstOrDefaultAsync(i => i.IdPeca == idPeca && i.PedidoInsercaoItem.IdPedidoInsercao == idInsercao
                && i.Peca.Local.IdAfiliada == AfiliadaId, ct);
    }

    private Task<CheckingFoto> Gravada(Guid idCliente, CancellationToken ct) =>
        _db.CheckingFotos.AsNoTracking().Include(f => f.CheckingItem)
            .FirstOrDefaultAsync(f => f.IdClienteAcao == idCliente, ct);

    private async Task<IActionResult> Responder(CheckingFoto foto, int id, int idPeca, CancellationToken ct)
    {
        if (foto.IdOperadorCampo != OperadorId || foto.IdOrdemServicoCampo != id || foto.CheckingItem.IdPeca != idPeca)
            return RecusarCampo(IdClienteReutilizado);
        var ordem = await CampoOrdensController.ComResumo(
                CampoOrdensController.Visiveis(_queries, AfiliadaId, new FiltroListaCampo(AfiliadaId, OperadorId)))
            .FirstAsync(o => o.Id == id, ct);
        return Ok(new { idCliente = foto.IdClienteAcao, idPeca, idFoto = foto.Id, hora = foto.DataCapturaAparelho,
            ordem = CampoOrdensProjecao.Linha(ordem, OperadorId) });
    }

    // Metadado do blob: o usuario WL do operador quando existe; senao o do Core.
    private int AtorStorage =>
        int.TryParse(User.FindFirst(CampoAuthController.ClaimWlUsuarioOperadorId)?.Value, out var wl) ? wl : OperadorId;

    private sealed class FalhaGravacaoException : Exception
    {
        public FalhaGravacaoException(Exception causa) : base(causa.Message, causa) { }
    }

    private sealed class RecusaCheckingException : Exception
    {
        public RecusaCheckingException(string codigo) : base(codigo) => Codigo = codigo;
        public string Codigo { get; }
    }
}
