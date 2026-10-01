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

/// <summary>Campos do multipart de marca parceira (POST e PUT).</summary>
public sealed class MarcaForm
{
    public string Name { get; set; }
    public int? DisplayOrder { get; set; }

    /// <summary>Ausente no POST = inativa. Ausente no PUT = mantém.</summary>
    public bool? Ativo { get; set; }

    /// <summary>PNG, JPG, WebP ou SVG (sanitizado) até 2 MB. Obrigatória no POST.</summary>
    public IFormFile Imagem { get; set; }
}

/// <summary>Logos de marcas parceiras da LP (<c>api/wl/cms/marcas</c>).</summary>
[Route("api/wl/cms/marcas")]
public sealed class MarcasController : CmsControllerBase<CmsMarcaLinha>
{
    public MarcasController(CmsDependencias dependencias) : base(dependencias) { }

    protected override string Tabela => CmsSupabase.Marcas;
    protected override string Nome => "Marca";
    protected override object ParaDto(CmsMarcaLinha linha) => linha.ParaDto();
    protected override Guid IdDe(CmsMarcaLinha linha) => linha.Id;

    [HttpGet]
    public Task<IActionResult> Listar([FromQuery] WlPaginaRequest pagina, [FromQuery] string busca,
        [FromQuery] string status, CancellationToken ct) =>
        ListarAsync(pagina, status, new[] { PostgrestFiltro.Busca(busca, "name") }, ct);

    [HttpPost]
    [EnableRateLimiting(Startup.RateLimitEscrita)]
    [CmsLimiteCorpo]
    [RequestSizeLimit(CmsLimites.CorpoMultipart)]
    [RequestFormLimits(MultipartBodyLengthLimit = CmsLimites.CorpoMultipart)]
    public async Task<IActionResult> Criar([FromForm] MarcaForm form, CancellationToken ct)
    {
        if (!CmsAutor.TentarLer(User, D.Tenant, out var autor))
            return SemAutor();
        if (!TentarTexto(form.Name, "o nome da marca", TamanhoMaximoTexto, out var nome, out var erro))
            return erro;
        if (!CmsArquivos.TentarImagem(form.Imagem, CmsLimites.Logo, aceitaSvg: true, out var imagem, out var motivo))
            return Erro(StatusCodes.Status400BadRequest, motivo);

        var linha = new Dictionary<string, object>
        {
            ["name"] = nome,
            ["is_active"] = form.Ativo ?? false,
            ["display_order"] = form.DisplayOrder,
        };

        return await CriarAsync(autor, linha,
            new List<(string, string, CmsArquivo)> { ("image_url", CmsSupabase.BucketAssets, imagem) },
            Array.Empty<string>(), ct);
    }

    [HttpPut("{id:guid}")]
    [EnableRateLimiting(Startup.RateLimitEscrita)]
    [CmsLimiteCorpo]
    [RequestSizeLimit(CmsLimites.CorpoMultipart)]
    [RequestFormLimits(MultipartBodyLengthLimit = CmsLimites.CorpoMultipart)]
    public async Task<IActionResult> Atualizar(Guid id, [FromForm] MarcaForm form, CancellationToken ct)
    {
        if (!CmsAutor.TentarLer(User, D.Tenant, out var autor))
            return SemAutor();
        if (!TentarTexto(form.Name, "o nome da marca", TamanhoMaximoTexto, out var nome, out var erro))
            return erro;

        var trocas = new List<CmsTroca>();
        if (form.Imagem != null)
        {
            if (!CmsArquivos.TentarImagem(form.Imagem, CmsLimites.Logo, aceitaSvg: true, out var imagem, out var motivo))
                return Erro(StatusCodes.Status400BadRequest, motivo);
            trocas.Add(new CmsTroca("image_url", CmsSupabase.BucketAssets, Tabela, imagem));
        }

        var atual = await BuscarAsync(id, ct);
        if (atual == null)
            return NaoEncontrado();

        var alteracoes = new Dictionary<string, object> { ["name"] = nome };
        if (form.DisplayOrder.HasValue) alteracoes["display_order"] = form.DisplayOrder.Value;
        if (form.Ativo.HasValue) alteracoes["is_active"] = form.Ativo.Value;

        return await AtualizarAsync(id, autor, atual, alteracoes, trocas, Array.Empty<string>(), ct);
    }
}
