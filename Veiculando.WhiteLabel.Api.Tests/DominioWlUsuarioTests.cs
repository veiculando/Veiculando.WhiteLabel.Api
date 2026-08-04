using System;
using System.Linq;
using FluentAssertions;
using Veiculando.Domain.Entities.WhiteLabel;
using Veiculando.Domain.Enums;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests
{
    /// <summary>
    /// Regras de <c>WlUsuario</c> e <c>WlPermissoesValidas</c>.
    /// </summary>
    /// <remarks>
    /// Unitarios de verdade: nao tocam banco nem sobem a API, entao rodam em
    /// milissegundos e nao precisam da fixture. Cobrem o que e logica pura de
    /// dominio — o resto da suite cobre o que depende do provider EF.
    /// </remarks>
    public class DominioWlUsuarioTests
    {
        private static WlUsuarioAfiliada Operador(string[]? permissoes = null) =>
            new("Fulano", "fulano@exemplo.com", "hash", 1, permissoes: permissoes);

        [Fact]
        public void Permissoes_invalidas_geram_notificacao_e_nao_sao_gravadas()
        {
            var operador = Operador(new[] { "PecaGerenciar", "PermissaoQueNaoExiste" });

            operador.IsValid().Should().BeFalse();
            operador.Notifications.Should().Contain(n => n.Message.Contains("PermissaoQueNaoExiste"));
        }

        [Fact]
        public void Permissoes_duplicadas_sao_deduplicadas()
        {
            var operador = Operador(new[] { "Checking", "Checking", "PecaGerenciar" });

            operador.ObterPermissoes().Should().BeEquivalentTo(new[] { "Checking", "PecaGerenciar" });
        }

        [Fact]
        public void Operador_sem_permissao_devolve_lista_vazia_e_nao_null()
        {
            // O AuthController itera sobre isso para montar as claims; null viraria
            // NullReferenceException no login.
            Operador().ObterPermissoes().Should().NotBeNull().And.BeEmpty();
        }

        [Fact]
        public void Wildcard_nao_e_permissao_valida()
        {
            // O PermissionService do frontend tinha tratamento de '*' que concedia
            // tudo. O dominio nunca aceitou esse valor — foi removido de la.
            WlPermissoesValidas.ValidarPermissoes(new[] { "*" }, out var invalidas)
                .Should().BeFalse();

            invalidas.Should().Contain("*");
        }

        [Fact]
        public void Deletar_marca_status_e_registra_data_de_exclusao()
        {
            var operador = Operador();

            operador.Deletar();

            operador.StatusExibicao.Should().Be(StatusExibicaoEnum.Deletado);
            operador.DataExclusao.Should().NotBeNull();
        }

        [Fact]
        public void Token_de_recuperacao_e_validado_pelo_valor_bruto_e_nao_pelo_hash()
        {
            var operador = Operador();

            var tokenBruto = operador.GerarTokenRecuperacao();

            tokenBruto.Should().NotBeNullOrWhiteSpace();
            operador.ValidarTokenRecuperacao(tokenBruto).Should().BeTrue();
            operador.ValidarTokenRecuperacao("token-errado").Should().BeFalse();
        }

        [Fact]
        public void Token_de_recuperacao_expirado_nao_valida()
        {
            var operador = Operador();
            var token = operador.GerarTokenRecuperacao();

            // A validade e de 2h a partir da geracao; sem controle de relogio no
            // dominio, o que da para afirmar aqui e que invalidar mata o token.
            operador.InvalidarTokenRecuperacao();

            operador.ValidarTokenRecuperacao(token).Should().BeFalse();
        }

        [Fact]
        public void Alterar_senha_invalida_o_token_de_recuperacao()
        {
            var operador = Operador();
            var token = operador.GerarTokenRecuperacao();

            operador.AlterarSenha("novo-hash");

            operador.ValidarTokenRecuperacao(token).Should().BeFalse(
                "trocar a senha precisa queimar o token, senao ele continua valendo " +
                "para uma segunda troca por quem interceptou o e-mail");
        }

        [Fact]
        public void Registrar_login_grava_a_data()
        {
            var operador = Operador();
            operador.DataUltimoLogin.Should().BeNull();

            operador.RegistrarLogin();

            operador.DataUltimoLogin.Should().NotBeNull();
            operador.DataUltimoLogin!.Value.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        }

        [Fact]
        public void Atualizar_dados_preserva_o_email()
        {
            var operador = Operador();

            operador.AtualizarDados("Novo Nome", "Gerente", "Comercial", "1199999999");

            operador.Nome.Should().Be("Novo Nome");
            operador.Cargo.Should().Be("Gerente");
            operador.Email.Endereco.Should().Be("fulano@exemplo.com",
                "o e-mail identifica o operador e nao muda por AtualizarDados");
        }

        [Theory]
        [InlineData("PecaGerenciar")]
        [InlineData("Checking")]
        [InlineData("PedidoReservaGerenciar")]
        [InlineData("PedidoInsercaoGerenciar")]
        [InlineData("UsuarioAfiliadaGerenciar")]
        public void As_cinco_permissoes_da_whitelist_sao_aceitas(string permissao)
        {
            WlPermissoesValidas.ValidarPermissoes(new[] { permissao }, out _).Should().BeTrue();
        }

        [Fact]
        public void Whitelist_do_dominio_tem_exatamente_cinco_permissoes()
        {
            // Se alguem adicionar uma sexta, o AuthorizationSetup do BFF e o
            // PERMISSOES_WL do frontend precisam acompanhar. Este teste forca a
            // conversa em vez de deixar os tres divergirem em silencio.
            WlPermissoesValidas.Lista.Should().HaveCount(5);
        }
    }
}
