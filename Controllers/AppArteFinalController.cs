using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities;
using Veiculando.Domain.Entities.Arte;
using Veiculando.Domain.Enums;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;

namespace Veiculando.WhiteLabel.Api.Controllers;

/// <summary>
/// Aba Arte Final de Minhas Campanhas (VEI-RD-166a): motivos e layouts (motivo × formato).
/// </summary>
/// <remarks>
/// <para>Mesmas entidades e regras do <c>ArteFinalController</c>/<c>ArteFinalHandler</c> do Core, sem
/// passar pelos handlers: eles exigem um usuário Core de agência e quebram para o anunciante direto.
/// Quem enxerga vem de <see cref="AppCampanhasEscopo"/>; quem altera é só a identidade que criou a
/// campanha (a mesma regra do <c>podeEditar</c>: o pj vê a campanha da agência, mas não mexe).</para>
/// <para>Formatos: os do Core (<c>FormatoRepository.ListarPorCampanha</c>), só de peças desta
/// exibidora, o mesmo recorte da aba Produção.</para>
/// <para>Arquivo: gravado no FileServer em <c>conteudo/arte-final/...</c>, como no Core; o download
/// passa pelo BFF.</para>
/// </remarks>
[ApiController]
[Route("api/wl/app/campaigns/{code}/arte-final")]
[Authorize]
public sealed class AppArteFinalController : ControllerBase
{
    public const long TamanhoMaximo = 25 * 1024 * 1024;
    private const string MensagemSemKyc = "A aprovação do cadastro é necessária para ver suas campanhas.";
    private const string NaoEncontrada = "Campanha não encontrada.";
    private const string SemPermissao = "Só quem criou a campanha pode alterar a arte final.";

    /// <summary>Formato sem extensões cadastradas: os tipos de arte mais comuns.</summary>
    public static readonly string[] ExtensoesPadrao =
        { "pdf", "jpg", "jpeg", "png", "tif", "tiff", "ai", "eps", "psd", "cdr", "mp4", "mov" };

    private static readonly string[] Letras = { "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L" };

    private readonly VeiculandoDataContext _db;
    private readonly AppCampanhasEscopo _escopo;
    private readonly ITenantQueries _tenant;
    private readonly IArteFinalFileServer _arquivos;

    public AppArteFinalController(VeiculandoDataContext db, AppCampanhasEscopo escopo, ITenantQueries tenant,
        IArteFinalFileServer arquivos) => (_db, _escopo, _tenant, _arquivos) = (db, escopo, tenant, arquivos);

    public sealed record MotivoRequest(string Nome, string Descricao);

    [HttpGet]
    public async Task<IActionResult> Get(string code, CancellationToken ct)
    {
        var (erro, ctx) = await ResolverAsync(code, ct);
        if (erro != null) return erro;

        var motivos = await _db.Motivos.AsNoTracking()
            .Where(m => m.IdCampanha == ctx.IdCampanha && m.StatusExibicao == StatusExibicaoEnum.Ativo)
            .OrderBy(m => m.Codigo)
            .Select(m => new { m.Id, m.Codigo, m.Nome, m.Descricao })
            .ToListAsync(ct);
        var formatos = await FormatosAsync(ctx.IdCampanha, ct);
        var idsFormato = formatos.Select(f => f.Id).ToList();
        var layouts = await _db.ArtesFinais.AsNoTracking()
            .Where(a => a.Motivo.IdCampanha == ctx.IdCampanha && a.StatusExibicao != StatusExibicaoEnum.Deletado)
            .Select(a => new { a.Id, a.IdMotivo, a.IdFormato, Arquivo = a.ArquivoAlta.Nome, a.DataCadastro })
            .ToListAsync(ct);

        return Ok(new
        {
            podeEditar = ctx.PodeEditar,
            tamanhoMaximo = TamanhoMaximo,
            motivos,
            formatos = formatos.Select(f => new
            {
                f.Id,
                nome = f.Nome,
                midia = f.MidiaTipo == MidiaTipoEnum.Digital ? "digital" : "impressa",
                dimensao = f.Dimensao,
                resolucao = f.Resolucao,
                duracao = f.Duracao > 0 ? (int?)f.Duracao : null,
                extensoes = Extensoes(f),
                especificacaoCriacao = f.EspecificacaoCriacao,
                especificacaoArquivo = f.EspecificacaoArquivo
            }),
            layouts = layouts.Select(l => new
            {
                l.Id,
                l.IdMotivo,
                l.IdFormato,
                arquivo = NomeBase(l.Arquivo),
                enviadoEm = l.DataCadastro,
                // Layout de formato que saiu da campanha (pedido cancelado) continua visível para excluir.
                formatoNaCampanha = idsFormato.Contains(l.IdFormato)
            })
        });
    }

