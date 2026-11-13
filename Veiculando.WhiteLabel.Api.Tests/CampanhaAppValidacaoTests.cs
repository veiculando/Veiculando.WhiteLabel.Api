using System;
using System.Globalization;
using FluentAssertions;
using Veiculando.WhiteLabel.Api.Services;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests
{
    public class CampanhaAppValidacaoTests
    {
        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void Verba_zero_ou_negativa_e_campanha_sem_verba(int verba)
        {
            // O mapa do App manda budget 0 com o campo vazio (VEI-SUP-46).
            var hoje = DateTime.UtcNow.Date;
            var request = new CampanhaAppRequest
            {
                Name = "Campanha", Product = "Produto",
                StartDate = hoje.AddDays(1), EndDate = hoje.AddDays(30), Budget = verba
            };

            CampanhaAppValidacao.Validar(request).Should().BeEmpty();
            request.Budget.Should().BeNull();
        }

        [Fact]
        public void Verba_informada_continua_com_minimo_e_passo()
        {
            var hoje = DateTime.UtcNow.Date;
            CampanhaAppValidacao.Validar(new CampanhaAppRequest
            {
                Name = "Campanha", Product = "Produto",
                StartDate = hoje.AddDays(1), EndDate = hoje.AddDays(30), Budget = 2_500m
            }).Should().ContainSingle(d => d.Contains("múltipla de R$ 1.000"));
        }

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
