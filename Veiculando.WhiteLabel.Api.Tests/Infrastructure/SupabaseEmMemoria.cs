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

        public DateTimeOffset Agora { get; set; } = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

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
            var (filtros, limit, offset) = LerQuery(chamada.QueryCrua);
            var linhas = Tabela(tabela);

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

        private static (List<Func<JsonObject, bool>> Filtros, int? Limit, int Offset) LerQuery(string query)
        {
            var filtros = new List<Func<JsonObject, bool>>();
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
                    case "order":
                        continue;
                    case "limit":
                        limit = int.Parse(valor, CultureInfo.InvariantCulture);
                        continue;
                    case "offset":
                        offset = int.Parse(valor, CultureInfo.InvariantCulture);
                        continue;
                }

                if (valor == "is.null")
                    filtros.Add(l => l[chave] == null);
                else if (valor.StartsWith("eq.", StringComparison.Ordinal))
                {
                    var esperado = Literal(valor[3..]);
                    filtros.Add(l => l[chave]?.ToString() == esperado);
                }
                else if (valor.StartsWith("lt.", StringComparison.Ordinal))
                {
                    var teto = DateTimeOffset.Parse(valor[3..], CultureInfo.InvariantCulture);
                    filtros.Add(l => DateTimeOffset.Parse(l[chave]!.ToString(), CultureInfo.InvariantCulture) < teto);
                }
                else
                    throw new NotSupportedException($"Filtro não simulado: {chave}={valor}");
            }

            return (filtros, limit, offset);
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
