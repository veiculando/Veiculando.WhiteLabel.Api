using System;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Veiculando.WhiteLabel.Api.Middleware;

namespace Veiculando.WhiteLabel.Api.Services.Cms;

/// <summary>Valores de <c>cms_auditoria.acao</c>.</summary>
public static class CmsAcao
{
    public const string Criar = "criar";
    public const string Editar = "editar";
    public const string Ativar = "ativar";
    public const string Inativar = "inativar";
    public const string TrocarArquivo = "trocar_arquivo";
}

/// <summary>Quem fez a escrita, lido do servidor.</summary>
/// <remarks>
/// Os três campos saem do token e do tenant resolvido pelo Host, nunca de
/// formulário, query ou header: uma auditoria que aceita o autor do cliente não
/// prova nada.
/// </remarks>
public sealed class CmsAutor
{
    public int WlUsuarioId { get; init; }
    public string Email { get; init; }
    public int AfiliadaId { get; init; }

    /// <summary>
    /// Falso quando falta claim ou tenant. O controller responde 401 ANTES de
    /// escrever: uma escrita sem autor não teria como ser auditada.
    /// </summary>
    public static bool TentarLer(ClaimsPrincipal usuario, ITenantContext tenant, out CmsAutor autor)
    {
        autor = null;

        if (usuario?.Identity?.IsAuthenticated != true || tenant is not { Resolvido: true, AfiliadaId: > 0 })
            return false;

        if (!int.TryParse(usuario.FindFirstValue("WlUsuarioId"), out var wlUsuarioId) || wlUsuarioId <= 0)
            return false;

        var email = usuario.FindFirstValue(ClaimTypes.Email);
        if (string.IsNullOrWhiteSpace(email))
            return false;

        autor = new CmsAutor { WlUsuarioId = wlUsuarioId, Email = email, AfiliadaId = tenant.AfiliadaId };
        return true;
    }
}

/// <summary>Linha de <c>cms_auditoria</c> (colunas do TP-2, task 105a).</summary>
public sealed class CmsAuditoriaLinha
{
    public Guid? Id { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public int WlUsuarioId { get; set; }
    public string UsuarioEmail { get; set; }
    public int AfiliadaId { get; set; }
    public string Acao { get; set; }
    public string Recurso { get; set; }
    public Guid RecursoId { get; set; }
    public JsonElement? Antes { get; set; }
    public JsonElement? Depois { get; set; }

    /// <summary>
    /// Só em <c>trocar_arquivo</c>: null = o arquivo antigo ainda precisa ser
    /// removido pela varredura.
    /// </summary>
    public DateTimeOffset? ArquivoLimpoEm { get; set; }
}

public interface ICmsAuditoria
{
    /// <summary>
    /// Grava a linha de auditoria. Nunca lança: a mutação já aconteceu e não há
    /// como desfazê-la.
    /// </summary>
    Task RegistrarAsync(CmsAutor autor, string acao, string recurso, Guid recursoId,
        object antes, object depois, DateTimeOffset? arquivoLimpoEm = null);
}

/// <summary>
/// Histórico das escritas do CMS em <c>cms_auditoria</c>, no Supabase.
/// </summary>
/// <remarks>
/// <para><b>Por que no Supabase e não no SQL do Core.</b> O dado auditado está
/// lá, e o módulo guarda o próprio histórico, como <c>AfiliadaConfiguracaoHistorico</c>
/// faz no Core. Não existe <c>WL_AuditLog</c> genérico no BFF.</para>
///
/// <para><b>Falha depois da mutação.</b> Não há transação entre a tabela de conteúdo
/// e a de auditoria. Se o INSERT da auditoria falhar, a escrita fica (a resposta é
/// 200) e o registro completo vai para o log como Error, para ser reconstruído.
/// Nunca em silêncio.</para>
///
/// <para>A gravação usa <see cref="CancellationToken.None"/>: se o navegador fechar
/// depois da mutação, a auditoria ainda precisa ser gravada.</para>
/// </remarks>
public sealed class CmsAuditoria : ICmsAuditoria
{
    private readonly ISupabaseCmsClient _supabase;
    private readonly ILogger<CmsAuditoria> _logger;

    public CmsAuditoria(ISupabaseCmsClient supabase, ILogger<CmsAuditoria> logger)
    {
        _supabase = supabase;
        _logger = logger;
    }

    public async Task RegistrarAsync(CmsAutor autor, string acao, string recurso, Guid recursoId,
        object antes, object depois, DateTimeOffset? arquivoLimpoEm = null)
    {
        var linha = new CmsAuditoriaLinha
        {
            WlUsuarioId = autor.WlUsuarioId,
            UsuarioEmail = autor.Email,
            AfiliadaId = autor.AfiliadaId,
            Acao = acao,
            Recurso = recurso,
            RecursoId = recursoId,
            Antes = ParaJson(antes),
            Depois = ParaJson(depois),
            ArquivoLimpoEm = arquivoLimpoEm,
        };

        try
        {
            await _supabase.InserirAsync<CmsAuditoriaLinha>(CmsSupabase.Auditoria, ParaInsert(linha), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Falha ao gravar cms_auditoria depois da mutação; a escrita NÃO foi desfeita. Registro para reconstrução: {Registro}",
                JsonSerializer.Serialize(ParaInsert(linha), SupabaseCmsClient.Json));
        }
    }

    // Id e created_at ficam de fora: o banco gera. Mandar null neles quebraria o
    // default de gen_random_uuid() e de now().
    private static object ParaInsert(CmsAuditoriaLinha l) => new
    {
        wl_usuario_id = l.WlUsuarioId,
        usuario_email = l.UsuarioEmail,
        afiliada_id = l.AfiliadaId,
        acao = l.Acao,
        recurso = l.Recurso,
        recurso_id = l.RecursoId,
        antes = l.Antes,
        depois = l.Depois,
        arquivo_limpo_em = l.ArquivoLimpoEm,
    };

    private static JsonElement? ParaJson(object valor) =>
        valor == null ? null : JsonSerializer.SerializeToElement(valor, valor.GetType(), SupabaseCmsClient.Json);
}
