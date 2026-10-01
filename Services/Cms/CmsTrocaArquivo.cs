using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Veiculando.WhiteLabel.Api.Services.Cms;

/// <summary>
/// Uma coluna de arquivo que muda no PUT: um arquivo novo, ou <c>Arquivo = null</c>
/// para esvaziá-la (banner que deixa de ser html perde o <c>html_path</c>).
/// </summary>
/// <param name="Recurso">Prefixo do objeto no bucket (<c>banners</c>, <c>marcas</c>, <c>depoimentos</c>).</param>
public sealed record CmsTroca(string Coluna, string Bucket, string Recurso, CmsArquivo Arquivo);

/// <summary>Resultado da atualização: a linha gravada, ou não encontrada.</summary>
public sealed class CmsAtualizacao<T>
{
    public T Linha { get; init; }
    public bool NaoEncontrada => Linha == null;
}

/// <summary>
/// PUT com troca de arquivo: grava o novo, atualiza a linha e cuida do antigo
/// (VEI-RD-19f, decisão do owner de 30/09/2026: remoção ADIADA).
/// </summary>
/// <remarks>
/// <para><b>Ordem.</b> O arquivo novo sobe antes do PATCH, porque a linha não pode
/// apontar para um objeto que ainda não existe. O antigo só sai depois do PATCH,
/// porque a linha não pode apontar para um objeto que já sumiu.</para>
///
/// <para><b>PATCH que falha ou estoura o timeout.</b> Timeout é ambíguo: o PATCH
/// pode ter sido aplicado. A linha é relida, e o objeto novo só é removido se ela
/// NÃO o referencia. Se a releitura também falhar, nada é removido e o caminho do
/// novo vai para o log: um órfão é recuperável, uma imagem apagada de uma linha
/// que a aponta não é.</para>
///
/// <para><b>O antigo.</b> A LP guarda o HTML por 300 s (<c>revalidate</c>). Apagar o
/// antigo na hora, num registro que aparece no site, deixa imagem quebrada em
/// produção até a revalidação. Por isso:</para>
/// <list type="bullet">
/// <item>registro ativo antes OU depois da troca: o antigo fica, e a linha
/// <c>trocar_arquivo</c> nasce com <c>arquivo_limpo_em = null</c>. A
/// <see cref="CmsVarreduraArquivos"/> remove depois de 10 minutos. "Antes" conta
/// porque a página em cache da LP ainda aponta para o antigo;</item>
/// <item>inativo antes E depois: o site não o mostra, e o antigo sai na hora.</item>
/// </list>
/// <para>Nos dois caminhos, o objeto só sai se nenhuma linha atual o referencia
/// (<see cref="CmsObjetos.RemoverSeLivreAsync"/>). Falha ao remover nunca vira
/// erro para o operador: vira Warning, e a linha fica pendente para a varredura.</para>
/// </remarks>
public sealed class CmsTrocaArquivo
{
    private readonly ISupabaseCmsClient _supabase;
    private readonly CmsObjetos _objetos;
    private readonly ICmsAuditoria _auditoria;
    private readonly TimeProvider _relogio;
    private readonly ILogger<CmsTrocaArquivo> _logger;

    public CmsTrocaArquivo(ISupabaseCmsClient supabase, CmsObjetos objetos, ICmsAuditoria auditoria,
        TimeProvider relogio, ILogger<CmsTrocaArquivo> logger)
    {
        _supabase = supabase;
        _objetos = objetos;
        _auditoria = auditoria;
        _relogio = relogio;
        _logger = logger;
    }

    /// <param name="recurso">Valor de <c>cms_auditoria.recurso</c> (o nome da tabela).</param>
    /// <param name="atual">A linha como estava antes do PUT, lida pelo controller.</param>
    /// <param name="alteracoes">Colunas que não são de arquivo, em snake_case. As de
    /// arquivo são acrescentadas aqui, a partir de <paramref name="trocas"/>.</param>
    /// <exception cref="CmsIndisponivelException">Upload ou PATCH falhou e não foi
    /// aplicado; os arquivos novos já foram removidos.</exception>
    public async Task<CmsAtualizacao<T>> AtualizarAsync<T>(string recurso, Guid id, T atual,
        IDictionary<string, object> alteracoes, IReadOnlyList<CmsTroca> trocas, CmsAutor autor, CancellationToken ct)
        where T : class
    {
        var patch = new Dictionary<string, object>(alteracoes);
        var enviados = new List<CmsObjeto>();

        try
        {
            foreach (var troca in trocas)
            {
                if (troca.Arquivo == null)
                {
                    patch[troca.Coluna] = null;
                    continue;
                }

                var objeto = new CmsObjeto(troca.Bucket, CmsArquivos.NomeObjeto(troca.Recurso, troca.Arquivo.Extensao));
                using var conteudo = new MemoryStream(troca.Arquivo.Conteudo, writable: false);
                await _supabase.EnviarObjetoAsync(objeto.Bucket, objeto.Caminho, conteudo, troca.Arquivo.ContentType, ct);

                enviados.Add(objeto);
                patch[troca.Coluna] = _objetos.ValorDaColuna(objeto);
            }
        }
        catch
        {
            // Uma imagem subiu e o HTML não: a linha nem chegou a mudar.
            await RemoverEnviadosAsync(enviados, "upload interrompido");
            throw;
        }

        T gravada;
        try
        {
            var linhas = await _supabase.AtualizarAsync<T>(recurso, PostgrestFiltro.PorId(id), patch, ct);
            if (linhas.Count == 0)
            {
                await RemoverEnviadosAsync(enviados, "linha não encontrada no PATCH");
                return new CmsAtualizacao<T>();
            }

            gravada = linhas[0];
        }
        catch (CmsIndisponivelException)
        {
            gravada = await ReconciliarAsync<T>(recurso, id, patch, trocas, enviados);
            if (gravada == null)
                throw;
        }

        await TratarAntigosAsync(recurso, id, atual, gravada, trocas, autor);
        return new CmsAtualizacao<T> { Linha = gravada };
    }

