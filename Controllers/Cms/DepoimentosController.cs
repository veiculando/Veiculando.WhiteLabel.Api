using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Veiculando.WhiteLabel.Api.Services;
using Veiculando.WhiteLabel.Api.Services.Cms;

namespace Veiculando.WhiteLabel.Api.Controllers.Cms;

/// <summary>Campos do multipart de depoimento (POST e PUT).</summary>
public sealed class DepoimentoForm
{
    public string Author { get; set; }

    /// <summary>Cargo.</summary>
    public string Role { get; set; }

    public string Company { get; set; }
    public string Content { get; set; }
    public int? DisplayOrder { get; set; }

    /// <summary>Ausente no POST = oculto. Ausente no PUT = mantém.</summary>
    public bool? Ativo { get; set; }

    /// <summary>Foto opcional: PNG, JPG ou WebP até 2 MB.</summary>
    public IFormFile Avatar { get; set; }
}

/// <summary>Depoimentos da LP (<c>api/wl/cms/depoimentos</c>).</summary>
[Route("api/wl/cms/depoimentos")]
public sealed class DepoimentosController : CmsControllerBase<CmsDepoimentoLinha>
{
    /// <summary>
    /// A tela limita o relato a 400 caracteres. O servidor aceita mais, para não
    /// recusar a edição de um depoimento antigo, mais longo que isso, que só teve o
    /// cargo corrigido.
    /// </summary>
    private const int TamanhoMaximoRelato = 2000;

    public DepoimentosController(CmsDependencias dependencias) : base(dependencias) { }

    protected override string Tabela => CmsSupabase.Depoimentos;
    protected override string Nome => "Depoimento";
    protected override object ParaDto(CmsDepoimentoLinha linha) => linha.ParaDto();
    protected override Guid IdDe(CmsDepoimentoLinha linha) => linha.Id;

    [HttpGet]
    public Task<IActionResult> Listar([FromQuery] WlPaginaRequest pagina, [FromQuery] string busca,
        [FromQuery] string status, [FromQuery] string empresa, [FromQuery] string desde, CancellationToken ct)
    {
        var filtros = new List<string> { PostgrestFiltro.Busca(busca, "author", "company", "content") };

        if (!string.IsNullOrWhiteSpace(empresa))
            filtros.Add(PostgrestFiltro.Igual("company", empresa.Trim()));

        if (!string.IsNullOrWhiteSpace(desde))
        {
            if (!PostgrestFiltro.TentarDesde(desde.Trim(), out var filtroDesde))
                return Task.FromResult(Erro(StatusCodes.Status400BadRequest, "Data inválida. Use o formato aaaa-mm-dd."));
            filtros.Add(filtroDesde);
        }

        return ListarAsync(pagina, status, filtros, ct);
    }

    /// <summary>
    /// KPIs da tela, calculados no banco pela RPC <c>cms_depoimentos_resumo</c>: o
    /// PostgREST não faz COUNT DISTINCT, e contar em memória exigiria trazer a
    /// tabela inteira.
    /// </summary>
    [HttpGet("resumo")]
    public async Task<IActionResult> Resumo(CancellationToken ct)
    {
        var resumo = await D.Supabase.RpcAsync<CmsDepoimentosResumoRpc>(CmsSupabase.RpcResumoDepoimentos, ct);
        return Ok(resumo.ParaDto());
    }

    [HttpPost]
    [EnableRateLimiting(Startup.RateLimitEscrita)]
    [CmsLimiteCorpo]
    [RequestSizeLimit(CmsLimites.CorpoMultipart)]
    [RequestFormLimits(MultipartBodyLengthLimit = CmsLimites.CorpoMultipart)]
    public async Task<IActionResult> Criar([FromForm] DepoimentoForm form, CancellationToken ct)
    {
        if (!CmsAutor.TentarLer(User, D.Tenant, out var autor))
            return SemAutor();
        if (!TentarCampos(form, out var campos, out var erro))
            return erro;

        var arquivos = new List<(string, string, CmsArquivo)>();
        if (form.Avatar != null)
        {
            if (!CmsArquivos.TentarImagem(form.Avatar, CmsLimites.Avatar, aceitaSvg: false, out var avatar, out var motivo))
                return Erro(StatusCodes.Status400BadRequest, motivo);
            arquivos.Add(("avatar_url", CmsSupabase.BucketAssets, avatar));
        }

        campos["is_active"] = form.Ativo ?? false;
        campos["display_order"] = form.DisplayOrder;

        return await CriarAsync(autor, campos, arquivos, Array.Empty<string>(), ct);
    }

    [HttpPut("{id:guid}")]
    [EnableRateLimiting(Startup.RateLimitEscrita)]
    [CmsLimiteCorpo]
    [RequestSizeLimit(CmsLimites.CorpoMultipart)]
    [RequestFormLimits(MultipartBodyLengthLimit = CmsLimites.CorpoMultipart)]
    public async Task<IActionResult> Atualizar(Guid id, [FromForm] DepoimentoForm form, CancellationToken ct)
    {
        if (!CmsAutor.TentarLer(User, D.Tenant, out var autor))
            return SemAutor();
        if (!TentarCampos(form, out var alteracoes, out var erro))
            return erro;

        var trocas = new List<CmsTroca>();
        if (form.Avatar != null)
        {
            if (!CmsArquivos.TentarImagem(form.Avatar, CmsLimites.Avatar, aceitaSvg: false, out var avatar, out var motivo))
                return Erro(StatusCodes.Status400BadRequest, motivo);
            trocas.Add(new CmsTroca("avatar_url", CmsSupabase.BucketAssets, Tabela, avatar));
        }

        var atual = await BuscarAsync(id, ct);
        if (atual == null)
            return NaoEncontrado();

        if (form.DisplayOrder.HasValue) alteracoes["display_order"] = form.DisplayOrder.Value;
        if (form.Ativo.HasValue) alteracoes["is_active"] = form.Ativo.Value;

        return await AtualizarAsync(id, autor, atual, alteracoes, trocas, Array.Empty<string>(), ct);
    }

    private bool TentarCampos(DepoimentoForm form, out Dictionary<string, object> campos, out IActionResult erro)
    {
        campos = null;
        if (!TentarTexto(form.Author, "o autor", TamanhoMaximoTexto, out var autor, out erro)
            || !TentarTexto(form.Content, "o depoimento", TamanhoMaximoRelato, out var relato, out erro))
            return false;

        var cargo = Opcional(form.Role);
        var empresa = Opcional(form.Company);
        if ((cargo?.Length ?? 0) > TamanhoMaximoTexto || (empresa?.Length ?? 0) > TamanhoMaximoTexto)
        {
            erro = Erro(StatusCodes.Status400BadRequest, $"Cargo e empresa têm limite de {TamanhoMaximoTexto} caracteres.");
            return false;
        }

        campos = new Dictionary<string, object>
        {
            ["author"] = autor,
            ["content"] = relato,
            ["role"] = cargo,
            ["company"] = empresa,
        };
        return true;
    }
}
