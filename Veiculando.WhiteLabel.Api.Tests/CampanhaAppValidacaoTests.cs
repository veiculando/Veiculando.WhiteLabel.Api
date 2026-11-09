using System;
using System.Globalization;
using FluentAssertions;
using Veiculando.WhiteLabel.Api.Services;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests
{
    public class CampanhaAppValidacaoTests
    {
        [Fact]
        public void Valor_comprometido_sai_em_reais_mesmo_com_o_container_em_cultura_invariante()
        {
            var anterior = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            try
            {
                var hoje = DateTime.UtcNow.Date;
                var details = CampanhaAppValidacao.Validar(
                    new CampanhaAppRequest
                    {
                        Name = "Campanha", Product = "Produto",
                        StartDate = hoje.AddDays(10), EndDate = hoje.AddDays(40), Budget = 10_000m
                    },
                    new CampanhaAppValidacao.Comprometido(59_644.31m, hoje.AddDays(5), hoje.AddDays(20)));

                details.Should().Contain(d => d.Contains("R$ 59.644,31"));
                details.Should().Contain(d => d.Contains(hoje.AddDays(5).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)));
            }
            finally
            {
                CultureInfo.CurrentCulture = anterior;
            }
        }
    }
}
