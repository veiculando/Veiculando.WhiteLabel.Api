namespace Veiculando.WhiteLabel.Api.Services
{
    public static class PlanilhaAtribuicao
    {
        public static string Linha(string papel, string nome) =>
            string.IsNullOrWhiteSpace(nome) ? papel + ": ________________" : papel + ": " + nome.Trim();
    }
}
