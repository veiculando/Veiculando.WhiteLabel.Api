using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Veiculando.WhiteLabel.Api.Services.Cms;

/// <summary>Uma página do PostgREST com o total lido do <c>Content-Range</c>.</summary>
public sealed class CmsPaginaBruta<T>
{
    public IReadOnlyList<T> Itens { get; init; }
    public int Total { get; init; }
}

/// <summary>
/// Acesso ao Supabase do CMS: PostgREST (<c>/rest/v1</c>), RPC e Storage.
/// </summary>
/// <remarks>
/// Os filtros chegam prontos e já escapados (<c>id=eq.{uuid}</c>,
/// <c>or=(...)</c>): quem monta filtro a partir de entrada do usuário é o
/// controller, e o escape é responsabilidade dele. O client só transporta.
/// </remarks>
public interface ISupabaseCmsClient
{
    /// <summary>GET paginado, com <c>Prefer: count=exact</c>.</summary>
    Task<CmsPaginaBruta<T>> ListarAsync<T>(string tabela, string filtros, int limit, int offset, CancellationToken ct = default);

    /// <summary>GET simples. Id inexistente volta como lista vazia, não como erro.</summary>
    Task<IReadOnlyList<T>> SelecionarAsync<T>(string tabela, string filtros, CancellationToken ct = default);

    /// <summary>POST de uma linha, devolvendo o que foi gravado.</summary>
    Task<T> InserirAsync<T>(string tabela, object linha, CancellationToken ct = default);

    /// <summary>PATCH das linhas do filtro, devolvendo as linhas alteradas (vazio se nenhuma casou).</summary>
    Task<IReadOnlyList<T>> AtualizarAsync<T>(string tabela, string filtros, object alteracoes, CancellationToken ct = default);

    /// <summary>POST em <c>/rest/v1/rpc/{funcao}</c>, sem argumentos.</summary>
    Task<T> RpcAsync<T>(string funcao, CancellationToken ct = default);

    /// <summary>Grava um objeto novo no Storage, com <c>x-upsert: false</c>.</summary>
    Task EnviarObjetoAsync(string bucket, string caminho, Stream conteudo, string contentType, CancellationToken ct = default);

    /// <summary>Remove um objeto. Devolve false se ele já não existia.</summary>
    Task<bool> RemoverObjetoAsync(string bucket, string caminho, CancellationToken ct = default);

    /// <summary>URL pública de um objeto de bucket público, como a LP usa no &lt;img&gt;.</summary>
    string UrlPublica(string bucket, string caminho);
}

/// <summary>
/// Client HTTP do Supabase, sem o pacote <c>supabase-csharp</c>: a superfície
/// usada é pequena e não justifica uma dependência comunitária.
/// </summary>
/// <remarks>
/// <para><b>Autenticação por formato de chave.</b> A chave nova
/// (<c>sb_secret_…</c>) não é JWT: vai só no header <c>apikey</c>, e o gateway do
/// Supabase emite o token interno. Mandá-la também em <c>Authorization: Bearer</c>
/// faz o PostgREST tentar validá-la como JWT e recusar. A chave legada
/// (<c>eyJ…</c>) é um JWT e vai nos dois headers. O fake dos testes não detecta
/// erro aqui; a validação real é o smoke no preview.</para>
///
/// <para><b>Falhas.</b> Timeout, erro de rede e qualquer status inesperado viram
/// <see cref="CmsIndisponivelException"/>, que o <see cref="CmsIndisponivelFiltro"/>
/// transforma em 503 legível. A mensagem da exceção nunca contém a chave nem o
/// corpo da resposta; o corpo vai só para o log, truncado.</para>
/// </remarks>
public sealed class SupabaseCmsClient : ISupabaseCmsClient
{
    /// <summary>Timeout de cada chamada ao Supabase. Aplicado no registro do HttpClient.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>Snake_case: o PostgREST devolve e aceita as colunas como estão no banco.</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly Regex TotalDoContentRange = new(@"/(\d+)\s*$", RegexOptions.Compiled);
    private const int LimiteCorpoNoLog = 500;

    private readonly HttpClient _http;
    private readonly CmsConfiguracao _config;
    private readonly ILogger<SupabaseCmsClient> _logger;

