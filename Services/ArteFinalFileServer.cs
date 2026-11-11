using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Veiculando.WhiteLabel.Api.Services;

/// <summary>Arquivo de arte final lido do FileServer, para o BFF devolver.</summary>
public sealed record ArteFinalArquivo(Stream Conteudo, string ContentType, long? Tamanho);

/// <summary>
/// Arquivos de arte final no FileServer do Core (VEI-RD-166a), em <c>conteudo/arte-final/...</c>.
/// </summary>
/// <remarks>
/// <para>Mesmo lugar e mesma rota (<c>upload/conteudo</c>) que o <c>ArteFinalHandler</c> do Core
/// usa: o layout enviado pelo App é o mesmo arquivo que o Veiculando.Web e a exibidora enxergam.</para>
/// <para>O FileServer não autentica; o recorte de tenant e de campanha fica no BFF, que só chega
/// aqui depois de achar o layout no escopo da sessão. O browser nunca recebe a URL do FileServer.</para>
/// </remarks>
public interface IArteFinalFileServer
{
    /// <summary>Grava e devolve o caminho relativo a <c>conteudo/</c>, como o FileServer salvou.</summary>
    /// <exception cref="ArteFinalUploadException">O FileServer recusou o arquivo.</exception>
    Task<string> EnviarAsync(string caminho, Stream conteudo, CancellationToken ct);

    /// <returns>O arquivo, ou <c>null</c> se o FileServer não o tem.</returns>
    Task<ArteFinalArquivo> ObterAsync(string caminho, CancellationToken ct);
}

public sealed class ArteFinalUploadException : Exception
{
    public ArteFinalUploadException(string message) : base(message) { }
}

public sealed class FileServerArteFinal : IArteFinalFileServer
{
    private const string Raiz = "conteudo/";
    private readonly HttpClient _http;

    public FileServerArteFinal(HttpClient http) => _http = http;

    public async Task<string> EnviarAsync(string caminho, Stream conteudo, CancellationToken ct)
    {
        using var arquivo = new StreamContent(conteudo);
        arquivo.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var form = new MultipartFormDataContent { { arquivo, "file", caminho } };
        using var resposta = await _http.PostAsync("upload/conteudo", form, ct);
        var corpo = await resposta.Content.ReadAsStringAsync(ct);
        if (!resposta.IsSuccessStatusCode)
            throw new ArteFinalUploadException(resposta.StatusCode == HttpStatusCode.BadRequest
                ? "O servidor de arquivos recusou o layout. Confira o tamanho (até 25 MB) e o nome do arquivo."
                : "Não foi possível enviar o layout. Tente novamente.");

        // O FileServer normaliza o nome; o que vai para o banco é o caminho que ele gravou.
        try
        {
            using var json = JsonDocument.Parse(corpo);
            var salvo = json.RootElement.GetProperty("filePath").GetString()?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(salvo))
                return salvo.StartsWith(Raiz, StringComparison.OrdinalIgnoreCase) ? salvo[Raiz.Length..] : salvo;
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
        catch (System.Collections.Generic.KeyNotFoundException) { }
        return caminho;
    }

    public async Task<ArteFinalArquivo> ObterAsync(string caminho, CancellationToken ct)
    {
        var rota = Raiz + string.Join('/', caminho.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));
        var resposta = await _http.GetAsync(rota, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resposta.StatusCode == HttpStatusCode.NotFound)
        {
            resposta.Dispose();
            return null;
        }
        resposta.EnsureSuccessStatusCode();
        return new ArteFinalArquivo(await resposta.Content.ReadAsStreamAsync(ct),
            resposta.Content.Headers.ContentType?.MediaType ?? "application/octet-stream",
            resposta.Content.Headers.ContentLength);
    }
}
