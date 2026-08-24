using System.Collections.Generic;
using System.Linq;

namespace Veiculando.WhiteLabel.Api.Contracts
{
    /// <summary>
    /// Contrato único de listagem paginada (TP-C §3). Todo endpoint de
    /// listagem do BFF (Reservas, PIs, e futuros) devolve exatamente este
    /// formato — page/pageSize/total sempre presentes, nunca inferidos pelo
    /// cliente a partir do tamanho de <see cref="Items"/>.
    /// </summary>
    public class PagedResult<T>
    {
        public IReadOnlyList<T> Items { get; }
        public int Page { get; }
        public int PageSize { get; }
        public int TotalItems { get; }
        public int TotalPages => PageSize <= 0 ? 0 : (int)System.Math.Ceiling(TotalItems / (double)PageSize);

        public PagedResult(IReadOnlyList<T> items, int page, int pageSize, int totalItems)
        {
            Items = items;
            Page = page;
            PageSize = pageSize;
            TotalItems = totalItems;
        }
    }

    /// <summary>
    /// Query de paginação normalizada. Nunca aceitar page/pageSize crus do
    /// cliente sem passar por <see cref="Normalize"/> — page &lt; 1 ou
    /// pageSize fora dos limites são causa comum de erro N+1/estouro de
    /// memória se propagados direto pro EF.
    /// </summary>
    public class PageQuery
    {
        public const int DefaultPageSize = 20;
        public const int MaxPageSize = 100;

        public int Page { get; private set; }
        public int PageSize { get; private set; }
        public string SortBy { get; private set; }
        public string SortDirection { get; private set; }

        private PageQuery(int page, int pageSize, string sortBy, string sortDirection)
        {
            Page = page;
            PageSize = pageSize;
            SortBy = sortBy;
            SortDirection = sortDirection;
        }

        /// <summary>
        /// Normaliza page/pageSize/sortDirection e valida sortBy contra uma
        /// whitelist de colunas — string arbitrária do cliente NUNCA chega a
        /// virar um OrderBy dinâmico sem passar por aqui.
        /// </summary>
        public static PageQuery Normalize(int? page, int? pageSize, string sortBy, string sortDirection, IReadOnlyCollection<string> sortByWhitelist, string defaultSortBy)
        {
            var normalizedPage = (page.HasValue && page.Value >= 1) ? page.Value : 1;

            var normalizedPageSize = pageSize.HasValue && pageSize.Value > 0
                ? System.Math.Min(pageSize.Value, MaxPageSize)
                : DefaultPageSize;

            var normalizedSortBy = (!string.IsNullOrWhiteSpace(sortBy) && sortByWhitelist.Contains(sortBy))
                ? sortBy
                : defaultSortBy;

            var normalizedDirection = string.Equals(sortDirection, "desc", System.StringComparison.OrdinalIgnoreCase)
                ? "desc"
                : "asc";

            return new PageQuery(normalizedPage, normalizedPageSize, normalizedSortBy, normalizedDirection);
        }

        public int Skip => (Page - 1) * PageSize;
    }
}
