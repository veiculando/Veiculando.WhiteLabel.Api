namespace Veiculando.WhiteLabel.Api.Services
{
    public static class BaixaManual
    {
        public static string RecusaColagem(int? idColador, string status)
        {
            if (idColador is > 0) return "Desatribua o colador antes de dar a baixa.";
            if (status != "Pendente" && status != "Colagem") return "O status não permite a baixa da colagem.";
            return null;
        }

        public static string RecusaFoto(int? idFiscal, string status)
        {
            if (idFiscal is > 0) return "Desatribua o fiscal antes de enviar a foto.";
            if (status != "AguardandoFoto") return "O status não permite o envio da foto.";
            return null;
        }
    }
}
