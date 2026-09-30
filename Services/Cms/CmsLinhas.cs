using System;
using System.Text.Json.Serialization;
using Veiculando.WhiteLabel.Api.Controllers.Cms;

namespace Veiculando.WhiteLabel.Api.Services.Cms;

// Linhas como o PostgREST as devolve: snake_case, lidas com
// SupabaseCmsClient.Json. Ficam separadas dos DTOs públicos (CmsContrato.cs)
// para que o nome de coluna nunca vaze para o front.
//
// Colunas que só existem depois da migration v2 do TP-2 (title, tipo_destino,
// html_path, avatar_url, company, updated_at) chegam com o valor padrão se a
// migration ainda não foi aplicada, em vez de quebrar a leitura.

/// <summary>Nomes das tabelas e buckets do CMS no Supabase.</summary>
public static class CmsSupabase
{
    public const string Banners = "banners";
    public const string Marcas = "marcas";
    public const string Depoimentos = "depoimentos";
    public const string Auditoria = "cms_auditoria";

    public const string RpcResumoDepoimentos = "cms_depoimentos_resumo";

    /// <summary>Imagens (banner, logo, avatar). Público: a LP usa a URL direto.</summary>
    public const string BucketAssets = "cms-assets";

    /// <summary>Hotsites HTML. A LP os serve em /ofertas/{id} (ADR-CMS-003).</summary>
    public const string BucketHtml = "cms-html";
}

public sealed class CmsBannerLinha
{
    public Guid Id { get; set; }
    public string Title { get; set; }
    public string ImageUrl { get; set; }
    public string TipoDestino { get; set; }
    public string Destino { get; set; }
    public string HtmlPath { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public CmsBannerDto ParaDto() => new()
    {
        Id = Id,
        Title = Title ?? string.Empty,
        ImageUrl = ImageUrl,
        // Antes da migration v2 não há tipo_destino; toda linha antiga é link.
        TipoDestino = string.IsNullOrEmpty(TipoDestino) ? CmsTipoDestino.Link : TipoDestino,
        Destino = Destino,
        HtmlPath = HtmlPath,
        DisplayOrder = DisplayOrder,
        Ativo = IsActive,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt == default ? CreatedAt : UpdatedAt,
    };
}

public sealed class CmsMarcaLinha
{
    public Guid Id { get; set; }
    public string Name { get; set; }
    public string ImageUrl { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public CmsMarcaDto ParaDto() => new()
    {
        Id = Id,
        Name = Name,
        ImageUrl = ImageUrl,
        DisplayOrder = DisplayOrder,
        Ativo = IsActive,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt == default ? CreatedAt : UpdatedAt,
    };
}

public sealed class CmsDepoimentoLinha
{
    public Guid Id { get; set; }
    public string Author { get; set; }
    public string Role { get; set; }
    public string Company { get; set; }
    public string Content { get; set; }
    public string AvatarUrl { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public CmsDepoimentoDto ParaDto() => new()
    {
        Id = Id,
        Author = Author,
        Role = Role,
        Company = Company,
        Content = Content,
        AvatarUrl = AvatarUrl,
        DisplayOrder = DisplayOrder,
        Ativo = IsActive,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt == default ? CreatedAt : UpdatedAt,
    };
}

/// <summary>
/// Retorno da RPC <c>cms_depoimentos_resumo</c>.
/// </summary>
/// <remarks>
/// A função monta o objeto com <c>json_build_object('novosNoMes', ...)</c>, em
/// camelCase, e não com os nomes de coluna. Com a política snake_case do client,
/// <c>NovosNoMes</c> seria procurado como <c>novos_no_mes</c> e viria 0 sem erro.
/// Por isso cada campo declara o nome exato que o SQL do TP-2 usa.
/// </remarks>
public sealed class CmsDepoimentosResumoRpc
{
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("publicados")] public int Publicados { get; set; }
    [JsonPropertyName("ocultos")] public int Ocultos { get; set; }
    [JsonPropertyName("novosNoMes")] public int NovosNoMes { get; set; }
    [JsonPropertyName("empresas")] public int Empresas { get; set; }

    public CmsDepoimentosResumoDto ParaDto() => new()
    {
        Total = Total,
        Publicados = Publicados,
        Ocultos = Ocultos,
        NovosNoMes = NovosNoMes,
        Empresas = Empresas,
    };
}
