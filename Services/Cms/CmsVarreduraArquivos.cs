using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Veiculando.WhiteLabel.Api.Services.Cms;

/// <summary>
/// Remove os arquivos antigos de trocas feitas há mais de 10 minutos (VEI-RD-19f).
/// </summary>
/// <remarks>
/// <para><b>Fila.</b> Não há tabela nova: a fila são as linhas
/// <c>cms_auditoria</c> com <c>acao = trocar_arquivo</c> e
/// <c>arquivo_limpo_em is null</c>, com o índice parcial criado pelo TP-2.</para>
///
/// <para><b>Por que 10 minutos.</b> A LP revalida o HTML a cada 300 s. Com o dobro,
/// nenhuma página em cache ainda aponta para o arquivo quando ele sai.</para>
///
/// <para><b>Por que em QUALQUER escrita do CMS.</b> Rodar só na próxima escrita do
/// mesmo registro deixaria órfão permanente o arquivo de um registro que não for
/// mais editado. O critério revisado do card é "sem órfão permanente": o antigo
/// pode ficar até a primeira escrita do CMS, de qualquer recurso, depois de 10
/// minutos.</para>
///
/// <para><b>Teto de 20 por rodada</b>, para uma fila acumulada não virar uma rajada
/// de chamadas ao Supabase.</para>
/// </remarks>
public sealed class CmsVarreduraArquivos
{
    public static readonly TimeSpan Carencia = TimeSpan.FromMinutes(10);
    public const int Teto = 20;

    private static readonly string[] ColunasDeArquivo = { "image_url", "avatar_url", "html_path" };

    private readonly ISupabaseCmsClient _supabase;
    private readonly CmsObjetos _objetos;
    private readonly TimeProvider _relogio;
    private readonly ILogger<CmsVarreduraArquivos> _logger;

    public CmsVarreduraArquivos(ISupabaseCmsClient supabase, CmsObjetos objetos, TimeProvider relogio,
        ILogger<CmsVarreduraArquivos> logger)
    {
        _supabase = supabase;
        _objetos = objetos;
        _relogio = relogio;
        _logger = logger;
    }

    /// <summary>Processa até <see cref="Teto"/> pendências. Devolve quantas marcou como limpas.</summary>
    public async Task<int> VarrerAsync(CancellationToken ct)
    {
        // UTC com "Z": um offset "+00:00" teria o "+" lido como espaço na query.
        var limite = (_relogio.GetUtcNow() - Carencia).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        var filtro = "select=id,antes"
                     + $"&acao=eq.{CmsAcao.TrocarArquivo}"
                     + "&arquivo_limpo_em=is.null"
                     + $"&created_at=lt.{limite}"
                     + "&order=created_at.asc"
                     + $"&limit={Teto}";

        CmsAuditoriaLinha[] pendentes;
        try
        {
            pendentes = (await _supabase.SelecionarAsync<CmsAuditoriaLinha>(CmsSupabase.Auditoria, filtro, ct)).ToArray();
        }
        catch (CmsIndisponivelException ex)
        {
            _logger.LogWarning(ex, "Varredura de arquivos do CMS não conseguiu ler a fila; tenta na próxima escrita.");
            return 0;
        }

        var limpos = 0;
        foreach (var pendente in pendentes)
        {
            if (pendente.Id == null)
                continue;

            var antigo = ObjetoAntigo(pendente);
            try
            {
                // Sem objeto derivável (coluna vazia, URL fora do bucket): não há o
                // que apagar, só tirar da fila. Ainda referenciado: o arquivo tem
                // outro dono e fica; a pendência desta troca acabou.
                if (antigo != null)
                    await _objetos.RemoverSeLivreAsync(antigo, ct);

                await _supabase.AtualizarAsync<CmsAuditoriaLinha>(CmsSupabase.Auditoria,
                    PostgrestFiltro.PorId(pendente.Id.Value),
                    new { arquivo_limpo_em = _relogio.GetUtcNow() }, ct);
                limpos++;
            }
            catch (CmsIndisponivelException ex)
            {
                // Fica pendente: a próxima escrita tenta de novo.
                _logger.LogWarning(ex, "Varredura: falha ao limpar {Bucket}/{Caminho} (auditoria {Id}); segue pendente.",
                    antigo?.Bucket, antigo?.Caminho, pendente.Id);
            }
        }

        return limpos;
    }

    private CmsObjeto ObjetoAntigo(CmsAuditoriaLinha linha)
    {
        if (linha.Antes is not { ValueKind: JsonValueKind.Object } antes)
            return null;

        foreach (var coluna in ColunasDeArquivo)
        {
            if (antes.TryGetProperty(coluna, out var valor) && valor.ValueKind == JsonValueKind.String)
                return _objetos.DaColuna(coluna, valor.GetString());
        }

        return null;
    }
}

/// <summary>Pede uma varredura sem esperar por ela.</summary>
public interface ICmsVarreduraAgendador
{
    /// <summary>Não bloqueia. Pedidos enquanto uma varredura roda viram uma só rodada seguinte.</summary>
    void Solicitar();
}

/// <summary>
/// Roda a <see cref="CmsVarreduraArquivos"/> fora da requisição.
/// </summary>
/// <remarks>
/// A escrita do operador não espera a limpeza: o controller chama
/// <see cref="Solicitar"/> e responde. A fila tem capacidade 1: dez escritas
/// seguidas geram no máximo uma rodada em curso e uma na espera, não dez.
/// </remarks>
public sealed class CmsVarreduraAgendador : BackgroundService, ICmsVarreduraAgendador
{
    /// <summary>Teto de uma rodada inteira (até 20 itens, algumas chamadas cada).</summary>
    public static readonly TimeSpan TempoMaximo = TimeSpan.FromMinutes(2);

    private readonly Channel<bool> _pedidos = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private readonly IServiceScopeFactory _escopos;
    private readonly ILogger<CmsVarreduraAgendador> _logger;

    public CmsVarreduraAgendador(IServiceScopeFactory escopos, ILogger<CmsVarreduraAgendador> logger)
    {
        _escopos = escopos;
        _logger = logger;
    }

    public void Solicitar() => _pedidos.Writer.TryWrite(true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var _ in _pedidos.Reader.ReadAllAsync(stoppingToken))
        {
            using var limite = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            limite.CancelAfter(TempoMaximo);

            try
            {
                using var escopo = _escopos.CreateScope();
                await escopo.ServiceProvider.GetRequiredService<CmsVarreduraArquivos>().VarrerAsync(limite.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Varredura de arquivos do CMS interrompida; tenta na próxima escrita.");
            }
        }
    }
}