    public SupabaseCmsClient(HttpClient http, CmsConfiguracao config, ILogger<SupabaseCmsClient> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
    }

    public async Task<CmsPaginaBruta<T>> ListarAsync<T>(string tabela, string filtros, int limit, int offset, CancellationToken ct = default)
    {
        // limit/offset na query, e não o header Range: é o mesmo pedido, sem a
        // semântica de "range" que o cliente HTTP poderia reinterpretar.
        var query = Juntar(filtros, $"limit={limit}", $"offset={offset}");
        using var request = Criar(HttpMethod.Get, $"rest/v1/{tabela}?{query}");
        request.Headers.Add("Prefer", "count=exact");

        using var response = await EnviarAsync(request, ct, aceitar: HttpStatusCode.RequestedRangeNotSatisfiable);

        // Página além do fim: conforme a versão, o PostgREST devolve 200 com []
        // ou 416 (PGRST103). Nos dois casos o Content-Range traz o total
        // ("*/57"), e para a tela é só uma página vazia.
        var total = LerTotal(response);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            return new CmsPaginaBruta<T> { Itens = Array.Empty<T>(), Total = total ?? 0 };

        var itens = await LerAsync<List<T>>(response, ct);
        return new CmsPaginaBruta<T> { Itens = itens, Total = total ?? itens.Count };
    }

    public async Task<IReadOnlyList<T>> SelecionarAsync<T>(string tabela, string filtros, CancellationToken ct = default)
    {
        using var request = Criar(HttpMethod.Get, $"rest/v1/{tabela}?{filtros}");
        using var response = await EnviarAsync(request, ct);
        return await LerAsync<List<T>>(response, ct);
    }

    public async Task<T> InserirAsync<T>(string tabela, object linha, CancellationToken ct = default)
    {
        using var request = Criar(HttpMethod.Post, $"rest/v1/{tabela}");
        request.Headers.Add("Prefer", "return=representation");
        request.Content = CorpoJson(linha);

        using var response = await EnviarAsync(request, ct);
        var linhas = await LerAsync<List<T>>(response, ct);
        if (linhas.Count != 1)
            throw Indisponivel($"INSERT em {tabela} devolveu {linhas.Count} linhas; esperava 1.");

        return linhas[0];
    }

