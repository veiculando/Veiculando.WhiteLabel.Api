using System.Threading.Tasks;
using Veiculando.WhiteLabel.Api.Middleware;

namespace Veiculando.WhiteLabel.Api.Services
{
    /// <summary>Resultado do KPI "Faturamento Previsto" (VEI-RD-85, VEI-RD-92).</summary>
    public sealed class FaturamentoPrevistoResult
    {
        public bool Disponivel { get; init; }
        public decimal? Valor { get; init; }
        public decimal? VariacaoPercentualVsCicloAnterior { get; init; }

        public static readonly FaturamentoPrevistoResult Indisponivel = new()
        {
            Disponivel = false,
            Valor = null,
            VariacaoPercentualVsCicloAnterior = null
        };
    }

    public interface IReceitaService
    {
        Task<FaturamentoPrevistoResult> ObterFaturamentoPrevistoAsync(int periodoId, ITenantQueries tenant);
    }

    /// <summary>
    /// Fonte única de receita, compartilhada entre o KPI "Faturamento Previsto" do
    /// Dashboard (VEI-RD-85) e a tela de Relatórios (VEI-RD-92).
    /// </summary>
    /// <remarks>
    /// <para><b>Por que este serviço existe, e por que ele não calcula nada
    /// ainda.</b> O plano tático Ordem 5 (seção 7) é explícito: "os números têm
    /// de bater" entre as duas telas — dois lugares calculando receita cada um a
    /// seu jeito é exatamente como se chega a dois faturamentos diferentes para o
    /// mesmo período. A decisão de 2026-09-16 (Humano) definiu que a receita
    /// aparece nas duas telas, mas NÃO definiu a fórmula nem a fonte dos dados.
    /// Inventar uma aqui seria repetir o erro que já custou a remoção do antigo
    /// campo <c>ReceitaMensal</c> (ver <c>DashboardController.GetKpis</c>): o PRD
    /// proíbe apresentar um número como se fosse receita real quando ele não é.
    /// </para>
    ///
    /// <para><b>O que fazer quando a fórmula vier definida.</b> Implementar o
    /// cálculo dentro de <see cref="ObterFaturamentoPrevistoAsync"/> — só isso.
    /// Nenhum consumidor (Dashboard, Relatórios) precisa mudar: os dois já leem
    /// <see cref="FaturamentoPrevistoResult.Disponivel"/> e tratam
    /// <c>false</c> como "não renderizar como se fosse receita", exatamente a
    /// regra que sobrevive a qualquer resposta (plano tático, seção 6).</para>
    /// </remarks>
    public sealed class ReceitaService : IReceitaService
    {
        public Task<FaturamentoPrevistoResult> ObterFaturamentoPrevistoAsync(int periodoId, ITenantQueries tenant)
        {
            return Task.FromResult(FaturamentoPrevistoResult.Indisponivel);
        }
    }
}
