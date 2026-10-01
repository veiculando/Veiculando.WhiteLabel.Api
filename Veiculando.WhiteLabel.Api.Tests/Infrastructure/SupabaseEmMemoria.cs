using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Veiculando.WhiteLabel.Api.Tests.Infrastructure
{
    /// <summary>
    /// Um Supabase mínimo em memória atrás do <see cref="SupabaseFake"/>: tabelas
    /// do PostgREST e objetos do Storage.
    /// </summary>
    /// <remarks>
    /// <para>Existe para testar fluxos com estado (troca de arquivo, varredura)
    /// sem programar resposta por resposta. Entende só o subconjunto de PostgREST
    /// que o BFF usa: <c>select</c>, <c>limit</c>, <c>offset</c>, <c>order</c> e os
    /// filtros <c>eq</c>, <c>is.null</c> e <c>lt</c>. Qualquer outro filtro lança,
    /// para o teste não passar por um filtro ignorado.</para>
    ///
    /// <para><see cref="Falhar"/> intercepta uma chamada antes do estado: devolver
    /// uma resposta simula erro do Supabase; <see cref="Aplicar"/> junto com uma
    /// falha simula o timeout ambíguo (o PATCH entrou, a resposta não voltou).</para>
    /// </remarks>
    public sealed class SupabaseEmMemoria
    {
        private readonly object _trava = new();

        public SupabaseFake Handler { get; } = new();

        public Dictionary<string, List<JsonObject>> Tabelas { get; } = new();

        /// <summary>Objetos do Storage, por "bucket/caminho".</summary>
        public HashSet<string> Objetos { get; } = new();

        /// <summary>Devolve uma resposta para simular falha; null deixa seguir.</summary>
        public Func<SupabaseFake.Chamada, HttpResponseMessage> Falhar { get; set; } = _ => null;

        /// <summary>Com <see cref="Falhar"/>: aplica a mudança mesmo assim (timeout ambíguo).</summary>
        public Func<SupabaseFake.Chamada, bool> Aplicar { get; set; } = _ => false;

        private DateTimeOffset? _agora;

        /// <summary>
        /// O "agora" do banco simulado (default do <c>created_at</c>). Segue o relógio
        /// real, porque o BFF sob teste usa o relógio real para a carência da
        /// varredura; um teste que controla o tempo o fixa.
        /// </summary>
        public DateTimeOffset Agora
        {
            get => _agora ?? DateTimeOffset.UtcNow;
            set => _agora = value;
        }

        public SupabaseEmMemoria()
        {
            Handler.Responder = (chamada, _) => Task.FromResult(Responder(chamada));
        }

        public JsonObject Inserir(string tabela, JsonObject linha)
        {
            lock (_trava)
            {
                linha["id"] ??= Guid.NewGuid().ToString();
                linha["created_at"] ??= Agora.ToString("O");
                Tabela(tabela).Add(linha);
                return linha;
            }
        }

        public IReadOnlyList<JsonObject> Linhas(string tabela)
        {
            lock (_trava) return Tabela(tabela).Select(l => (JsonObject)l.DeepClone()).ToList();
        }

        private List<JsonObject> Tabela(string nome) =>
            Tabelas.TryGetValue(nome, out var t) ? t : Tabelas[nome] = new List<JsonObject>();

        private HttpResponseMessage Responder(SupabaseFake.Chamada chamada)
        {
            var falha = Falhar(chamada);
            if (falha != null && !Aplicar(chamada))
                return falha;

            HttpResponseMessage resposta;
            lock (_trava)
            {
                var caminho = chamada.Uri.AbsolutePath;
                resposta = caminho.StartsWith("/storage/v1/object/", StringComparison.Ordinal)
                    ? Storage(chamada, Uri.UnescapeDataString(caminho["/storage/v1/object/".Length..]))
                    : Rest(chamada, caminho["/rest/v1/".Length..]);
            }

            return falha ?? resposta;
        }

        private HttpResponseMessage Storage(SupabaseFake.Chamada chamada, string chave)
        {
            if (chamada.Metodo == HttpMethod.Post)
            {
                if (!Objetos.Add(chave))
                    return SupabaseFake.Json(HttpStatusCode.Conflict, "{\"error\":\"Duplicate\"}");
                return SupabaseFake.Json(HttpStatusCode.OK, $"{{\"Key\":\"{chave}\"}}");
            }

            if (chamada.Metodo == HttpMethod.Delete)
            {
                return Objetos.Remove(chave)
                    ? SupabaseFake.Json(HttpStatusCode.OK, "{}")
                    : SupabaseFake.Json(HttpStatusCode.NotFound, "{\"error\":\"not_found\"}");
            }

            throw new NotSupportedException($"Storage: {chamada.Metodo} não simulado.");
        }

        private HttpResponseMessage Rest(SupabaseFake.Chamada chamada, string tabela)
        {
            if (tabela.StartsWith("rpc/", StringComparison.Ordinal))
            {
                return Rpcs.TryGetValue(tabela[4..], out var rpc)
                    ? SupabaseFake.Json(HttpStatusCode.OK, rpc(this))
                    : SupabaseFake.Json(HttpStatusCode.NotFound, "{\"code\":\"PGRST202\"}");
            }

            var (filtros, ordem, limit, offset) = LerQuery(chamada.QueryCrua);
            var linhas = ordem == null ? Tabela(tabela) : ordem(Tabela(tabela)).ToList();

            if (chamada.Metodo == HttpMethod.Post)
            {
                var nova = JsonNode.Parse(chamada.Corpo)!.AsObject();
                Inserir(tabela, nova);
                return SupabaseFake.Json(HttpStatusCode.Created, new JsonArray(nova.DeepClone()).ToJsonString());
            }

            var casadas = linhas.Where(l => filtros.All(f => f(l))).ToList();

            if (chamada.Metodo == HttpMethod.Patch)
            {
                var alteracoes = JsonNode.Parse(chamada.Corpo)!.AsObject();
                foreach (var linha in casadas)
                    foreach (var (k, v) in alteracoes)
                        linha[k] = v?.DeepClone();
                return SupabaseFake.Json(HttpStatusCode.OK, new JsonArray(casadas.Select(l => (JsonNode)l.DeepClone()).ToArray()).ToJsonString());
            }

            if (chamada.Metodo == HttpMethod.Get)
            {
                var total = casadas.Count;
                var pagina = casadas.Skip(offset).Take(limit ?? int.MaxValue).Select(l => (JsonNode)l.DeepClone()).ToArray();
                return SupabaseFake.Json(HttpStatusCode.OK, new JsonArray(pagina).ToJsonString(), $"*/{total}");
            }

            throw new NotSupportedException($"PostgREST: {chamada.Metodo} não simulado.");
        }

        /// <summary>RPCs simuladas: nome → corpo JSON da resposta.</summary>
        public Dictionary<string, Func<SupabaseEmMemoria, string>> Rpcs { get; } = new();

        private static (List<Func<JsonObject, bool>> Filtros, Func<IEnumerable<JsonObject>, IEnumerable<JsonObject>> Ordem, int? Limit, int Offset)
            LerQuery(string query)
        {
            var filtros = new List<Func<JsonObject, bool>>();
            Func<IEnumerable<JsonObject>, IEnumerable<JsonObject>> ordem = null;
            int? limit = null;
            var offset = 0;

            foreach (var parte in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var i = parte.IndexOf('=');
                var chave = Uri.UnescapeDataString(parte[..i]);
                var valor = Uri.UnescapeDataString(parte[(i + 1)..]);

                switch (chave)
                {
                    case "select":
                        continue;
                    case "order":
                        ordem = Ordenar(valor);
                        continue;
                    case "limit":
                        limit = int.Parse(valor, CultureInfo.InvariantCulture);
                        continue;
                    case "offset":
                        offset = int.Parse(valor, CultureInfo.InvariantCulture);
                        continue;
                    case "or":
                        var alternativas = Alternativas(valor);
                        filtros.Add(l => alternativas.Any(a => a(l)));
                        continue;
                }

                filtros.Add(Condicao(chave, valor));
            }

            return (filtros, ordem, limit, offset);
        }

        private static Func<JsonObject, bool> Condicao(string coluna, string valor)
        {
            if (valor == "is.null")
                return l => l[coluna] == null;

            if (valor.StartsWith("eq.", StringComparison.Ordinal))
            {
                var esperado = Literal(valor[3..]);
                return l => l[coluna]?.ToString() == esperado;
            }

            if (valor.StartsWith("lt.", StringComparison.Ordinal) || valor.StartsWith("gte.", StringComparison.Ordinal))
            {
                var menor = valor.StartsWith("lt.", StringComparison.Ordinal);
                var limite = DateTimeOffset.Parse(valor[(menor ? 3 : 4)..], CultureInfo.InvariantCulture);
                return l =>
                {
                    var data = DateTimeOffset.Parse(l[coluna]!.ToString(), CultureInfo.InvariantCulture);
                    return menor ? data < limite : data >= limite;
                };
            }

            if (valor.StartsWith("ilike.", StringComparison.Ordinal))
            {
                var padrao = Literal(valor[6..]);
                var regex = new System.Text.RegularExpressions.Regex(
                    "^" + IlikeParaRegex(padrao) + "$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
                return l => l[coluna] != null && regex.IsMatch(l[coluna]!.ToString());
            }

            throw new NotSupportedException($"Filtro não simulado: {coluna}={valor}");
        }

        // "(a.ilike."*x*",b.ilike."*x*")": separa nas vírgulas que estão fora de aspas.
        private static List<Func<JsonObject, bool>> Alternativas(string valor)
        {
            if (!valor.StartsWith('(') || !valor.EndsWith(')'))
                throw new NotSupportedException($"or= sem parênteses: {valor}");

            var corpo = valor[1..^1];
            var partes = new List<string>();
            var atual = new System.Text.StringBuilder();
            var emAspas = false;

            for (var i = 0; i < corpo.Length; i++)
            {
                var c = corpo[i];
                if (emAspas && c == '\\' && i + 1 < corpo.Length)
                {
                    atual.Append(c).Append(corpo[++i]);
                    continue;
                }
                if (c == '"') emAspas = !emAspas;
                if (c == ',' && !emAspas)
                {
                    partes.Add(atual.ToString());
                    atual.Clear();
                    continue;
                }
                atual.Append(c);
            }
            partes.Add(atual.ToString());

            return partes.Select(p =>
            {
                var ponto = p.IndexOf('.');
                return Condicao(p[..ponto], p[(ponto + 1)..]);
            }).ToList();
        }

        // * e % são curingas; \x é o caractere x literal.
        private static string IlikeParaRegex(string padrao)
        {
            var regex = new System.Text.StringBuilder();
            for (var i = 0; i < padrao.Length; i++)
            {
                var c = padrao[i];
                if (c == '\\' && i + 1 < padrao.Length)
                    regex.Append(System.Text.RegularExpressions.Regex.Escape(padrao[++i].ToString()));
                else if (c is '*' or '%')
                    regex.Append(".*");
                else if (c == '_')
                    regex.Append('.');
                else
                    regex.Append(System.Text.RegularExpressions.Regex.Escape(c.ToString()));
            }
            return regex.ToString();
        }

        // Só a primeira coluna do order; o resto é desempate que o fake não precisa.
        private static Func<IEnumerable<JsonObject>, IEnumerable<JsonObject>> Ordenar(string valor)
        {
            var primeira = valor.Split(',')[0].Split('.');
            var coluna = primeira[0];
            var desc = primeira.Length > 1 && primeira[1] == "desc";
            Func<JsonObject, IComparable> chave = l => l[coluna] is JsonValue v && v.TryGetValue<int>(out var n)
                ? n
                : l[coluna]?.ToString();
            return linhas => desc ? linhas.OrderByDescending(chave) : linhas.OrderBy(chave);
        }

        // "..." com \" e \\ escapados, ou o valor cru.
        private static string Literal(string valor)
        {
            if (valor.Length < 2 || valor[0] != '"' || valor[^1] != '"')
                return valor;

            return valor[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
        }
    }
}