    public async Task<IReadOnlyList<T>> AtualizarAsync<T>(string tabela, string filtros, object alteracoes, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filtros))
            throw new ArgumentException("PATCH sem filtro alteraria a tabela inteira.", nameof(filtros));

        using var request = Criar(HttpMethod.Patch, $"rest/v1/{tabela}?{filtros}");
        request.Headers.Add("Prefer", "return=representation");
        request.Content = CorpoJson(alteracoes);

        using var response = await EnviarAsync(request, ct);
        return await LerAsync<List<T>>(response, ct);
    }

    public async Task<T> RpcAsync<T>(string funcao, CancellationToken ct = default)
    {
        using var request = Criar(HttpMethod.Post, $"rest/v1/rpc/{funcao}");
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        using var response = await EnviarAsync(request, ct);
        return await LerAsync<T>(response, ct);
    }

    public async Task EnviarObjetoAsync(string bucket, string caminho, Stream conteudo, string contentType, CancellationToken ct = default)
    {
        using var request = Criar(HttpMethod.Post, $"storage/v1/object/{bucket}/{caminho}");

        // Nunca sobrescrever: o nome é um uuid novo, e um conflito aqui indica
        // bug, não uma troca legítima de arquivo.
        request.Headers.Add("x-upsert", "false");
        request.Content = new StreamContent(conteudo);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        using var _ = await EnviarAsync(request, ct);
    }

    public async Task<bool> RemoverObjetoAsync(string bucket, string caminho, CancellationToken ct = default)
    {
        using var request = Criar(HttpMethod.Delete, $"storage/v1/object/{bucket}/{caminho}");

        // 404: o objeto já não existe. Para quem quer removê-lo, é sucesso.
        using var response = await EnviarAsync(request, ct, aceitar: HttpStatusCode.NotFound);
        return response.StatusCode != HttpStatusCode.NotFound;
    }

    public string UrlPublica(string bucket, string caminho) =>
        $"{_config.SupabaseUrl}/storage/v1/object/public/{bucket}/{caminho}";

    /// <summary>
    /// Total do <c>Content-Range</c> (<c>0-24/57</c> ou <c>*/57</c>). Null quando
    /// ausente ou sem contagem (<c>0-24/*</c>).
    /// </summary>
    public static int? LerTotal(HttpResponseMessage response)
    {
        if (!response.Content.Headers.TryGetValues("Content-Range", out var valores)
            && !response.Headers.TryGetValues("Content-Range", out valores))
            return null;

        foreach (var valor in valores)
        {
            var m = TotalDoContentRange.Match(valor);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var total))
                return total;
        }

        return null;
    }

    private HttpRequestMessage Criar(HttpMethod metodo, string caminho)
    {
        if (!_config.ChavesPresentes)
            throw Indisponivel("CMS sem Cms:SupabaseUrl ou Cms:ServiceRoleKey.");

        var request = new HttpRequestMessage(metodo, $"{_config.SupabaseUrl}/{caminho}");
        request.Headers.TryAddWithoutValidation("apikey", _config.ServiceRoleKey);

        if (EhChaveLegadaJwt(_config.ServiceRoleKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ServiceRoleKey);

        return request;
    }

    /// <summary>
    /// Chave legada (JWT, começa com <c>eyJ</c>) vai também no Bearer. Qualquer
    /// outro formato, a começar por <c>sb_secret_</c>, vai só no apikey.
    /// </summary>
    public static bool EhChaveLegadaJwt(string chave) =>
        chave != null && chave.StartsWith("eyJ", StringComparison.Ordinal);

    private async Task<HttpResponseMessage> EnviarAsync(HttpRequestMessage request, CancellationToken ct, HttpStatusCode? aceitar = null)
    {
        var alvo = $"{request.Method} {request.RequestUri?.AbsolutePath}";
        HttpResponseMessage response;

        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Quem cancelou foi o cliente da requisição, não o Supabase.
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning("Supabase CMS sem resposta em {Alvo}: {Tipo}", alvo, ex.GetType().Name);
            throw Indisponivel($"Supabase sem resposta em {alvo}.", ex);
        }

        if (response.IsSuccessStatusCode || response.StatusCode == aceitar)
            return response;

        using (response)
        {
            var corpo = await response.Content.ReadAsStringAsync(ct);
            if (corpo.Length > LimiteCorpoNoLog) corpo = corpo[..LimiteCorpoNoLog];

            _logger.LogWarning("Supabase CMS respondeu {Status} em {Alvo}: {Corpo}",
                (int)response.StatusCode, alvo, corpo);
            throw Indisponivel($"Supabase respondeu {(int)response.StatusCode} em {alvo}.");
        }
    }

    private static string Juntar(params string[] partes) =>
        string.Join("&", Array.FindAll(partes, p => !string.IsNullOrWhiteSpace(p)));

    private static StringContent CorpoJson(object valor) =>
        new(JsonSerializer.Serialize(valor, valor.GetType(), Json), Encoding.UTF8, "application/json");

    private static async Task<TResultado> LerAsync<TResultado>(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync<TResultado>(stream, Json, ct)
                   ?? throw Indisponivel("Supabase devolveu corpo vazio.");
        }
        catch (JsonException ex)
        {
            throw Indisponivel("Supabase devolveu JSON inesperado.", ex);
        }
    }

    private static CmsIndisponivelException Indisponivel(string detalhe, Exception inner = null) =>
        new(detalhe, inner);
}

/// <summary>
/// O Supabase do CMS não respondeu como esperado. Vira 503 no
/// <see cref="CmsIndisponivelFiltro"/>.
/// </summary>
/// <remarks>
/// A mensagem é diagnóstico interno (método, caminho, status) e vai para o log;
/// a resposta ao front é sempre a mensagem fixa do filtro. Nenhuma das duas
/// contém a chave.
/// </remarks>
public sealed class CmsIndisponivelException : Exception
{
    public CmsIndisponivelException(string message, Exception inner = null) : base(message, inner) { }
}
