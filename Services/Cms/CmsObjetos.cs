using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Veiculando.WhiteLabel.Api.Services.Cms;

/// <summary>Um objeto do Storage do CMS: bucket + caminho dentro dele.</summary>
public sealed record CmsObjeto(string Bucket, string Caminho);

/// <summary>Resultado de <see cref="CmsObjetos.RemoverSeLivreAsync"/>.</summary>
public enum CmsRemocao
{
    /// <summary>Removido, ou já não existia.</summary>
    Removido,

    /// <summary>Alguma linha atual ainda aponta para o objeto; ele fica.</summary>
    AindaReferenciado,
}

/// <summary>
/// Converte colunas em objetos do Storage e remove objeto sem dono.
/// </summary>
public sealed class CmsObjetos
{
    private readonly ISupabaseCmsClient _supabase;
    private readonly CmsConfiguracao _config;

    public CmsObjetos(ISupabaseCmsClient supabase, CmsConfiguracao config)
    {
        _supabase = supabase;
        _config = config;
    }

    /// <summary>
    /// O objeto por trás do valor de uma coluna de arquivo. Null quando o valor
    /// não é um objeto deste Supabase (URL externa do seed, por exemplo).
    /// </summary>
    /// <remarks>
    /// <c>image_url</c> e <c>avatar_url</c> guardam a URL pública completa de
    /// <c>cms-assets</c>; <c>html_path</c> guarda o caminho relativo ao
    /// <c>cms-html</c>. Um valor que não casa com isso nunca é apagado: o BFF só
    /// remove o que consegue provar que está no bucket dele.
    /// </remarks>
    public CmsObjeto DaColuna(string coluna, string valor)
    {
        if (string.IsNullOrWhiteSpace(valor))
            return null;

        if (coluna == "html_path")
            return CaminhoSeguro(valor) ? new CmsObjeto(CmsSupabase.BucketHtml, valor) : null;

        var prefixo = _supabase.UrlPublica(CmsSupabase.BucketAssets, string.Empty);
        if (_config.SupabaseUrl == null || !valor.StartsWith(prefixo, StringComparison.Ordinal))
            return null;

        var caminho = valor[prefixo.Length..];
        var query = caminho.IndexOfAny(new[] { '?', '#' });
        if (query >= 0) caminho = caminho[..query];

        return CaminhoSeguro(caminho) ? new CmsObjeto(CmsSupabase.BucketAssets, caminho) : null;
    }

    /// <summary>O valor que as colunas guardam para este objeto (inverso de <see cref="DaColuna"/>).</summary>
    public string ValorDaColuna(CmsObjeto objeto) =>
        objeto.Bucket == CmsSupabase.BucketHtml ? objeto.Caminho : _supabase.UrlPublica(objeto.Bucket, objeto.Caminho);

    /// <summary>
    /// Remove o objeto se nenhuma linha atual das três tabelas o referencia.
    /// </summary>
    /// <remarks>
    /// A checagem vale para a remoção imediata e para a varredura. Um mesmo
    /// arquivo pode estar em duas linhas (o mesmo logo em duas marcas, uma linha
    /// copiada à mão no painel do Supabase); apagar porque UMA delas trocou
    /// quebraria a outra. Falha de rede propaga como
    /// <see cref="CmsIndisponivelException"/> para o chamador decidir.
    /// </remarks>
    public async Task<CmsRemocao> RemoverSeLivreAsync(CmsObjeto objeto, CancellationToken ct = default)
    {
        foreach (var (tabela, coluna) in ColunasQueApontamPara(objeto.Bucket))
        {
            var filtro = $"select=id&{PostgrestFiltro.Igual(coluna, ValorDaColuna(objeto))}&limit=1";
            var linhas = await _supabase.SelecionarAsync<CmsIdLinha>(tabela, filtro, ct);
            if (linhas.Count > 0)
                return CmsRemocao.AindaReferenciado;
        }

        await _supabase.RemoverObjetoAsync(objeto.Bucket, objeto.Caminho, ct);
        return CmsRemocao.Removido;
    }

    private static IEnumerable<(string Tabela, string Coluna)> ColunasQueApontamPara(string bucket) =>
        bucket == CmsSupabase.BucketHtml
            ? new[] { (CmsSupabase.Banners, "html_path") }
            : new[]
            {
                (CmsSupabase.Banners, "image_url"),
                (CmsSupabase.Marcas, "image_url"),
                (CmsSupabase.Depoimentos, "avatar_url"),
            };

    // Sem "..", sem barra inicial e sem vazio: o caminho vai na URL do DELETE.
    private static bool CaminhoSeguro(string caminho) =>
        !string.IsNullOrWhiteSpace(caminho)
        && !caminho.StartsWith('/')
        && !caminho.Contains("..", StringComparison.Ordinal)
        && !caminho.Contains('\\');

    private sealed class CmsIdLinha
    {
        public Guid Id { get; set; }
    }
}
