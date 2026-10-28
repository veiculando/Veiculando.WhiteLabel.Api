using FluentAssertions;
using Veiculando.WhiteLabel.Api.Services;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests;

public class CampoImagemCaminhoTests
{
    [Fact]
    public void Checking_wl_usa_a_chave_do_storage_privado()
    {
        CampoImagemCaminho.Checking("wl-upload:tenant-1/checking/9/wl-abc.jpg")
            .Should().Be(("tenant-1/checking/9/wl-abc.jpg", (string)null));
    }

    [Fact]
    public void Checking_legado_usa_o_caminho_sob_conteudo()
    {
        CampoImagemCaminho.Checking("https://file.veiculando.com.br/conteudo/checking/cli/c1/foto_1.JPG")
            .Should().Be(((string)null, "checking/cli/c1/foto_1.JPG"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("checking/sem-host.jpg")]
    [InlineData("https://file/conteudo/checking/../segredo.jpg")]
    [InlineData("https://file/conteudo/checking//a.jpg")]
    [InlineData("https://file/conteudo/arte-final/a.jpg")]
    [InlineData("https://file/conteudo/checking/a.pdf")]
    [InlineData("https://file/conteudo/checking/a b.jpg")]
    public void Checking_fora_do_formato_nao_e_servivel(string url) =>
        CampoImagemCaminho.Checking(url).Should().BeNull();

    [Theory]
    [InlineData("arte-final/cli/camp/motivo-1_hi_1.png", "arte-final/cli/camp/motivo-1_hi_1.png")]
    [InlineData("arte-final/cli/camp/motivo-1_hi_1.pdf", null)]
    [InlineData("temp.jpg", null)]
    [InlineData("arte-final/../x.jpg", null)]
    [InlineData(null, null)]
    public void Arte_so_serve_imagem_em_arte_final(string arquivo, string esperado) =>
        CampoImagemCaminho.Arte(arquivo).Should().Be(esperado);

    [Fact]
    public void Tipo_vem_da_extensao_validada()
    {
        CampoImagemCaminho.ContentType("a.PNG").Should().Be("image/png");
        CampoImagemCaminho.ContentType("a.jpeg").Should().Be("image/jpeg");
    }
}
