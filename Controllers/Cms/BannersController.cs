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

/// <summary>Campos do multipart de banner (POST e PUT).</summary>
public sealed class BannerForm
{
    public string Title { get; set; }

    /// <summary><c>link</c> (padrão) ou <c>html</c>.</summary>
    public string TipoDestino { get; set; }

    /// <summary>Só no tipo link: <c>https://…</c> ou <c>#secao</c>.</summary>
    public string Destino { get; set; }

    public int? DisplayOrder { get; set; }

    /// <summary>Ausente no POST = inativo. Ausente no PUT = mantém.</summary>
    public bool? Ativo { get; set; }

    /// <summary>PNG, JPG ou WebP até 5 MB. Obrigatória no POST.</summary>
    public IFormFile Imagem { get; set; }

    /// <summary>Hotsite .html até 2 MB. Obrigatório no POST do tipo html.</summary>
    public IFormFile Html { get; set; }
}

/// <summary>Banners do carrossel da LP (<c>api/wl/cms/banners</c>).</summary>
[Route("api/wl/cms/banners")]
public sealed class BannersController : CmsControllerBase<CmsBannerLinha>
{
    public BannersController(CmsDependencias dependencias) : base(dependencias) { }

    protected override string Tabela => CmsSupabase.Banners;
    protected override string Nome => "Banner";
    protected override object ParaDto(CmsBannerLinha linha) => linha.ParaDto();
    protected override Guid IdDe(CmsBannerLinha linha) => linha.Id;

    [HttpGet]
    public Task<IActionResult> Listar([FromQuery] WlPaginaRequest pagina, [FromQuery] string busca,
        [FromQuery] string status, CancellationToken ct) =>
        ListarAsync(pagina, status, new[] { PostgrestFiltro.Busca(busca, "title", "destino") }, ct);

    [HttpPost]
    [EnableRateLimiting(Startup.RateLimitEscrita)]
    [CmsLimiteCorpo]
    [RequestSizeLimit(CmsLimites.CorpoMultipart)]
    [RequestFormLimits(MultipartBodyLengthLimit = CmsLimites.CorpoMultipart)]
    public async Task<IActionResult> Criar([FromForm] BannerForm form, CancellationToken ct)
    {
        if (!CmsAutor.TentarLer(User, D.Tenant, out var autor))
            return SemAutor();
        if (!TentarTexto(form.Title, "o título", TamanhoMaximoTexto, out var titulo, out var erro))
            return erro;
        if (!TentarTipo(form.TipoDestino, CmsTipoDestino.Link, out var tipo, out erro))
            return erro;
        if (!CmsArquivos.TentarImagem(form.Imagem, CmsLimites.Banner, aceitaSvg: false, out var imagem, out var motivo))
            return Erro(StatusCodes.Status400BadRequest, motivo);

        var linha = new Dictionary<string, object>
        {
            ["title"] = titulo,
            ["tipo_destino"] = tipo,
            ["is_active"] = form.Ativo ?? false,
            ["display_order"] = form.DisplayOrder,
        };
        var arquivos = new List<(string, string, CmsArquivo)> { ("image_url", CmsSupabase.BucketAssets, imagem) };
        IReadOnlyList<string> avisos = Array.Empty<string>();

        if (tipo == CmsTipoDestino.Link)
        {
            if (!DestinoValido(form.Destino))
                return DestinoInvalido();
            if (form.Html != null)
                return HtmlEmBannerLink();

            linha["destino"] = form.Destino.Trim();
            linha["html_path"] = null;
        }
        else
        {
            if (form.Html == null)
                return Erro(StatusCodes.Status400BadRequest, "Banner do tipo HTML precisa do arquivo .html do hotsite.");
            if (!CmsArquivos.TentarHtml(form.Html, out var html, out motivo))
                return Erro(StatusCodes.Status400BadRequest, motivo);

            linha["destino"] = null;
            arquivos.Add(("html_path", CmsSupabase.BucketHtml, html));
            avisos = html.Avisos;
        }

        return await CriarAsync(autor, linha, arquivos, avisos, ct);
    }