    /// <summary>
    /// Depois de um PATCH sem resposta: relê a linha. Devolve a linha se o PATCH
    /// foi aplicado; senão remove o que subiu e devolve null.
    /// </summary>
    private async Task<T> ReconciliarAsync<T>(string recurso, Guid id, IReadOnlyDictionary<string, object> patch,
        IReadOnlyList<CmsTroca> trocas, List<CmsObjeto> enviados)
        where T : class
    {
        IReadOnlyList<T> relidas;
        try
        {
            relidas = await _supabase.SelecionarAsync<T>(recurso, $"select=*&{PostgrestFiltro.PorId(id)}", CancellationToken.None);
        }
        catch (Exception ex)
        {
            if (enviados.Count > 0)
                _logger.LogWarning(ex,
                    "PATCH de {Recurso} {Id} sem resposta e a releitura falhou; arquivos novos mantidos para não quebrar a linha: {Objetos}",
                    recurso, id, Descrever(enviados));
            return null;
        }

        // Só dá para afirmar que o PATCH entrou olhando as colunas de arquivo: são
        // as únicas com valor novo garantido (um uuid que não existia antes).
        var linha = relidas.FirstOrDefault();
        var aplicado = linha != null && trocas.Count > 0 && trocas.All(t =>
            string.Equals(Coluna(linha, t.Coluna), patch[t.Coluna] as string, StringComparison.Ordinal));

        if (aplicado)
            return linha;

        await RemoverEnviadosAsync(enviados, "PATCH não aplicado");
        return null;
    }

    private async Task TratarAntigosAsync<T>(string recurso, Guid id, T atual, T gravada,
        IReadOnlyList<CmsTroca> trocas, CmsAutor autor)
    {
        var visivel = Ativo(atual) || Ativo(gravada);

        foreach (var troca in trocas)
        {
            var valorAntigo = Coluna(atual, troca.Coluna);
            var valorNovo = Coluna(gravada, troca.Coluna);
            if (string.Equals(valorAntigo, valorNovo, StringComparison.Ordinal))
                continue;

            var antigo = _objetos.DaColuna(troca.Coluna, valorAntigo);
            DateTimeOffset? limpoEm = null;

            if (antigo == null)
            {
                // Coluna vazia ou URL fora deste bucket: nada a limpar.
                limpoEm = _relogio.GetUtcNow();
            }
            else if (!visivel)
            {
                try
                {
                    await _objetos.RemoverSeLivreAsync(antigo, CancellationToken.None);
                    limpoEm = _relogio.GetUtcNow();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Troca em {Recurso} {Id}: falha ao remover o arquivo antigo {Bucket}/{Caminho}; fica para a varredura.",
                        recurso, id, antigo.Bucket, antigo.Caminho);
                }
            }

            // A linha trocar_arquivo é também a fila da varredura: o "antes" guarda
            // a coluna e o valor antigo, de onde o objeto é derivado de novo.
            await _auditoria.RegistrarAsync(autor, CmsAcao.TrocarArquivo, recurso, id,
                new Dictionary<string, string> { [troca.Coluna] = valorAntigo },
                new Dictionary<string, string> { [troca.Coluna] = valorNovo },
                limpoEm);
        }
    }

    private async Task RemoverEnviadosAsync(List<CmsObjeto> enviados, string motivo)
    {
        foreach (var objeto in enviados)
        {
            try
            {
                await _supabase.RemoverObjetoAsync(objeto.Bucket, objeto.Caminho, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Arquivo novo órfão ({Motivo}): {Bucket}/{Caminho}", motivo, objeto.Bucket, objeto.Caminho);
            }
        }
    }

    private static string Coluna<T>(T linha, string coluna)
    {
        if (linha == null) return null;
        var json = JsonSerializer.SerializeToElement(linha, SupabaseCmsClient.Json);
        return json.TryGetProperty(coluna, out var valor) && valor.ValueKind == JsonValueKind.String ? valor.GetString() : null;
    }

    private static bool Ativo<T>(T linha)
    {
        if (linha == null) return false;
        var json = JsonSerializer.SerializeToElement(linha, SupabaseCmsClient.Json);
        return json.TryGetProperty("is_active", out var valor) && valor.ValueKind == JsonValueKind.True;
    }

    private static string Descrever(IEnumerable<CmsObjeto> objetos) =>
        string.Join(", ", objetos.Select(o => $"{o.Bucket}/{o.Caminho}"));
}
