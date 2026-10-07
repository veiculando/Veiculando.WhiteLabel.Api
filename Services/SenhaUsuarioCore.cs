using System.Security.Cryptography;
using System.Text;

namespace Veiculando.WhiteLabel.Api.Services
{
    /// <summary>
    /// Confere a senha do Usuario do Core. O algoritmo é o de Usuario.EncryptPassword.
    /// </summary>
    public static class SenhaUsuarioCore
    {
        public static bool Confere(string senhaInformada, string senhaArmazenada)
        {
            if (string.IsNullOrEmpty(senhaInformada) || string.IsNullOrEmpty(senhaArmazenada))
                return false;
            return senhaArmazenada == Hash(senhaInformada);
        }

        public static string Hash(string senha)
        {
            var password = senha + "|3d331cc9-RxTx-aABb-7e32989c2981";
            using (var md5 = MD5.Create())
            {
                var data = md5.ComputeHash(Encoding.UTF32.GetBytes(password));
                var sb = new StringBuilder();
                foreach (var t in data)
                    sb.Append(t.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
