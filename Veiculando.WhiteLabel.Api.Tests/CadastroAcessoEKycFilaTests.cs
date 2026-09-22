using System.Linq;
using FluentAssertions;
using Veiculando.Domain.Enums;
using Veiculando.WhiteLabel.Api.Controllers;
using Veiculando.WhiteLabel.Api.Services;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests
{
    /// <summary>
    /// VEI-RD-82 — política de e-mail corporativo, e VEI-RD-80 — os estados da fila.
    /// </summary>
    /// <remarks>
    /// A parte do <c>PermiteAsync</c> que consulta <c>AfiliadaConfiguracao</c> precisa de
    /// banco e vive nos testes de integração; o teste de domínio, que é onde a guarda
    /// erra na prática (maiúscula, espaço, subdomínio), é puro e fica aqui.
    /// </remarks>
    public class CadastroAcessoEKycFilaTests
    {
        private static WlPoliticaEmailCorporativo Politica() => new(null);

        [Theory]
        [InlineData("fulano@gmail.com")]
        [InlineData("fulano@hotmail.com")]
        [InlineData("fulano@outlook.com")]
        [InlineData("fulano@yahoo.com")]
        [InlineData("fulano@live.com")]
        [InlineData("fulano@icloud.com")]
        public void Os_seis_dominios_pessoais_sao_bloqueados(string email)
        {
            // Seis, não quatro: o PRD §5.14 cita gmail/hotmail/outlook/yahoo e o frame
            // 287:12843 acrescenta live.com e icloud.com.
            Politica().DominioBloqueado(email).Should().BeTrue();
        }

        [Theory]
        [InlineData("comercial@outdoorpremium.com.br")]
        [InlineData("contato@impar.com.br")]
        [InlineData("fulano@gmail.com.br")]
        [InlineData("fulano@naogmail.com")]
        public void Dominio_corporativo_passa(string email)
        {
            // O outro lado da guarda. Testada só pela recusa, um "return true" passaria
            // por correta recusando todo cadastro — inclusive os legítimos.
            Politica().DominioBloqueado(email).Should().BeFalse();
        }

        [Theory]
        [InlineData("  Fulano@GMAIL.COM  ")]
        [InlineData("FULANO@Gmail.Com")]
        public void A_normalizacao_impede_escapar_por_maiuscula_ou_espaco(string email)
        {
            Politica().DominioBloqueado(email).Should().BeTrue();
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("sem-arroba")]
        [InlineData("dois@@arrobas.com")]
        public void Entrada_malformada_nao_estoura_a_guarda(string email)
        {
            Politica().DominioBloqueado(email).Should().BeFalse();
        }

        [Fact]
        public void A_lista_de_dominios_tem_um_dono_so()
        {
            // O frontend não define a lista: ele a recebe do GET /config/cadastro-acesso.
            Politica().DominiosBloqueados.Should().BeEquivalentTo(new[]
            {
                "gmail.com", "hotmail.com", "outlook.com", "yahoo.com", "live.com", "icloud.com"
            });
        }

        // ------------------------------------------------------------------

        [Fact]
        public void A_fila_de_kyc_enxerga_cinco_estados_e_nao_sete()
        {
            KycController.EstadosDaFila.Should().BeEquivalentTo(new[]
            {
                EstadoOnboardingEnum.PendenteVerificacao,
                EstadoOnboardingEnum.EmAnalise,
                EstadoOnboardingEnum.AjustesSolicitados,
                EstadoOnboardingEnum.Aprovado,
                EstadoOnboardingEnum.Rejeitado
            });
        }

        [Fact]
        public void Rascunho_e_suspenso_ficam_fora_da_fila_mas_seguem_no_dominio()
        {
            // Rascunho: o solicitante ainda não enviou — ninguém pediu análise.
            // Suspenso: ação pós-aprovação, aplicada no detalhe de organização já ativa.
            KycController.EstadosDaFila.Should().NotContain(EstadoOnboardingEnum.Rascunho);
            KycController.EstadosDaFila.Should().NotContain(EstadoOnboardingEnum.Suspenso);

            System.Enum.GetValues(typeof(EstadoOnboardingEnum)).Length.Should().Be(7);
        }
    }
}
