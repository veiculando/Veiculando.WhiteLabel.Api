using System;
using System.Reflection;
using FluentAssertions;
using Veiculando.Domain.Entities;
using Veiculando.WhiteLabel.Api.Services;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests
{
    public class CriacaoOperadorCampoTests
    {
        [Fact]
        public void Cria_os_dois_e_o_wl_aponta_para_o_core_com_os_dois_papeis()
        {
            var (core, wl) = CriacaoOperadorCampo.Montar(
                Afiliada(13), Perfil(), "Joao Campo", "joao@exemplo.com", "senha123", true, true, null);

            core.IsValid().Should().BeTrue();
            core.Colador.Should().BeTrue();
            core.Fiscal.Should().BeTrue();
            core.IdAfiliada.Should().Be(13);
            wl.AfiliadaId.Should().Be(13);
            wl.Usuario_Operador.Should().BeSameAs(core);
        }

        [Fact]
        public void Colador_e_fiscal_podem_vir_separados()
        {
            var (core, _) = CriacaoOperadorCampo.Montar(
                Afiliada(4), Perfil(), "So Colador", "colador@exemplo.com", "senha123", true, false, null);

            core.Colador.Should().BeTrue();
            core.Fiscal.Should().BeFalse();
        }

        private static Afiliada Afiliada(int id)
        {
            var afiliada = (Afiliada)Activator.CreateInstance(typeof(Afiliada), nonPublic: true);
            var property = typeof(Afiliada).GetProperty("Id", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            property.DeclaringType.GetProperty("Id").SetValue(afiliada, id);
            return afiliada;
        }

        private static PerfilUsuario Perfil() =>
            (PerfilUsuario)Activator.CreateInstance(typeof(PerfilUsuario), nonPublic: true);
    }
}
