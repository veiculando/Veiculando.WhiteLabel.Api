using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Veiculando.WhiteLabel.Api.Services;

public sealed record CampoImagem(Stream Conteudo, string ContentType);

/// <summary>Arquivos legados do Core, servidos pelo FileServer em <c>conteudo/</c>.</summary>
/// <remarks>Como no PDF de PI, o FileServer não autentica: a visibilidade é
/// conferida no BFF antes da chamada e a URL dele nunca chega ao app.</remarks>
public interface IWlArquivoLegado
{
    /// <returns>A imagem, ou <c>null</c> se o FileServer não a conhece.</returns>
    Task<CampoImagem> AbrirAsync(string caminho, CancellationToken ct);
}

public sealed class FileServerArquivoLegado : IWlArquivoLegado
{
    private readonly HttpClient _http;
    public FileServerArquivoLegado(HttpClient http) => _http = http;

    public async Task<CampoImagem> AbrirAsync(string caminho, CancellationToken ct)
    {
        var rota = "conteudo/" + string.Join("/", caminho.Split('/').Select(Uri.EscapeDataString));
        var resposta = await _http.GetAsync(rota, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resposta.StatusCode == HttpStatusCode.NotFound)
        {
            resposta.Dispose();
            return null;
        }
        resposta.EnsureSuccessStatusCode();
        // Tipo pela extensão validada, não pelo cabeçalho de um serviço sem autenticação.
        return new CampoImagem(await resposta.Content.ReadAsStreamAsync(ct), CampoImagemCaminho.ContentType(caminho));
    }
}

/// <summary>De onde sai cada imagem de campo; null = não servível.</summary>
public static class CampoImagemCaminho
{
    private static readonly Regex Legado = new(@"^(checking|arte-final)/[A-Za-z0-9_\-./]+\.(jpe?g|png)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static string ContentType(string caminho) =>
        caminho.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";

    /// <summary>Chave do storage WL (prefixo <c>wl-upload:</c>) ou caminho legado em <c>conteudo/checking/</c>.</summary>
    public static (string ChaveWl, string Legado)? Checking(string urlArquivo)
    {
        if (string.IsNullOrEmpty(urlArquivo)) return null;
        if (urlArquivo.StartsWith(WlUploadReferences.Prefix, StringComparison.Ordinal))
            return (urlArquivo.Substring(WlUploadReferences.Prefix.Length), null);
        var i = urlArquivo.IndexOf("/conteudo/", StringComparison.Ordinal);
        var caminho = i < 0 ? null : urlArquivo.Substring(i + "/conteudo/".Length);
        return Valido(caminho, "checking/") ? (null, caminho) : null;
    }

    /// <summary>A prévia do Core é sempre "temp.jpg"; serve o arquivo de alta quando é imagem.</summary>
    public static string Arte(string arquivoAlta) => Valido(arquivoAlta, "arte-final/") ? arquivoAlta : null;

    private static bool Valido(string caminho, string raiz) =>
        caminho != null && caminho.StartsWith(raiz, StringComparison.Ordinal) && Legado.IsMatch(caminho)
        && !caminho.Contains("..") && !caminho.Contains("//");
}
