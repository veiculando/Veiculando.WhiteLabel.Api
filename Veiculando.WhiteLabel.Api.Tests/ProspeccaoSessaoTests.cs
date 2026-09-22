using System;
using System.Threading;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Veiculando.Infra.Security;
using Veiculando.WhiteLabel.Api.Services;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests
{
    /// <summary>
    /// VEI-RD-83 task d — garantias de segurança da sessão de prospecção, e VEI-RD-81
    /// task d — URL temporária de documento de KYC. Os dois usam o mesmo assinador.
    /// </summary>
    /// <remarks>
    /// Testes do assinador em si, sem banco nem Testcontainers: a barreira que este card
    /// promete é criptográfica, e é aqui que ela pode ser demonstrada de forma
    /// determinística. Os cenários de rota (rate limit, 404 cross-tenant) dependem do
    /// <c>WlApiFactory</c>, que exige Docker.
    /// </remarks>
    public class ProspeccaoSessaoTests
    {
        private const int Afiliada = 7;
        private const string Recurso = "42:-";

        private static WlLinkTemporario Assinador(string segredo = null) =>
            new(Options.Create(new JwtSettings
            {
                Secret = segredo ?? new string('k', 48)
            }));

        // ------------------------------------------------------------------
        // 1. Audience — os DOIS lados (§8.2)
        // ------------------------------------------------------------------

        [Fact]
        public void Token_de_prospeccao_e_aceito_no_proposito_para_o_qual_foi_emitido()
        {
            // Sem este, uma validação que recusasse TUDO passaria no teste seguinte e
            // seria dada como correta.
            var assinador = Assinador();
            var token = assinador.Assinar(WlLinkTemporario.PropositoProspeccao, Recurso, Afiliada, TimeSpan.FromMinutes(2));

            assinador.Validar(token.Token, WlLinkTemporario.PropositoProspeccao, Recurso, Afiliada)
                .Should().NotBeNull();
        }

        [Fact]
        public void Token_de_prospeccao_e_recusado_em_qualquer_outro_proposito()
        {
            // É a barreira central do card: o token não pode ser reapresentado fora da
            // sessão de prospecção. A chave de cada propósito é derivada, então o token
            // não é sequer verificável com a chave de outro.
            var assinador = Assinador();
            var token = assinador.Assinar(WlLinkTemporario.PropositoProspeccao, Recurso, Afiliada, TimeSpan.FromMinutes(2));

            assinador.Validar(token.Token, WlLinkTemporario.PropositoDocumentoKyc, Recurso, Afiliada)
                .Should().BeNull();
        }

        [Fact]
        public void Um_segredo_diferente_nao_valida_o_token()
        {
            var token = Assinador(new string('a', 48))
                .Assinar(WlLinkTemporario.PropositoProspeccao, Recurso, Afiliada, TimeSpan.FromMinutes(2));

            Assinador(new string('b', 48))
                .Validar(token.Token, WlLinkTemporario.PropositoProspeccao, Recurso, Afiliada)
                .Should().BeNull();
        }

        // ------------------------------------------------------------------
        // 2. TTL
        // ------------------------------------------------------------------

        [Fact]
        public void Token_expirado_deixa_de_autenticar()
        {
            var assinador = Assinador();
            // TTL de 1s já emitido no passado: assinamos e esperamos o relógio passar.
            var token = assinador.Assinar(WlLinkTemporario.PropositoProspeccao, Recurso, Afiliada, TimeSpan.FromSeconds(1));

            Thread.Sleep(1200);

            assinador.Validar(token.Token, WlLinkTemporario.PropositoProspeccao, Recurso, Afiliada)
                .Should().BeNull();
        }

        [Fact]
        public void Token_dentro_do_ttl_continua_valendo()
        {
            var assinador = Assinador();
            var token = assinador.Assinar(WlLinkTemporario.PropositoProspeccao, Recurso, Afiliada, TimeSpan.FromMinutes(5));

            token.ExpiraEm.Should().BeAfter(DateTimeOffset.UtcNow);
            assinador.Validar(token.Token, WlLinkTemporario.PropositoProspeccao, Recurso, Afiliada)
                .Should().NotBeNull();
        }

        // ------------------------------------------------------------------
        // 3. Uso único — o jti que o consumidor registra
        // ------------------------------------------------------------------

        [Fact]
        public void Cada_emissao_carrega_um_jti_proprio()
        {
            // O uso único depende de o token trazer um identificador irrepetível; dois
            // tokens com o mesmo jti fariam o consumo de um invalidar o outro.
            var assinador = Assinador();
            var primeiro = assinador.Assinar(WlLinkTemporario.PropositoProspeccao, Recurso, Afiliada, TimeSpan.FromMinutes(2));
            var segundo = assinador.Assinar(WlLinkTemporario.PropositoProspeccao, Recurso, Afiliada, TimeSpan.FromMinutes(2));

            primeiro.Jti.Should().NotBeNullOrWhiteSpace();
            primeiro.Jti.Should().NotBe(segundo.Jti);
            primeiro.Token.Should().NotBe(segundo.Token);
        }

        [Fact]
        public void O_jti_devolvido_na_validacao_e_o_mesmo_da_emissao()
        {
            var assinador = Assinador();
            var token = assinador.Assinar(WlLinkTemporario.PropositoProspeccao, Recurso, Afiliada, TimeSpan.FromMinutes(2));

            assinador.Validar(token.Token, WlLinkTemporario.PropositoProspeccao, Recurso, Afiliada)
                .Should().Be(token.Jti);
        }

        // ------------------------------------------------------------------
        // 8. Tenant (ADR-WL-008)
        // ------------------------------------------------------------------

        [Fact]
        public void A_sessao_nao_alcanca_recurso_de_outra_afiliada()
        {
            var assinador = Assinador();
            var token = assinador.Assinar(WlLinkTemporario.PropositoProspeccao, Recurso, Afiliada, TimeSpan.FromMinutes(2));

            assinador.Validar(token.Token, WlLinkTemporario.PropositoProspeccao, Recurso, afiliadaId: Afiliada + 1)
                .Should().BeNull();
        }

        [Fact]
        public void Token_emitido_para_um_operador_nao_vale_para_outro()
        {
            // O recurso assinado inclui o id do operador, então trocar de dono invalida.
            var assinador = Assinador();
            var token = assinador.Assinar(WlLinkTemporario.PropositoProspeccao, "42:-", Afiliada, TimeSpan.FromMinutes(2));

            assinador.Validar(token.Token, WlLinkTemporario.PropositoProspeccao, "99:-", Afiliada)
                .Should().BeNull();
        }

        // ------------------------------------------------------------------
        // Integridade
        // ------------------------------------------------------------------

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("sem-ponto")]
        [InlineData("a.b.c")]
        [InlineData("!!!.???")]
        public void Token_malformado_e_recusado_sem_estourar(string token)
        {
            Assinador().Validar(token, WlLinkTemporario.PropositoProspeccao, Recurso, Afiliada)
                .Should().BeNull();
        }

        [Fact]
        public void Payload_adulterado_invalida_a_assinatura()
        {
            var assinador = Assinador();
            var token = assinador.Assinar(WlLinkTemporario.PropositoProspeccao, Recurso, Afiliada, TimeSpan.FromMinutes(2));

            var partes = token.Token.Split('.');
            var adulterado = partes[0].Substring(0, partes[0].Length - 2) + "XY" + "." + partes[1];

            assinador.Validar(adulterado, WlLinkTemporario.PropositoProspeccao, Recurso, Afiliada)
                .Should().BeNull();
        }

        [Fact]
        public void O_token_nao_carrega_dado_sensivel_em_claro()
        {
            // O card proíbe vazamento: o token não pode conter o conteúdo do documento,
            // a chave do blob nem o segredo. O payload é só metadado de roteamento.
            var assinador = Assinador("segredo-super-secreto-de-48-caracteres-aqui-ok!!");
            var token = assinador.Assinar(WlLinkTemporario.PropositoProspeccao, Recurso, Afiliada, TimeSpan.FromMinutes(2));

            token.Token.Should().NotContain("segredo-super-secreto");
        }

        // ------------------------------------------------------------------
        // VEI-RD-81 task d — o mesmo assinador, para documento de KYC
        // ------------------------------------------------------------------

        [Fact]
        public void Url_de_documento_de_kyc_vale_para_o_documento_emitido_e_para_nenhum_outro()
        {
            // Os dois lados: libera o documento certo E recusa o vizinho. Testada só pela
            // recusa, a guarda passaria por correta recusando todo download.
            var assinador = Assinador();
            var token = assinador.Assinar(WlLinkTemporario.PropositoDocumentoKyc, "10", Afiliada, TimeSpan.FromMinutes(5));

            assinador.Validar(token.Token, WlLinkTemporario.PropositoDocumentoKyc, "10", Afiliada).Should().NotBeNull();
            assinador.Validar(token.Token, WlLinkTemporario.PropositoDocumentoKyc, "11", Afiliada).Should().BeNull();
        }

        [Fact]
        public void Documento_de_outra_afiliada_nao_e_liberado_pelo_mesmo_token()
        {
            var assinador = Assinador();
            var token = assinador.Assinar(WlLinkTemporario.PropositoDocumentoKyc, "10", Afiliada, TimeSpan.FromMinutes(5));

            assinador.Validar(token.Token, WlLinkTemporario.PropositoDocumentoKyc, "10", Afiliada + 1)
                .Should().BeNull();
        }

        [Fact]
        public void A_url_de_documento_nunca_e_permanente()
        {
            var assinador = Assinador();
            var token = assinador.Assinar(WlLinkTemporario.PropositoDocumentoKyc, "10", Afiliada, TimeSpan.FromMinutes(5));

            token.ExpiraEm.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(5), TimeSpan.FromSeconds(5));
        }

        [Fact]
        public void Ttl_invalido_e_recusado_na_emissao()
        {
            var assinador = Assinador();

            assinador.Invoking(a => a.Assinar(WlLinkTemporario.PropositoDocumentoKyc, "10", Afiliada, TimeSpan.Zero))
                .Should().Throw<ArgumentOutOfRangeException>();
        }
    }
}
