using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Veiculando.WhiteLabel.Api.Services
{
    /// <summary>
    /// O que o BFF pode repassar ao navegador de uma resposta de cadastro do Core.
    /// </summary>
    /// <remarks>
    /// VEI-SUP-57: o Core devolve a entidade salva inteira, e ela traz
    /// <c>usuarioCadastro</c>: a conta de serviço do BFF, com hash da senha, códigos de
    /// confirmação e permissões. Quem abria as ferramentas do navegador via tudo. As
    /// telas só usam os campos do próprio registro; os dados de usuário saem aqui, num
    /// lugar só, para todo controller que repassa resposta do Core.
    /// </remarks>
    public static class CoreRespostaSegura
    {
        private static readonly HashSet<string> Removidos = new(StringComparer.OrdinalIgnoreCase)
        {
            "usuarioCadastro", "usuarios", "senha"
        };

        public static string Limpar(string corpo)
        {
            if (string.IsNullOrWhiteSpace(corpo)) return "{}";
            JToken token;
            try { token = JToken.Parse(corpo); }
            catch (JsonReaderException) { return "{}"; }
            Remover(token);
            return token.ToString(Formatting.None);
        }

        /// <summary>Id do registro salvo (<c>data.result.id</c> do envelope do Core).</summary>
        public static int? IdDoResultado(string corpo)
        {
            if (string.IsNullOrWhiteSpace(corpo)) return null;
            try
            {
                var id = JToken.Parse(corpo).SelectToken("data.result.id");
                return id != null && id.Type == JTokenType.Integer ? id.Value<int>() : null;
            }
            catch (JsonReaderException) { return null; }
        }

        private static void Remover(JToken token)
        {
            switch (token)
            {
                case JObject objeto:
                    foreach (var propriedade in objeto.Properties().ToList())
                    {
                        if (Removidos.Contains(propriedade.Name)) propriedade.Remove();
                        else Remover(propriedade.Value);
                    }
                    break;
                case JArray lista:
                    foreach (var item in lista) Remover(item);
                    break;
            }
        }
    }
}
