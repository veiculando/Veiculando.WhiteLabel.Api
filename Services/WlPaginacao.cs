using System;
using System.Collections.Generic;

namespace Veiculando.WhiteLabel.Api.Services
{
    /// <summary>Parâmetros de paginação vindos da query string.</summary>
    public sealed class WlPaginaRequest
    {
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = WlPaginacao.PageSizePadrao;

        /// <summary>Campo de ordenação. Validado contra a whitelist do endpoint.</summary>
        public string Sort { get; set; }

        /// <summary>Ordem decrescente.</summary>
        public bool Desc { get; set; }
    }

    /// <summary>Uma página de resultados, com o total para a UI montar o rodapé.</summary>
    public sealed class WlPagina<T>
    {
        public IReadOnlyList<T> Itens { get; init; }
        public int Page { get; init; }
        public int PageSize { get; init; }
        public int Total { get; init; }
        public int TotalPaginas => PageSize <= 0 ? 0 : (int)Math.Ceiling(Total / (double)PageSize);
    }

    /// <summary>
    /// Regras de paginação comuns aos endpoints de listagem.
    /// </summary>
    /// <remarks>
    /// <para><b>Por que existe um teto.</b> As listagens de PI, reserva e
    /// programação devolviam a coleção inteira. Numa exibidora com histórico isso
    /// é uma varredura completa a cada abertura de tela — e um cliente podia
    /// pedir tudo de uma vez. O teto é do servidor, não uma sugestão ao cliente.</para>
    ///
    /// <para><b>Por que whitelist de ordenação.</b> Aceitar um nome de campo
    /// arbitrário e interpolá-lo numa query é injeção. Cada endpoint declara os
    /// campos que sabe ordenar e qualquer outro valor cai no padrão, em vez de
    /// virar erro — um cliente antigo mandando `sort=qualquerCoisa` continua
    /// recebendo uma página válida.</para>
    ///
    /// <para><b>Por que desempate por id.</b> Ordenar por um campo com valores
    /// repetidos (várias PIs no mesmo dia, vários pedidos com o mesmo status) não
    /// define uma ordem total: o SQL Server pode devolver as linhas empatadas em
    /// ordem diferente entre duas execuções, e aí a página 2 repete ou pula
    /// registros que a página 1 já mostrou. O id como último critério fecha isso.</para>
    /// </remarks>
    public static class WlPaginacao
    {
        public const int PageSizePadrao = 25;
        public const int PageSizeMaximo = 100;

        /// <summary>Normaliza página e tamanho para dentro dos limites do servidor.</summary>
        public static (int Page, int PageSize) Normalizar(WlPaginaRequest pedido)
        {
            var page = pedido?.Page ?? 1;
            if (page < 1) page = 1;

            var pageSize = pedido?.PageSize ?? PageSizePadrao;
            if (pageSize < 1) pageSize = PageSizePadrao;
            if (pageSize > PageSizeMaximo) pageSize = PageSizeMaximo;

            return (page, pageSize);
        }

        /// <summary>
        /// Devolve o campo de ordenação se ele estiver na whitelist; senão, o padrão.
        /// </summary>
        public static string Ordenacao(string solicitado, string padrao, params string[] permitidos)
        {
            if (string.IsNullOrWhiteSpace(solicitado))
                return padrao;

            foreach (var permitido in permitidos)
            {
                if (string.Equals(permitido, solicitado, StringComparison.OrdinalIgnoreCase))
                    return permitido;
            }

            return padrao;
        }

        public static WlPagina<T> Montar<T>(IReadOnlyList<T> itens, int page, int pageSize, int total) =>
            new() { Itens = itens, Page = page, PageSize = pageSize, Total = total };
    }
}