    [HttpPost("motivos")]
    [EnableRateLimiting(Startup.RateLimitEscrita)]
    public async Task<IActionResult> CriarMotivo(string code, [FromBody] MotivoRequest request, CancellationToken ct)
    {
        var (erro, ctx) = await ResolverAsync(code, ct);
        if (erro != null) return erro;
        if (!ctx.PodeEditar) return StatusCode(403, new { message = SemPermissao });

        var nome = request?.Nome?.Trim();
        if (string.IsNullOrEmpty(nome) || nome.Length > 100)
            return BadRequest(new { message = "Informe o nome do motivo (até 100 caracteres)." });
        var descricao = request.Descricao?.Trim();
        if (descricao?.Length > 1000)
            return BadRequest(new { message = "A descrição do motivo tem até 1.000 caracteres." });

        var campanha = await _db.Campanhas.Include(c => c.Motivos).SingleAsync(c => c.Id == ctx.IdCampanha, ct);
        var usados = campanha.Motivos.Select(m => m.Codigo).ToHashSet();
        var codigo = Letras.FirstOrDefault(l => !usados.Contains(l));
        if (codigo == null)
            return Conflict(new { message = $"A campanha já tem o máximo de {Letras.Length} motivos." });

        var usuario = await _db.UsuariosAnunciantes.SingleAsync(u => u.Id == ctx.IdUsuarioCore, ct);
        var motivo = new Motivo(campanha, nome, Veiculando.Shared.Helpers.TextHelper.SecureHTML(descricao), usuario);
        if (!motivo.IsValid())
            return BadRequest(new { message = "Dados do motivo inválidos.", details = motivo.Notifications.Select(n => n.Message) });
        _db.Motivos.Add(motivo);
        // O Core numera pela quantidade de motivos; depois de uma exclusão isso repete a letra.
        _db.Entry(motivo).Property(m => m.Codigo).CurrentValue = codigo;
        await _db.SaveChangesAsync(ct);

        return StatusCode(201, new { motivo.Id, codigo, nome = motivo.Nome, descricao = motivo.Descricao });
    }

