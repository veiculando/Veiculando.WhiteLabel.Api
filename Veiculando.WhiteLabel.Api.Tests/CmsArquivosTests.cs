using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Veiculando.WhiteLabel.Api.Services;
using Veiculando.WhiteLabel.Api.Services.Cms;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests;

/// <summary>
/// Validação de arquivos do CMS (VEI-RD-19d). O 413 do corpo acima de 8 MB é
/// do Kestrel e é testado nos controllers (19e).
/// </summary>
public class CmsArquivosTests
{
    private static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 1, 0xFF, 0xD9 };
    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D };
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n%âãÏÓ\n1 0 obj");
    private static readonly byte[] WebP = Riff("WEBP");

    // ---------- imagem ----------

    [Theory]
    [InlineData("jpeg", "image/jpeg", "jpg")]
    [InlineData("png", "image/png", "png")]
    [InlineData("webp", "image/webp", "webp")]
    public void Imagem_raster_e_aceita_pelo_conteudo(string tipo, string contentType, string extensao)
    {
        var bytes = tipo switch { "jpeg" => Jpeg, "png" => Png, _ => WebP };

        CmsArquivos.TentarImagem(Arquivo("qualquer.nome", bytes, "application/octet-stream"),
            CmsLimites.Banner, aceitaSvg: false, out var arquivo, out var erro).Should().BeTrue(erro);

        arquivo.ContentType.Should().Be(contentType, "o tipo sai dos bytes, não do que o cliente declarou");
        arquivo.Extensao.Should().Be(extensao);
        arquivo.Conteudo.Should().Equal(bytes);
    }

    [Fact]
    public void Png_que_e_jpeg_e_gravado_como_jpeg()
    {
        CmsArquivos.TentarImagem(Arquivo("foto.png", Jpeg, "image/png"), CmsLimites.Banner, false, out var arquivo, out _)
            .Should().BeTrue();

        arquivo.Extensao.Should().Be("jpg");
    }

    [Fact]
    public void Png_que_e_pdf_e_recusado()
    {
        CmsArquivos.TentarImagem(Arquivo("logo.png", Pdf, "image/png"), CmsLimites.Logo, aceitaSvg: true, out var arquivo, out var erro)
            .Should().BeFalse();

        arquivo.Should().BeNull();
        erro.Should().Contain("Tipo de arquivo não aceito");
    }

    [Theory]
    [InlineData("WAVE")]
    [InlineData("AVI ")]
    public void Riff_que_nao_e_webp_e_recusado(string formato)
    {
        CmsArquivos.TentarImagem(Arquivo("x.webp", Riff(formato), "image/webp"), CmsLimites.Banner, false, out _, out _)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(2 * 1024 * 1024 + 1, 2 * 1024 * 1024, false)]
    [InlineData(2 * 1024 * 1024, 2 * 1024 * 1024, true)]
    [InlineData(5 * 1024 * 1024 + 1, 5 * 1024 * 1024, false)]
    public void Limite_de_tamanho(int tamanho, long limite, bool aceito)
    {
        var bytes = new byte[tamanho];
        WebP.CopyTo(bytes, 0);

        CmsArquivos.TentarImagem(Arquivo("avatar.webp", bytes), limite, false, out _, out var erro).Should().Be(aceito);
        if (!aceito) erro.Should().Contain($"{limite / (1024 * 1024)} MB");
    }

    [Fact]
    public void Avatar_webp_de_3_mb_e_recusado()
    {
        var bytes = new byte[3 * 1024 * 1024];
        WebP.CopyTo(bytes, 0);

        CmsArquivos.TentarImagem(Arquivo("avatar.webp", bytes), CmsLimites.Avatar, false, out _, out var erro)
            .Should().BeFalse();
        erro.Should().Contain("2 MB");
    }

    [Fact]
    public void Arquivo_vazio_ou_ausente_e_recusado()
    {
        CmsArquivos.TentarImagem(null, CmsLimites.Banner, false, out _, out var erro).Should().BeFalse();
        erro.Should().Be("Envie um arquivo.");

        CmsArquivos.TentarImagem(Arquivo("x.png", Array.Empty<byte>()), CmsLimites.Banner, false, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Svg_em_banner_e_recusado()
    {
        CmsArquivos.TentarImagem(Arquivo("banner.svg", Utf8(SvgLimpo)), CmsLimites.Banner, aceitaSvg: false, out _, out var erro)
            .Should().BeFalse();
        erro.Should().StartWith("SVG não é aceito neste campo");
    }

    [Fact]
    public void Svg_limpo_em_marca_e_aceito()
    {
        CmsArquivos.TentarImagem(Arquivo("logo.svg", Utf8(SvgLimpo), "text/plain"), CmsLimites.Logo, aceitaSvg: true, out var arquivo, out var erro)
            .Should().BeTrue(erro);

        arquivo.ContentType.Should().Be("image/svg+xml");
        arquivo.Extensao.Should().Be("svg");
    }

    [Fact]
    public void Html_no_campo_de_imagem_e_recusado()
    {
        CmsArquivos.TentarImagem(Arquivo("foto.png", Utf8("<!DOCTYPE html><html><body></body></html>")), CmsLimites.Banner, true, out _, out var erro)
            .Should().BeFalse();
        erro.Should().StartWith("HTML não é aceito no campo de imagem");
    }

    [Fact]
    public void Upload_legado_continua_sem_webp()
    {
        // O WebP entrou só no CMS: foto de peça e documentos seguem com a lista antiga.
        new FileValidationService().IsValidFile(Arquivo("foto.webp", WebP, "image/webp"), 1024 * 1024, out _)
            .Should().BeFalse();
    }

    // ---------- SVG ----------

    private const string SvgLimpo =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
        "<!-- Exportado por um editor qualquer -->\n" +
        "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" viewBox=\"0 0 10 10\">" +
        "<defs><linearGradient id=\"g\"><stop offset=\"0\" stop-color=\"#fff\"/></linearGradient></defs>" +
        "<style>.a{fill:url(#g)}</style>" +
        "<rect class=\"a\" width=\"10\" height=\"10\" opacity=\"0.5\"/>" +
        "<use xlink:href=\"#g\"/>" +
        "<image href=\"data:image/png;base64,iVBORw0KGgo=\" width=\"1\" height=\"1\"/>" +
        "<text>url(qualquer) em texto não é CSS</text>" +
        "</svg>";

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>", "script")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><SCRIPT>alert(1)</SCRIPT></svg>", "SCRIPT")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"alert(1)\"/>", "onload")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><rect OnClick=\"x()\"/></svg>", "OnClick")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><foreignObject><div/></foreignObject></svg>", "foreignObject")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><a href=\"javascript:alert(1)\"><rect/></a></svg>", "script")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><a href=\"java&#9;script:alert(1)\"><rect/></a></svg>", "script")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><image href=\"https://rastreio.exemplo/p.png\"/></svg>", "externo")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\"><use xlink:href=\"https://exemplo/s.svg#a\"/></svg>", "externo")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><a><set attributeName=\"href\" to=\"https://exemplo\"/></a></svg>", "anima")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><style>@import url(https://exemplo/a.css);</style></svg>", "@import")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><rect style=\"fill:url(https://exemplo/x)\"/></svg>", "url()")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><image href=\"data:image/svg+xml;base64,PHN2Zz4=\"/></svg>", "externo")]
    [InlineData("<!DOCTYPE svg [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><svg xmlns=\"http://www.w3.org/2000/svg\">&x;</svg>", "DTD")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><rect></svg>", "XML")]
    public void Svg_inseguro_e_recusado(string svg, string trecho)
    {
        CmsArquivos.TentarImagem(Arquivo("logo.svg", Utf8(svg)), CmsLimites.Logo, aceitaSvg: true, out var arquivo, out var erro)
            .Should().BeFalse();

        arquivo.Should().BeNull();
        erro.Should().StartWith("SVG inseguro").And.Contain(trecho);
    }

    [Fact]
    public void Arquivo_com_svg_mas_raiz_html_nao_passa_por_svg()
    {
        SvgSanitizer.TentarValidar(Utf8("<html><body><svg/></body></html>"), out var erro).Should().BeFalse();
        erro.Should().Contain("não é um SVG");
    }

    // ---------- HTML de hotsite ----------

    [Fact]
    public void Html_com_url_relativa_gera_aviso_no_formato_da_fixture()
    {
        var html = "<!doctype html><html><body><img src='img/a.png'><a href=\"https://exemplo.com.br\">x</a></body></html>";

        CmsArquivos.TentarHtml(Arquivo("oferta.html", Utf8(html), "text/html"), out var arquivo, out var erro).Should().BeTrue(erro);

        arquivo.ContentType.Should().Be("text/html; charset=utf-8");
        arquivo.Extensao.Should().Be("html");

        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Contratos", "cms-banner-salvo-avisos.json")))!;
        arquivo.Avisos.Should().Equal(fixture["avisos"]!.AsArray().Select(a => a!.GetValue<string>()));
    }

    [Fact]
    public void Html_autocontido_nao_gera_aviso()
    {
        var html = "<html><head><style>body{background:url(data:image/png;base64,AAAA)}</style></head>" +
                   "<body><a href=\"#topo\">t</a><a href=\"mailto:a@exemplo.com\">m</a>" +
                   "<img src=\"https://cdn.exemplo.com/x.png\"><script src=\"//cdn.exemplo.com/a.js\"></script></body></html>";

        CmsArquivos.TentarHtml(Arquivo("oferta.html", Utf8(html)), out var arquivo, out _).Should().BeTrue();
        arquivo.Avisos.Should().BeEmpty();
    }

    [Fact]
    public void Html_aponta_todos_os_relativos_sem_repetir()
    {
        var html = "<img src=\"/a.png\"><img src=\"/a.png\"><link href=./estilo.css><div style=\"background:url('fundo.jpg')\"></div>";

        CmsArquivos.TentarHtml(Arquivo("oferta.html", Utf8(html)), out var arquivo, out _).Should().BeTrue();

        arquivo.Avisos.Should().HaveCount(3);
        arquivo.Avisos.Should().Contain(a => a.Contains("src: /a.png"));
        arquivo.Avisos.Should().Contain(a => a.Contains("href: ./estilo.css"));
        arquivo.Avisos.Should().Contain(a => a.Contains("url(): fundo.jpg"));
    }

    [Fact]
    public void Html_acima_de_2_mb_e_recusado()
    {
        var bytes = Utf8("<html>" + new string('a', 2 * 1024 * 1024) + "</html>");

        CmsArquivos.TentarHtml(Arquivo("oferta.html", bytes), out _, out var erro).Should().BeFalse();
        erro.Should().Contain("2 MB");
    }

    [Fact]
    public void Html_que_nao_e_utf8_e_recusado()
    {
        // "Promoção" em Latin-1: ç = 0xE7 e ã = 0xE3 não formam sequência UTF-8 válida.
        var latin1 = Encoding.Latin1.GetBytes("<html><body>Promoção</body></html>");

        CmsArquivos.TentarHtml(Arquivo("oferta.html", latin1), out _, out var erro).Should().BeFalse();
        erro.Should().Contain("UTF-8");
    }

    [Theory]
    [InlineData("oferta.htm")]
    [InlineData("oferta.txt")]
    [InlineData("oferta.html.png")]
    public void Hotsite_precisa_de_extensao_html(string nome)
    {
        CmsArquivos.TentarHtml(Arquivo(nome, Utf8("<html></html>")), out _, out var erro).Should().BeFalse();
        erro.Should().Contain(".html");
    }

    [Fact]
    public void Binario_renomeado_para_html_e_recusado()
    {
        CmsArquivos.TentarHtml(Arquivo("oferta.html", new byte[] { 0x3C, 0x68, 0x00, 0x01 }), out _, out var erro).Should().BeFalse();
        erro.Should().Contain("não é um HTML");
    }

    // ---------- nome do objeto ----------

    [Fact]
    public void Nome_do_objeto_ignora_o_nome_original()
    {
        CmsArquivos.NomeObjeto("banners", "png").Should().MatchRegex("^banners/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\\.png$");
        CmsArquivos.NomeObjeto("banners", "png").Should().NotBe(CmsArquivos.NomeObjeto("banners", "png"));
    }

    private static IFormFile Arquivo(string nome, byte[] bytes, string contentType = "application/octet-stream") =>
        new FormFile(new MemoryStream(bytes), 0, bytes.Length, "arquivo", nome)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType,
        };

    private static byte[] Utf8(string texto) => Encoding.UTF8.GetBytes(texto);

    private static byte[] Riff(string formato) =>
        Encoding.ASCII.GetBytes("RIFF").Concat(new byte[] { 0x24, 0, 0, 0 }).Concat(Encoding.ASCII.GetBytes(formato))
            .Concat(new byte[] { 0x56, 0x50, 0x38, 0x20 }).ToArray();
}
