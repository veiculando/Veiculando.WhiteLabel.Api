using FluentAssertions;
using Veiculando.WhiteLabel.Api.Services;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests
{
    public class CampoLoginTests
    {
        [Fact]
        public void Wl_com_ponteiro_autentica_no_id_do_core()
        {
            var decisao = CampoLoginDecisao.Resolver(
                existeWl: true, senhaWlConfere: true, usuarioOperadorId: 44, wlUsuarioOperadorId: 7,
                existeCore: true, senhaCoreConfere: true, coreId: 99);

            decisao.Ok.Should().BeTrue();
            decisao.Origem.Should().Be(OrigemCampo.WhiteLabel);
            decisao.UsuarioOperadorId.Should().Be(44);
            decisao.WlUsuarioOperadorId.Should().Be(7);
        }

        [Fact]
        public void Wl_sem_ponteiro_recusa_e_nao_cai_no_core()
        {
            var decisao = CampoLoginDecisao.Resolver(
                existeWl: true, senhaWlConfere: true, usuarioOperadorId: null, wlUsuarioOperadorId: 7,
                existeCore: true, senhaCoreConfere: true, coreId: 44);

            decisao.Ok.Should().BeFalse();
        }

        [Fact]
        public void Wl_com_senha_errada_nao_tenta_o_core()
        {
            var decisao = CampoLoginDecisao.Resolver(
                existeWl: true, senhaWlConfere: false, usuarioOperadorId: 44, wlUsuarioOperadorId: 7,
                existeCore: true, senhaCoreConfere: true, coreId: 44);

            decisao.Ok.Should().BeFalse();
        }

        [Fact]
        public void Sem_wl_autentica_o_Usuario_Operador_do_core()
        {
            var decisao = CampoLoginDecisao.Resolver(
                existeWl: false, senhaWlConfere: false, usuarioOperadorId: null, wlUsuarioOperadorId: null,
                existeCore: true, senhaCoreConfere: true, coreId: 44);

            decisao.Ok.Should().BeTrue();
            decisao.Origem.Should().Be(OrigemCampo.Core);
            decisao.UsuarioOperadorId.Should().Be(44);
        }

        [Fact]
        public void Nenhum_dos_dois_falha()
        {
            CampoLoginDecisao.Resolver(false, false, null, null, false, false, 0).Ok.Should().BeFalse();
        }

        [Fact]
        public void Hash_da_senha_do_core_confere_com_o_valor_guardado()
        {
            var hash = SenhaUsuarioCore.Hash("senha123");
            SenhaUsuarioCore.Confere("senha123", hash).Should().BeTrue();
            SenhaUsuarioCore.Confere("outra", hash).Should().BeFalse();
        }
    }
}
