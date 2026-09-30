using System;

namespace Veiculando.WhiteLabel.Api.Services.Cms;

/// <summary>
/// Monta filtros do PostgREST com o valor escapado e codificado.
/// </summary>
/// <remarks>
/// <para>No PostgREST, vírgula, parênteses e ponto são sintaxe do filtro:
/// <c>company=eq.a,b</c> e <c>or=(title.ilike.x)</c> são estruturas, não texto. Um
/// valor vindo do usuário, interpolado cru, pode fechar o filtro e abrir outro.
/// Entre aspas duplas o valor é literal; dentro delas só <c>"</c> e <c>\</c>
/// precisam de escape.</para>
///
/// <para>Depois das aspas, o parâmetro inteiro passa por
/// <see cref="Uri.EscapeDataString"/> uma única vez: <c>&amp;</c>, <c>#</c>, <c>%</c> e
/// <c>+</c> no valor não podem virar separador de query nem espaço.</para>
/// </remarks>
public static class PostgrestFiltro
{
    /// <summary>O valor entre aspas duplas, com <c>\</c> e <c>"</c> escapados.</summary>
    public static string Aspas(string valor) =>
        "\"" + (valor ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary><c>{coluna}=eq."{valor}"</c>, codificado.</summary>
    public static string Igual(string coluna, string valor) =>
        $"{coluna}=eq.{Uri.EscapeDataString(Aspas(valor))}";

    /// <summary><c>id=eq.{uuid}</c>. Um Guid não tem caractere que precise de escape.</summary>
    public static string PorId(Guid id) => $"id=eq.{id:D}";
}