    [HttpDelete("motivos/{id:int}")]
    [EnableRateLimiting(Startup.RateLimitEscrita)]
    public async Task<IActionResult> ExcluirMotivo(string code, int id, CancellationToken ct)
    {
        var (erro, ctx) = await ResolverAsync(code, ct);
        if (erro != null) return erro;
        var motivo = await _db.Motivos.SingleOrDefaultAsync(m => m.Id == id && m.IdCampanha == ctx.IdCampanha, ct);
        if (motivo == null) return NotFound(new { message = "Motivo não encontrado." });
        if (!ctx.PodeEditar) return StatusCode(403, new { message = SemPermissao });
        if (await _db.ArtesFinais.AnyAsync(a => a.IdMotivo == id, ct))
            return Conflict(new { message = "Este motivo tem layouts. Exclua os layouts antes de excluir o motivo." });

        _db.Motivos.Remove(motivo);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("layouts")]
    [EnableRateLimiting(Startup.RateLimitEscrita)]
    [RequestSizeLimit(TamanhoMaximo + 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = TamanhoMaximo + 1024 * 1024)]
    public async Task<IActionResult> EnviarLayout(string code, [FromForm] int idMotivo, [FromForm] int idFormato,
        IFormFile file, CancellationToken ct)
    {
        var (erro, ctx) = await ResolverAsync(code, ct);
        if (erro != null) return erro;
        if (!ctx.PodeEditar) return StatusCode(403, new { message = SemPermissao });

        var motivo = await _db.Motivos.Include(m => m.Campanha.Cliente)
            .SingleOrDefaultAsync(m => m.Id == idMotivo && m.IdCampanha == ctx.IdCampanha &&
                                       m.StatusExibicao == StatusExibicaoEnum.Ativo, ct);
        if (motivo == null) return BadRequest(new { message = "Motivo inválido." });
        var formato = (await FormatosAsync(ctx.IdCampanha, ct)).SingleOrDefault(f => f.Id == idFormato);
        if (formato == null) return BadRequest(new { message = "Este formato não faz parte da campanha." });

        if (file == null || file.Length == 0) return BadRequest(new { message = "Selecione o arquivo do layout." });
        if (file.Length > TamanhoMaximo) return BadRequest(new { message = "O layout tem até 25 MB." });
        var extensao = Path.GetExtension(file.FileName ?? string.Empty).TrimStart('.').ToLowerInvariant();
        var aceitas = Extensoes(formato);
        if (!aceitas.Contains(extensao))
            return BadRequest(new { message = $"Extensão não aceita neste formato. Use: {string.Join(", ", aceitas).ToUpperInvariant()}." });

        // Mesmo caminho do ArteFinalHandler do Core.
        var caminho = $"arte-final/{motivo.Campanha.Cliente.Codigo}/{motivo.Campanha.Codigo}/motivo-{motivo.Codigo}_{formato}_HI_{DateTime.Now.Ticks}.{extensao}"
            .ToLowerInvariant();
        string salvo;
        try
        {
            await using var conteudo = file.OpenReadStream();
            salvo = await _arquivos.EnviarAsync(caminho, conteudo, ct);
        }
        catch (ArteFinalUploadException ex)
        {
            return StatusCode(502, new { message = ex.Message });
        }
        catch (HttpRequestException)
        {
            return StatusCode(503, new { message = "O servidor de arquivos não respondeu. Tente novamente." });
        }

        // Um layout por motivo × formato: enviar de novo substitui o anterior.
        var anteriores = await _db.ArtesFinais.Where(a => a.IdMotivo == idMotivo && a.IdFormato == idFormato).ToListAsync(ct);
        _db.ArtesFinais.RemoveRange(anteriores);

        var usuario = await _db.UsuariosAnunciantes.Include(u => u.Agencia).SingleAsync(u => u.Id == ctx.IdUsuarioCore, ct);
        // Rastreado: com o da consulta AsNoTracking, o EF inseriria uma cópia do formato.
        var formatoRastreado = await _db.Formatos.SingleAsync(f => f.Id == idFormato, ct);
        var arte = new ArteFinal(motivo, formatoRastreado, salvo, "temp.jpg", usuario);
        if (!arte.IsValid())
            return BadRequest(new { message = "Dados do layout inválidos.", details = arte.Notifications.Select(n => n.Message) });
        arte.RegistrarOrigem(FonteOrigemEnum.WhiteLabel, usuario.Agencia?.Id, usuario.Id);
        _db.ArtesFinais.Add(arte);
        await _db.SaveChangesAsync(ct);

        return StatusCode(201, new
        {
            arte.Id, arte.IdMotivo, arte.IdFormato, arquivo = NomeBase(salvo), enviadoEm = arte.DataCadastro,
            formatoNaCampanha = true
        });
    }

    [HttpDelete("layouts/{id:int}")]
    [EnableRateLimiting(Startup.RateLimitEscrita)]
    public async Task<IActionResult> ExcluirLayout(string code, int id, CancellationToken ct)
    {
        var (erro, ctx) = await ResolverAsync(code, ct);
        if (erro != null) return erro;
        var arte = await _db.ArtesFinais.SingleOrDefaultAsync(a => a.Id == id && a.Motivo.IdCampanha == ctx.IdCampanha, ct);
        if (arte == null) return NotFound(new { message = "Layout não encontrado." });
        if (!ctx.PodeEditar) return StatusCode(403, new { message = SemPermissao });

        // Como no Core, o arquivo fica no FileServer; sai só o registro.
        _db.ArtesFinais.Remove(arte);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("layouts/{id:int}/arquivo")]
    public async Task<IActionResult> BaixarLayout(string code, int id, CancellationToken ct)
    {
        var (erro, ctx) = await ResolverAsync(code, ct);
        if (erro != null) return erro;
        var nome = await _db.ArtesFinais.AsNoTracking()
            .Where(a => a.Id == id && a.Motivo.IdCampanha == ctx.IdCampanha)
            .Select(a => a.ArquivoAlta.Nome)
            .SingleOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(nome)) return NotFound(new { message = "Layout não encontrado." });

