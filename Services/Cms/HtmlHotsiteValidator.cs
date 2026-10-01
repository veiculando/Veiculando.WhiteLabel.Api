using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Veiculando.WhiteLabel.Api.Services.Cms;

/// <summary>
/// Valida o HTML de hotsite de banner e aponta endereços relativos.
/// </summary>
/// <remarks>
/// <para>A LP serve o arquivo em <c>/ofertas/{id}</c>, com CSP <c>sandbox</c> e sem
/// <c>allow-same-origin</c> (ADR-CMS-003). É o sandbox que isola o script do
/// hotsite; este validador não tenta decidir se o HTML é seguro.</para>
///
/// <para><b>Por que avisar, e não bloquear, URL relativa.</b> Servido em
/// <c>/ofertas/{id}</c>, um <c>src="img/a.png"</c> vira <c>/ofertas/img/a.png</c>, que
/// não existe: a imagem quebra. O PRD exige hotsite autocontido ou com URL
/// absoluta, mas o operador pode estar subindo uma versão provisória. O aviso
/// volta em <c>avisos[]</c> e a gravação segue.</para>
/// </remarks>
public static class HtmlHotsiteValidator
{
    private const int MaximoDeAvisos = 10;

    private static readonly Regex Atributo = new(
        @"\b(src|href|poster|action)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s""'>]+))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UrlEmCss = new(
        @"url\(\s*['""]?\s*([^'"")\s]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Esquemas que não dependem de onde o arquivo é servido.
    private static readonly string[] PrefixosIndependentes =
        { "#", "//", "data:", "mailto:", "tel:", "javascript:", "about:", "blob:" };

    public static bool TentarValidar(byte[] conteudo, out IReadOnlyList<string> avisos, out string erro)
    {
        avisos = Array.Empty<string>();

        string texto;
        try
        {
            texto = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(conteudo);
        }
        catch (DecoderFallbackException)
        {
            erro = "O hotsite precisa estar em UTF-8. Salve o arquivo com essa codificação e envie de novo.";
            return false;
        }

        // Byte nulo não aparece em HTML de verdade; indica binário renomeado.
        if (texto.Contains('\0'))
        {
            erro = "O arquivo enviado não é um HTML.";
            return false;
        }

        var relativos = new List<(string Atributo, string Valor)>();

        foreach (Match m in Atributo.Matches(texto))
        {
            var valor = m.Groups[2].Success ? m.Groups[2].Value
                : m.Groups[3].Success ? m.Groups[3].Value
                : m.Groups[4].Value;

            if (EhRelativo(valor))
                relativos.Add((m.Groups[1].Value.ToLowerInvariant(), valor.Trim()));
        }

        foreach (Match m in UrlEmCss.Matches(texto))
        {
            if (EhRelativo(m.Groups[1].Value))
                relativos.Add(("url()", m.Groups[1].Value.Trim()));
        }

        var distintos = relativos.Distinct().ToList();
        var lista = distintos
            .Take(MaximoDeAvisos)
            .Select(r => $"O HTML usa um endereço relativo em {r.Atributo}: {r.Valor}. " +
                         "O hotsite precisa ser autocontido ou usar URLs absolutas.")
            .ToList();

        if (distintos.Count > MaximoDeAvisos)
            lista.Add($"E mais {distintos.Count - MaximoDeAvisos} endereços relativos.");

        avisos = lista;
        erro = null;
        return true;
    }

    private static bool EhRelativo(string valor)
    {
        var v = valor?.Trim();
        if (string.IsNullOrEmpty(v))
            return false;

        if (PrefixosIndependentes.Any(p => v.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return false;

        return !(Uri.TryCreate(v, UriKind.Absolute, out var uri)
                 && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp));
    }
}
