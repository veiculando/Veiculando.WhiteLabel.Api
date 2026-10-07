namespace Veiculando.WhiteLabel.Api.Services
{
    public enum OrigemCampo
    {
        Nenhuma = 0,
        WhiteLabel = 1,
        Core = 2
    }

    public sealed class CampoLoginDecisao
    {
        public bool Ok { get; private set; }
        public int UsuarioOperadorId { get; private set; }
        public int? WlUsuarioOperadorId { get; private set; }
        public OrigemCampo Origem { get; private set; }

        public static CampoLoginDecisao Recusar() => new CampoLoginDecisao();

        public static CampoLoginDecisao WhiteLabel(int usuarioOperadorId, int wlUsuarioOperadorId) =>
            new CampoLoginDecisao
            {
                Ok = true,
                UsuarioOperadorId = usuarioOperadorId,
                WlUsuarioOperadorId = wlUsuarioOperadorId,
                Origem = OrigemCampo.WhiteLabel
            };

        public static CampoLoginDecisao Core(int usuarioOperadorId) =>
            new CampoLoginDecisao
            {
                Ok = true,
                UsuarioOperadorId = usuarioOperadorId,
                Origem = OrigemCampo.Core
            };

        /// <summary>
        /// Se existe WLUsuario_Operador do e-mail nesta afiliada, a tentativa termina nele.
        /// Sem a FK para Usuario_Operador, recusa. Senão, autentica o Usuario_Operador do Core.
        /// </summary>
        public static CampoLoginDecisao Resolver(
            bool existeWl,
            bool senhaWlConfere,
            int? usuarioOperadorId,
            int? wlUsuarioOperadorId,
            bool existeCore,
            bool senhaCoreConfere,
            int coreId)
        {
            if (existeWl)
            {
                if (!senhaWlConfere || usuarioOperadorId == null || usuarioOperadorId <= 0 || wlUsuarioOperadorId == null)
                    return Recusar();
                return WhiteLabel(usuarioOperadorId.Value, wlUsuarioOperadorId.Value);
            }

            if (existeCore && senhaCoreConfere && coreId > 0)
                return Core(coreId);

            return Recusar();
        }
    }
}