        ArteFinalArquivo arquivo;
        try { arquivo = await _arquivos.ObterAsync(nome, ct); }
        catch (HttpRequestException) { return StatusCode(503, new { message = "O servidor de arquivos não respondeu." }); }
        if (arquivo == null) return NotFound(new { message = "O arquivo deste layout não está mais no servidor." });
        return File(arquivo.Conteudo, arquivo.ContentType, NomeBase(nome));
    }

    private sealed record Contexto(int IdCampanha, bool PodeEditar, int IdUsuarioCore);

    private async Task<(IActionResult, Contexto)> ResolverAsync(string code, CancellationToken ct)
    {
        if (User.FindFirstValue("WlPerfil") != "Anunciante" ||
            !int.TryParse(User.FindFirstValue("WlAnuncianteId"), out var userId))
            return (Unauthorized(), null);
        if (string.IsNullOrWhiteSpace(code) || code.Length > 50) return (NotFound(new { message = NaoEncontrada }), null);
        var escopo = await _escopo.ResolverAsync(User, userId, MensagemSemKyc, ct);
        if (escopo.StatusErro.HasValue)
            return (escopo.StatusErro == 401 ? Unauthorized() : StatusCode(escopo.StatusErro.Value, new { message = escopo.MensagemErro }), null);

        var daCampanha = escopo.MinhasCampanhas.Where(c => c.Codigo == code);
        var campanha = await daCampanha.Select(c => new { c.Id, c.IdUsuarioAnunciante, c.Status }).FirstOrDefaultAsync(ct);
        if (campanha == null) return (NotFound(new { message = NaoEncontrada }), null);
        var criadaPelaSessao = await daCampanha.AnyAsync(escopo.CriadaPelaSessao, ct);
        return (null, new Contexto(campanha.Id, criadaPelaSessao && campanha.Status != StatusCampanhaEnum.Cancelada,
            campanha.IdUsuarioAnunciante));
    }

    /// <summary>Formatos de arte final das peças desta exibidora na campanha (regra do Core: status &gt; 0).</summary>
    private async Task<List<Formato>> FormatosAsync(int idCampanha, CancellationToken ct)
    {
        var afiliada = _tenant.AfiliadaId;
        var ids = _db.PedidoItens
            .Where(i => i.Pedido.IdCampanha == idCampanha && i.Peca.Local.IdAfiliada == afiliada &&
                        (int)i.Status > 0 && (int)i.Pedido.Status > 0 && i.Peca.IdFormatoArteFinal != null)
            .Select(i => i.Peca.IdFormatoArteFinal.Value)
            .Distinct();
        var formatos = await _db.Formatos.AsNoTracking().Where(f => ids.Contains(f.Id)).ToListAsync(ct);
        return formatos.OrderBy(f => f.MidiaTipo == MidiaTipoEnum.Digital ? 0 : 1).ThenBy(f => f.Nome).ToList();
    }

    private static string[] Extensoes(Formato formato)
    {
        var lista = (formato.ExtensoesAceitas ?? string.Empty)
            .Split(new[] { ';', ',', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Trim().TrimStart('.').ToLowerInvariant())
            .Where(e => e.Length > 0)
            .Distinct()
            .ToArray();
        return lista.Length > 0 ? lista : ExtensoesPadrao;
    }

    private static string NomeBase(string caminho) =>
        string.IsNullOrEmpty(caminho) ? null : caminho[(caminho.LastIndexOf('/') + 1)..];
}
