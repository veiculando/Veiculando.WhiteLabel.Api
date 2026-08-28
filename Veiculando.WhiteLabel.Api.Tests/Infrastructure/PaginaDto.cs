using System.Collections.Generic;

namespace Veiculando.WhiteLabel.Api.Tests.Infrastructure
{
    /// <summary>
    /// Envelope de paginacao devolvido pelas listagens do BFF.
    /// </summary>
    /// <remarks>
    /// As listagens de PI, reserva e programacao passaram a devolver
    /// <c>{ itens, page, pageSize, total, totalPaginas }</c> em vez de um array
    /// cru. Ter o envelope tipado aqui evita cada teste redeclarar o seu.
    /// </remarks>
    public sealed record PaginaDto<T>(
        List<T> Itens,
        int Page,
        int PageSize,
        int Total,
        int TotalPaginas);
}
