using System.IO;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Veiculando.WhiteLabel.Api.Services;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests;

public class FileValidationTests
{
    [Fact]
    public void Mime_precisa_corresponder_ao_conteudo()
    {
        using var stream = new MemoryStream(new byte[] { 255, 216, 255, 224, 0, 16, 255, 217 });
        var file = new FormFile(stream, 0, stream.Length, "foto", "foto.png") { Headers = new HeaderDictionary(), ContentType = "image/png" };
        new FileValidationService().IsValidFile(file, 1024, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(0, 1024)]
    [InlineData(8, 7)]
    public void Recusa_vazio_ou_acima_do_limite(int tamanho, int limite)
    {
        using var stream = new MemoryStream(new byte[tamanho]);
        var file = new FormFile(stream, 0, tamanho, "foto", "foto.jpg");
        new FileValidationService().IsValidFile(file, limite, out _).Should().BeFalse();
    }

    [Fact]
    public void Nome_hostil_nao_determina_caminho_no_storage()
    {
        var nome = new FileValidationService().SanitizeFileName("../../arquivo.jpg");
        nome.Should().MatchRegex("^[a-f0-9]{32}\\.jpg$");
    }
}
