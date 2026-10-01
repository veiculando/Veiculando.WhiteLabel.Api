using System;
using System.Globalization;
using System.Linq;

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
    public const int TamanhoMaximoBusca = 100;

    private static readonly TimeZoneInfo Brasilia = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    /// <summary>O valor entre aspas duplas, com <c>\</c> e <c>"</c> escapados.</summary>
    public static string Aspas(string valor) =>
        "\"" + (valor ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary><c>{coluna}=eq."{valor}"</c>, codificado.</summary>
    public static string Igual(string coluna, string valor) =>
        $"{coluna}=eq.{Uri.EscapeDataString(Aspas(valor))}";

    /// <summary><c>id=eq.{uuid}</c>. Um Guid não tem caractere que precise de escape.</summary>
    public static string PorId(Guid id) => $"id=eq.{id:D}";

    /// <summary>
    /// <c>or=(c1.ilike."*v*",c2.ilike."*v*")</c>, ou null quando não há o que buscar.
    /// </summary>
    /// <remarks>
    /// <para>O valor é tratado como texto: <c>*</c> (curinga do PostgREST) é removido,
    /// e <c>\</c>, <c>%</c> e <c>_</c> (curingas do ILIKE) são escapados com <c>\</c>.
    /// Só depois vêm as aspas, que escapam as barras de novo: é assim que um
    /// <c>\%</c> chega ao Postgres como <c>\%</c> e não como <c>%</c>.</para>
    ///
    /// <para>Limitado a 100 caracteres: a busca vira ILIKE em várias colunas, sem
    /// índice, e não há motivo para um termo maior que isso.</para>
    /// </remarks>
    public static string Busca(string busca, params string[] colunas)
    {
        var termo = (busca ?? string.Empty).Trim();
        if (termo.Length > TamanhoMaximoBusca) termo = termo[..TamanhoMaximoBusca];
        termo = termo.Replace("*", string.Empty).Trim();
        if (termo.Length == 0) return null;

        var padrao = termo.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        var valor = Aspas($"*{padrao}*");
        var condicoes = string.Join(",", colunas.Select(c => $"{c}.ilike.{valor}"));
        return $"or={Uri.EscapeDataString($"({condicoes})")}";
    }

    /// <summary>
    /// <c>created_at=gte.{meia-noite de Brasília}</c> para <c>yyyy-MM-dd</c>.
    /// Falso para qualquer outro formato: o controller responde 400.
    /// </summary>
    /// <remarks>
    /// "Desde 01/09" é o dia no Brasil: meia-noite em UTC deixaria de fora o que foi
    /// cadastrado entre 21h e meia-noite de 31/08, no horário do operador.
    /// </remarks>
    public static bool TentarDesde(string desde, out string filtro)
    {
        filtro = null;
        if (!DateTime.TryParseExact(desde, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dia))
            return false;

        var meiaNoite = DateTime.SpecifyKind(dia.Date, DateTimeKind.Unspecified);
        var instante = new DateTimeOffset(meiaNoite, Brasilia.GetUtcOffset(meiaNoite));
        filtro = $"created_at=gte.{Uri.EscapeDataString(instante.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture))}";
        return true;
    }

    /// <summary>
    /// <c>todos</c> (ou vazio) = sem filtro; <c>ativo</c>/<c>inativo</c> = <c>is_active</c>.
    /// Falso para qualquer outro valor.
    /// </summary>
    public static bool TentarStatus(string status, out string filtro)
    {
        filtro = null;
        switch ((status ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "":
            case "todos":
                return true;
            case "ativo":
                filtro = "is_active=eq.true";
                return true;
            case "inativo":
                filtro = "is_active=eq.false";
                return true;
            default:
                return false;
        }
    }
}
