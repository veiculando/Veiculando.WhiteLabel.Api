using FluentAssertions;
using Veiculando.WhiteLabel.Api.Services;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests
{
    public class PlanilhaAtribuicaoTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Sem_nome_deixa_linha_para_anotar_a_mao(string nome)
        {
            PlanilhaAtribuicao.Linha("Colador", nome).Should().Be("Colador: ________________");
        }

        [Fact]
        public void Com_nome_imprime_o_responsavel()
        {
            PlanilhaAtribuicao.Linha("Fiscal", " Ana ").Should().Be("Fiscal: Ana");
        }
    }
}
