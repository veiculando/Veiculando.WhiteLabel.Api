using System.IO;
using FluentAssertions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using Veiculando.WhiteLabel.Api.Services;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests;

// A foto da peça fica pública no FileServer: a de celular não pode levar GPS junto.
public class FotoPecaImagemTests
{
    private static byte[] JpgDeCelular(int largura = 4, int altura = 2)
    {
        using var imagem = new Image<Rgb24>(largura, altura, new Rgb24(0, 0, 255));
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Orientation, (ushort)6); // girar 90° no sentido horário
        exif.SetValue(ExifTag.GPSLatitudeRef, "S");
        exif.SetValue(ExifTag.Model, "Celular de teste");
        imagem.Metadata.ExifProfile = exif;
        using var saida = new MemoryStream();
        imagem.SaveAsJpeg(saida);
        return saida.ToArray();
    }

    [Fact]
    public void JpgSaiSemExifEComAOrientacaoAplicada()
    {
        var preparada = FotoPecaImagem.Preparar(JpgDeCelular());

        preparada.Should().NotBeNull();
        preparada.ContentTypeOriginal.Should().Be("image/jpeg");
        preparada.GuardarOriginal.Should().BeFalse("só metadados saíram");
        using var gravada = Image.Load<Rgb24>(preparada.Jpg);
        gravada.Metadata.ExifProfile.Should().BeNull();
        gravada.Metadata.XmpProfile.Should().BeNull();
        gravada.Metadata.IptcProfile.Should().BeNull();
        // 4x2 com orientação 6 vira 2x4 nos pixels.
        gravada.Width.Should().Be(2);
        gravada.Height.Should().Be(4);
    }

    [Fact]
    public void FotoGrandeTemOLadoMaiorLimitado()
    {
        var preparada = FotoPecaImagem.Preparar(JpgDeCelular(FotoPecaImagem.LadoMaximo * 2, 1000));

        preparada.GuardarOriginal.Should().BeTrue("a resolução cheia só fica no original");
        using var gravada = Image.Load<Rgb24>(preparada.Jpg);
        // Orientação aplicada antes: o lado maior agora é a altura.
        gravada.Height.Should().Be(FotoPecaImagem.LadoMaximo);
        gravada.Width.Should().Be(500);
    }

    [Fact]
    public void PngViraJpg()
    {
        using var png = new Image<Rgba32>(3, 3, new Rgba32(0, 0, 0, 0));
        using var saida = new MemoryStream();
        png.SaveAsPng(saida);

        var preparada = FotoPecaImagem.Preparar(saida.ToArray());

        preparada.ContentTypeOriginal.Should().Be("image/png");
        preparada.GuardarOriginal.Should().BeTrue();
        Image.Identify(preparada.Jpg, out var formato).Should().NotBeNull();
        formato.Should().BeOfType<JpegFormat>();
    }

    [Fact]
    public void MesmaFotoGeraOsMesmosBytes()
    {
        // A restauração do UploadsController compara bytes com o que foi enviado.
        var original = JpgDeCelular();
        FotoPecaImagem.Preparar(original).Jpg.Should().Equal(FotoPecaImagem.Preparar(original).Jpg);
    }

    [Fact]
    public void ArquivoQueNaoEImagemERecusado()
    {
        FotoPecaImagem.Preparar(new byte[] { 0xFF, 0xD8, 0xFF, 0x00, 0x01 }).Should().BeNull();
    }
}
