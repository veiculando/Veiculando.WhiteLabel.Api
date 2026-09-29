using System;
using System.Collections.Generic;
using System.Linq;
using Veiculando.Domain.Entities;

namespace Veiculando.WhiteLabel.Api.Services
{
    /// <summary>
    /// Período de veiculação de uma PI: rótulo comercial do primeiro período e o
    /// intervalo que cobre todos os itens.
    /// </summary>
    /// <param name="Id">Id do primeiro período (menor <c>DataInicio</c>).</param>
    /// <param name="Rotulo">Rótulo comercial do primeiro período (<c>Periodo.Nome</c>).</param>
    /// <param name="DataInicio">Início do primeiro período.</param>
    /// <param name="DataFim">Fim do último período.</param>
    /// <param name="Quantidade">Quantos períodos distintos a PI tem.</param>
    public sealed record WlPeriodoVeiculacao(int Id, string Rotulo, DateTime DataInicio, DateTime DataFim, int Quantidade);

    /// <summary>
    /// Consolida cidade e período a partir dos itens de uma PI, para as colunas
    /// Cidade e Período das listagens de PIs e de Check out (D8, D14).
    /// </summary>
    /// <remarks>
    /// Uma PI pode ter itens em mais de uma cidade e em mais de um período. A
    /// linha mostra um valor representante e a quantidade, para a UI indicar
    /// "+N" em vez de esconder que há mais. O representante segue a mesma regra
    /// da ordenação do servidor (<c>Min</c>), para a coluna exibida e a ordem da
    /// lista não se contradizerem.
    /// </remarks>
    public static class WlResumoItens
    {
        /// <summary>Menor nome de cidade (ordem alfabética) e total de cidades distintas.</summary>
        public static (string Nome, int Quantidade) Cidades(IEnumerable<string> nomes)
        {
            var distintas = nomes
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct()
                .OrderBy(n => n, StringComparer.CurrentCulture)
                .ToList();

            return (distintas.FirstOrDefault(), distintas.Count);
        }

        /// <summary>Período de veiculação dos itens; <c>null</c> se nenhum item tem período.</summary>
        public static WlPeriodoVeiculacao Periodos(IEnumerable<Periodo> periodos)
        {
            var distintos = periodos
                .Where(p => p != null)
                .GroupBy(p => p.Id)
                .Select(g => g.First())
                .OrderBy(p => p.DataInicio)
                .ThenBy(p => p.Id)
                .ToList();

            if (distintos.Count == 0)
                return null;

            var primeiro = distintos[0];
            return new WlPeriodoVeiculacao(
                primeiro.Id,
                primeiro.Nome,
                primeiro.DataInicio,
                distintos.Max(p => p.DataFim),
                distintos.Count);
        }
    }
}
