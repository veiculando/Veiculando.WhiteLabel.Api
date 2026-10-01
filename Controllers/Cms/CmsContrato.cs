using System;
using System.Collections.Generic;

namespace Veiculando.WhiteLabel.Api.Controllers.Cms;

// Contrato público de api/wl/cms/*, serializado em camelCase pelo padrão do
// AddControllers(). As fixtures em Veiculando.WhiteLabel.Api.Tests/Contratos/
// cms-*.json são a cópia que a Exibidora usa nos testes dela; o
// CmsContratoTests garante que os dois lados não divergem.
//
// Por que DTOs separados dos do PostgREST: o Supabase fala snake_case
// (image_url, is_active, display_order) e a Exibidora fala camelCase. Usar a
// mesma classe para os dois obrigaria a anotar cada propriedade com
// JsonPropertyName e bastaria uma anotação esquecida para o front receber
// is_active em vez de ativo — foi um descompasso assim, PascalCase contra
// camelCase, que quebrou telas inteiras no assurance da Sprint 10.

/// <summary>Valores aceitos em <see cref="CmsBannerDto.TipoDestino"/>.</summary>
public static class CmsTipoDestino
{
    /// <summary>O banner leva a uma URL https ou a uma âncora da LP (<c>destino</c>).</summary>
    public const string Link = "link";

    /// <summary>O banner leva ao hotsite servido pela LP em <c>/ofertas/{id}</c> (<c>htmlPath</c>).</summary>
    public const string Html = "html";
}

public sealed class CmsBannerDto
{
    public Guid Id { get; init; }
    public string Title { get; init; }

    /// <summary>URL pública completa do objeto em <c>cms-assets</c>: a LP usa o valor direto no &lt;img&gt;.</summary>
    public string ImageUrl { get; init; }

    /// <summary><see cref="CmsTipoDestino.Link"/> ou <see cref="CmsTipoDestino.Html"/>.</summary>
    public string TipoDestino { get; init; }

    /// <summary>Preenchido só no tipo link; null no tipo html.</summary>
    public string Destino { get; init; }

    /// <summary>
    /// Caminho relativo ao bucket <c>cms-html</c> (<c>banners/{uuid}.html</c>), não URL:
    /// a rota <c>/ofertas/[id]</c> da LP faz <c>download(html_path)</c>. Null no tipo link.
    /// </summary>
    public string HtmlPath { get; init; }

    public int DisplayOrder { get; init; }
    public bool Ativo { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class CmsMarcaDto
{
    public Guid Id { get; init; }
    public string Name { get; init; }

    /// <summary>URL pública completa do objeto em <c>cms-assets</c>.</summary>
    public string ImageUrl { get; init; }

    public int DisplayOrder { get; init; }
    public bool Ativo { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class CmsDepoimentoDto
{
    public Guid Id { get; init; }
    public string Author { get; init; }

    /// <summary>Cargo de quem deu o depoimento.</summary>
    public string Role { get; init; }

    public string Company { get; init; }
    public string Content { get; init; }

    /// <summary>URL pública completa do objeto em <c>cms-assets</c>, ou null sem foto.</summary>
    public string AvatarUrl { get; init; }

    public int DisplayOrder { get; init; }
    public bool Ativo { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>KPIs da tela de depoimentos, calculados pela RPC <c>cms_depoimentos_resumo</c>.</summary>
public sealed class CmsDepoimentosResumoDto
{
    public int Total { get; init; }
    public int Publicados { get; init; }
    public int Ocultos { get; init; }
    public int NovosNoMes { get; init; }
    public int Empresas { get; init; }
}

/// <summary>Resposta de POST e PUT.</summary>
/// <remarks>
/// <c>avisos</c> vem sempre, vazio quando não há o que avisar, para o front não
/// precisar tratar ausência e lista vazia como casos diferentes. Hoje só o HTML
/// de hotsite gera aviso (URL relativa), e o aviso não bloqueia a gravação.
/// </remarks>
public sealed class CmsSalvoDto<T>
{
    public T Item { get; init; }
    public IReadOnlyList<string> Avisos { get; init; } = Array.Empty<string>();
}

/// <summary>Corpo de 400, 404 e 503 do módulo.</summary>
public sealed class CmsErroDto
{
    public string Message { get; init; }
}
