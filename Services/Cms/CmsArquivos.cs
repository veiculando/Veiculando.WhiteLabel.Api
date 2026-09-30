using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Veiculando.WhiteLabel.Api.Services.Cms;

/// <summary>Limites de tamanho do CMS (card VEI-RD-19, seção 4).</summary>
public static class CmsLimites
{
    private const long MB = 1024 * 1024;

    public const long Banner = 5 * MB;
    public const long Logo = 2 * MB;
    public const long Avatar = 2 * MB;
    public const long Html = 2 * MB;

    /// <summary>
    /// Teto do corpo multipart dos POST/PUT: imagem de 5 MB + HTML de 2 MB + campos.
    /// Acima disso o Kestrel devolve 413 antes do controller. O edge do preview
    /// aceita 16 MB, então o 413 é deste limite e não do nginx.
    /// </summary>
    public const long CorpoMultipart = 8 * MB;
}

/// <summary>Arquivo aprovado, pronto para o Storage.</summary>
public sealed class CmsArquivo
{
    /// <summary>Os bytes validados. É isto que sobe, não o stream do form, relido depois.</summary>
    public byte[] Conteudo { get; init; }

    /// <summary>Content-Type decidido pelo conteúdo, nunca o declarado pelo cliente.</summary>
    public string ContentType { get; init; }

    /// <summary>Extensão sem ponto, também decidida pelo conteúdo.</summary>
    public string Extensao { get; init; }

    /// <summary>Avisos que não bloqueiam (hoje, só URL relativa no HTML).</summary>
    public IReadOnlyList<string> Avisos { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Validação dos arquivos do CMS: imagem (banner, logo, avatar) e HTML de hotsite.
/// </summary>
/// <remarks>
/// <para><b>O tipo sai dos bytes.</b> Nome, extensão e Content-Type vêm do cliente
/// e são ignorados: um <c>.png</c> que é JPEG é gravado como <c>.jpg</c>, e um
/// <c>.png</c> que é PDF é recusado.</para>
///
/// <para><b>Lido para a memória uma vez.</b> O que foi validado é exatamente o que
/// sobe: reabrir o stream do form para gravar abriria espaço para o conteúdo
/// gravado não ser o conteúdo checado. Os limites (no máximo 5 MB) tornam isso
/// barato.</para>
/// </remarks>
public static class CmsArquivos
{
    public static bool TentarImagem(IFormFile arquivo, long limite, bool aceitaSvg, out CmsArquivo resultado, out string erro)
    {
        resultado = null;
        if (!TentarLer(arquivo, limite, out var bytes, out erro))
            return false;

        var raster = FileValidationService.DetectarImagem(bytes.AsSpan(0, Math.Min(bytes.Length, FileValidationService.CabecalhoImagem)));
        if (raster != null)
        {
            resultado = new CmsArquivo { Conteudo = bytes, ContentType = raster.Value.ContentType, Extensao = raster.Value.Extensao };
            return true;
        }

        var tiposAceitos = aceitaSvg ? "PNG, JPG, WebP ou SVG" : "PNG, JPG ou WebP";

        if (PareceMarcacao(bytes, "<svg"))
        {
            if (!aceitaSvg)
            {
                erro = $"SVG não é aceito neste campo. Envie uma imagem {tiposAceitos}.";
                return false;
            }

            if (!SvgSanitizer.TentarValidar(bytes, out var motivo))
            {
                erro = $"SVG inseguro: {motivo}. Remova esse trecho do arquivo e envie de novo.";
                return false;
            }

            resultado = new CmsArquivo { Conteudo = bytes, ContentType = "image/svg+xml", Extensao = "svg" };
            return true;
        }

        erro = PareceMarcacao(bytes, "<html") || PareceMarcacao(bytes, "<!doctype html")
            ? $"HTML não é aceito no campo de imagem. Envie uma imagem {tiposAceitos}."
            : $"Tipo de arquivo não aceito. Envie uma imagem {tiposAceitos}.";
        return false;
    }

    public static bool TentarHtml(IFormFile arquivo, out CmsArquivo resultado, out string erro)
    {
        resultado = null;

        var extensao = Path.GetExtension(arquivo?.FileName ?? string.Empty);
        if (arquivo != null && !string.Equals(extensao, ".html", StringComparison.OrdinalIgnoreCase))
        {
            erro = "O hotsite precisa ser um arquivo .html.";
            return false;
        }

        if (!TentarLer(arquivo, CmsLimites.Html, out var bytes, out erro))
            return false;

        if (!HtmlHotsiteValidator.TentarValidar(bytes, out var avisos, out erro))
            return false;

        resultado = new CmsArquivo
        {
            Conteudo = bytes,
            ContentType = "text/html; charset=utf-8",
            Extensao = "html",
            Avisos = avisos,
        };
        return true;
    }

    /// <summary>
    /// Nome do objeto no bucket: <c>{recurso}/{uuid}.{ext}</c>. O nome original do
    /// arquivo nunca entra, nem como parte.
    /// </summary>
    public static string NomeObjeto(string recurso, string extensao) =>
        $"{recurso}/{Guid.NewGuid():D}.{extensao}";

    private static bool TentarLer(IFormFile arquivo, long limite, out byte[] bytes, out string erro)
    {
        bytes = null;

        if (arquivo == null || arquivo.Length == 0)
        {
            erro = "Envie um arquivo.";
            return false;
        }

        // Antes de ler: o Length vem do multipart já recebido, então recusar aqui
        // não custa leitura nenhuma.
        if (arquivo.Length > limite)
        {
            erro = $"O arquivo passa do limite de {limite / (1024 * 1024)} MB.";
            return false;
        }

        using var memoria = new MemoryStream((int)arquivo.Length);
        using (var stream = arquivo.OpenReadStream())
            stream.CopyTo(memoria);

        if (memoria.Length > limite)
        {
            erro = $"O arquivo passa do limite de {limite / (1024 * 1024)} MB.";
            return false;
        }

        bytes = memoria.ToArray();
        erro = null;
        return true;
    }

    /// <summary>
    /// O texto começa com a marcação (depois de BOM, espaço, declaração XML ou
    /// comentários)? Olha só o início, o suficiente para classificar a mensagem de
    /// erro; a validação de verdade do SVG é o <see cref="SvgSanitizer"/>.
    /// </summary>
    private static bool PareceMarcacao(byte[] bytes, string marcador)
    {
        // 4 KB: logos exportados trazem declaração XML e comentário do editor antes da tag.
        var inicio = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 4096)).ToLowerInvariant();
        return inicio.Contains(marcador, StringComparison.Ordinal);
    }
}
