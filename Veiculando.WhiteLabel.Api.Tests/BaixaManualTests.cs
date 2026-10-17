using FluentAssertions;
using Veiculando.WhiteLabel.Api.Services;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests
{
    public class BaixaManualTests
    {
        [Fact]
        public void Colagem_sem_colador_e_pendente_passa()
        {
            BaixaManual.RecusaColagem(null, "Pendente").Should().BeNull();
        }

        [Fact]
        public void Colagem_com_colador_pede_desatribuir()
        {
            BaixaManual.RecusaColagem(9, "Pendente").Should().Be("Desatribua o colador antes de dar a baixa.");
        }

        [Fact]
        public void Foto_so_sem_fiscal_e_aguardando_foto()
        {
            BaixaManual.RecusaFoto(4, "AguardandoFoto").Should().Be("Desatribua o fiscal antes de enviar a foto.");
            BaixaManual.RecusaFoto(null, "Pendente").Should().Be("O status não permite o envio da foto.");
            BaixaManual.RecusaFoto(null, "AguardandoFoto").Should().BeNull();
        }
    }
}
