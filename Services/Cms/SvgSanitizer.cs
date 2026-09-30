using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Veiculando.WhiteLabel.Api.Services.Cms;

/// <summary>
/// Aceita ou recusa um SVG de logo (só Marcas). Não reescreve o arquivo: o que
/// passa é gravado como veio.
/// </summary>
/// <remarks>
/// <para><b>Por que recusar e não limpar.</b> Um sanitizador que remove partes
/// perigosas precisa acertar todos os casos para não deixar passar nada; um que
/// recusa só precisa detectá-los, e o erro ao operador diz o que tirar do arquivo.
/// Logo de marca parceira é arquivo exportado de ferramenta de design, que não
/// traz script: recusar custa pouco.</para>
///
/// <para><b>Por que importa, se a LP usa &lt;img&gt;.</b> Dentro de &lt;img&gt; o SVG
/// não executa script. Mas o objeto fica num bucket público, e quem abrir a URL
/// direto no navegador renderiza o SVG como documento, com script.</para>
///
/// <para>O XML é lido com DTD proibido e sem resolver: entidade externa (XXE) e
/// expansão de entidade ("billion laughs") nem chegam a ser processadas.</para>
/// </remarks>
public static class SvgSanitizer
{
    private static readonly string[] ElementosProibidos =
        { "script", "foreignobject", "iframe", "embed", "object" };

    private static readonly string[] ElementosDeAnimacao =
        { "animate", "set", "animatemotion", "animatetransform" };

    // Imagem raster embutida é comum em logo exportado e não executa nada.
    private static readonly Regex DataUriDeImagem =
        new(@"^data:image/(png|jpe?g|gif|webp);", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UrlEmCss =
        new(@"url\(\s*['""]?\s*([^'"")\s]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool TentarValidar(byte[] conteudo, out string erro)
    {
        string texto;
        try
        {
            texto = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(conteudo);
        }
        catch (DecoderFallbackException)
        {
            erro = "o arquivo não é texto UTF-8";
            return false;
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };

        try
        {
            using var reader = XmlReader.Create(new StringReader(texto.TrimStart('﻿')), settings);
            var raiz = true;
            var dentroDeStyle = false;

            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName.Equals("style", StringComparison.OrdinalIgnoreCase))
                {
                    dentroDeStyle = false;
                }
                else if (reader.NodeType == XmlNodeType.Element)
                {
                    var nome = reader.LocalName.ToLowerInvariant();
                    if (nome == "style" && !reader.IsEmptyElement)
                        dentroDeStyle = true;

                    if (raiz && nome != "svg")
                    {
                        erro = "o arquivo não é um SVG";
                        return false;
                    }
                    raiz = false;

                    if (ElementosProibidos.Contains(nome))
                    {
                        erro = $"contém o elemento <{reader.LocalName}>";
                        return false;
                    }

                    if (!AtributosSeguros(reader, nome, out erro))
                        return false;
                }
                else if (dentroDeStyle
                         && reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA
                         && !CssSeguro(reader.Value, out erro))
                {
                    // Conteúdo de <style>: @import e url() externos puxam recurso de fora.
                    return false;
                }
            }

            if (raiz)
            {
                erro = "o arquivo não é um SVG";
                return false;
            }
        }
        catch (XmlException)
        {
            erro = "o SVG não é um XML válido ou declara DTD";
            return false;
        }

        erro = null;
        return true;
    }

    private static bool AtributosSeguros(XmlReader reader, string elemento, out string erro)
    {
        if (reader.MoveToFirstAttribute())
        {
            do
            {
                var nome = reader.LocalName.ToLowerInvariant();
                var valor = reader.Value ?? string.Empty;
                var compacto = Compactar(valor);

                if (nome.StartsWith("on", StringComparison.Ordinal) && reader.Prefix != "xmlns")
                {
                    erro = $"contém o atributo de evento {reader.Name}";
                    return false;
                }

                if (compacto.Contains("javascript:") || compacto.Contains("vbscript:") || compacto.Contains("data:text/html"))
                {
                    erro = $"contém script no atributo {reader.Name}";
                    return false;
                }

                if (nome == "href" && !ReferenciaInterna(valor))
                {
                    erro = $"aponta para um endereço externo em {reader.Name}";
                    return false;
                }

                // <set attributeName="href" to="https://..."> troca o href depois de carregado.
                if (nome == "attributename" && ElementosDeAnimacao.Contains(elemento)
                    && (compacto.EndsWith("href") || compacto.StartsWith("on")))
                {
                    erro = $"anima o atributo {valor}";
                    return false;
                }

                if (nome == "style" && !CssSeguro(valor, out erro))
                    return false;
            }
            while (reader.MoveToNextAttribute());

            reader.MoveToElement();
        }

        erro = null;
        return true;
    }

    private static bool CssSeguro(string css, out string erro)
    {
        if (string.IsNullOrEmpty(css))
        {
            erro = null;
            return true;
        }

        if (Compactar(css).Contains("@import"))
        {
            erro = "importa CSS externo (@import)";
            return false;
        }

        foreach (Match m in UrlEmCss.Matches(css))
        {
            if (!ReferenciaInterna(m.Groups[1].Value))
            {
                erro = "referencia um endereço externo em url()";
                return false;
            }
        }

        erro = null;
        return true;
    }

    private static bool ReferenciaInterna(string valor)
    {
        var v = valor.Trim();
        return v.Length == 0 || v.StartsWith('#') || DataUriDeImagem.IsMatch(v);
    }

    // "java\tscript:" e "JavaScript:" são o mesmo esquema para o navegador.
    private static string Compactar(string valor) =>
        new string(valor.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)).ToArray()).ToLowerInvariant();
}
