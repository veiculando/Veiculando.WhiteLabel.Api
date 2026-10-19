using Veiculando.Domain.Entities;
using Veiculando.Domain.Entities.WhiteLabel;
using Veiculando.Domain.ValueObjects;

namespace Veiculando.WhiteLabel.Api.Services
{
    public static class CriacaoOperadorCampo
    {
        public static (Usuario_Operador Core, WLUsuario_Operador Wl) Montar(
            Afiliada afiliada,
            PerfilUsuario perfil,
            string nome,
            string email,
            string senha,
            bool colador,
            bool fiscal,
            Usuario usuarioCadastro)
        {
            var core = new Usuario_Operador(
                afiliada, perfil, nome, new Email(email), senha, colador, fiscal, usuarioCadastro);
            var wl = new WLUsuario_Operador(email, null, afiliada?.Id ?? 0);
            if (core.IsValid())
                wl.ApontarPara(core);
            return (core, wl);
        }
    }
}