    [HttpPut("{id:guid}")]
    [EnableRateLimiting(Startup.RateLimitEscrita)]
    [CmsLimiteCorpo]
    [RequestSizeLimit(CmsLimites.CorpoMultipart)]
    [RequestFormLimits(MultipartBodyLengthLimit = CmsLimites.CorpoMultipart)]
    public async Task<IActionResult> Atualizar(Guid id, [FromForm] BannerForm form, CancellationToken ct)
    {
        if (!CmsAutor.TentarLer(User, D.Tenant, out var autor))
            return SemAutor();
        if (!TentarTexto(form.Title, "o título", TamanhoMaximoTexto, out var titulo, out var erro))
            return erro;

        var atual = await BuscarAsync(id, ct);
        if (atual == null)
            return NaoEncontrado();

        var tipoAtual = string.IsNullOrEmpty(atual.TipoDestino) ? CmsTipoDestino.Link : atual.TipoDestino;
        if (!TentarTipo(form.TipoDestino, tipoAtual, out var tipo, out erro))
            return erro;

        var alteracoes = new Dictionary<string, object> { ["title"] = titulo, ["tipo_destino"] = tipo };
        if (form.DisplayOrder.HasValue) alteracoes["display_order"] = form.DisplayOrder.Value;
        if (form.Ativo.HasValue) alteracoes["is_active"] = form.Ativo.Value;

        var trocas = new List<CmsTroca>();
        IReadOnlyList<string> avisos = Array.Empty<string>();

        if (form.Imagem != null)
        {
            if (!CmsArquivos.TentarImagem(form.Imagem, CmsLimites.Banner, aceitaSvg: false, out var imagem, out var motivo))
                return Erro(StatusCodes.Status400BadRequest, motivo);
            trocas.Add(new CmsTroca("image_url", CmsSupabase.BucketAssets, Tabela, imagem));
        }

        if (tipo == CmsTipoDestino.Link)
        {
            if (!DestinoValido(form.Destino))
                return DestinoInvalido();
            if (form.Html != null)
                return HtmlEmBannerLink();

            alteracoes["destino"] = form.Destino.Trim();

            // Deixou de ser html: o hotsite sai da linha e entra na regra de
            // remoção do arquivo antigo.
            if (atual.HtmlPath != null)
                trocas.Add(new CmsTroca("html_path", CmsSupabase.BucketHtml, Tabela, null));
        }
        else
        {
            alteracoes["destino"] = null;

            if (form.Html != null)
            {
                if (!CmsArquivos.TentarHtml(form.Html, out var html, out var motivo))
                    return Erro(StatusCodes.Status400BadRequest, motivo);
                trocas.Add(new CmsTroca("html_path", CmsSupabase.BucketHtml, Tabela, html));
                avisos = html.Avisos;
            }
            else if (atual.HtmlPath == null)
            {
                return Erro(StatusCodes.Status400BadRequest, "Banner do tipo HTML precisa do arquivo .html do hotsite.");
            }
        }

        return await AtualizarAsync(id, autor, atual, alteracoes, trocas, avisos, ct);
    }

    private bool TentarTipo(string informado, string padrao, out string tipo, out IActionResult erro)
    {
        tipo = string.IsNullOrWhiteSpace(informado) ? padrao : informado.Trim().ToLowerInvariant();
        erro = tipo is CmsTipoDestino.Link or CmsTipoDestino.Html
            ? null
            : Erro(StatusCodes.Status400BadRequest, "Tipo de destino inválido. Use link ou html.");
        return erro == null;
    }

    private IActionResult DestinoInvalido() =>
        Erro(StatusCodes.Status400BadRequest,
            "O destino precisa ser um endereço https:// completo ou uma âncora da página, como #contato.");

    private IActionResult HtmlEmBannerLink() =>
        Erro(StatusCodes.Status400BadRequest, "Arquivo .html só vale para banner do tipo HTML.");
}
