using System;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Veiculando.WhiteLabel.Api.Services.Cms;

/// <summary>
/// Seção <c>Cms</c> da configuração, como vem do ambiente
/// (<c>Cms__SupabaseUrl</c>, <c>Cms__ServiceRoleKey</c>, <c>Cms__SiteUrl</c> e
/// <c>Cms__AfiliadasHabilitadas__0</c>). Ninguém lê isto direto: o valor
/// normalizado está em <see cref="CmsConfiguracao"/>.
/// </summary>
public sealed class CmsOptions
{
    public const string Secao = "Cms";

    public string SupabaseUrl { get; set; }
    public string ServiceRoleKey { get; set; }
    public string SiteUrl { get; set; }

    /// <summary>Códigos (<c>Afiliada.Codigo</c>) das afiliadas que carregam o módulo.</summary>
    public string[] AfiliadasHabilitadas { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Configuração do CMS já normalizada, calculada uma vez na subida.
/// </summary>
/// <remarks>
/// <para><b>Por que normalizar aqui.</b> O <c>CMS_SITE_URL</c> do preview termina
/// em <c>/</c>, e o front monta <c>{cmsSiteUrl}/ofertas/{id}</c>: sem o
/// <c>TrimEnd('/')</c> sairia <c>...vercel.app//ofertas/{id}</c>. A URL do
/// Supabase recebe o mesmo tratamento, porque o client concatena caminhos nela.</para>
///
/// <para><b>Por que singleton.</b> Um <c>SiteUrl</c> inválido gera um Warning. Por
/// requisição, ele inundaria o log; na subida, aparece uma vez, que é quando
/// alguém está olhando.</para>
///
/// <para><b>A service role key</b> dá escrita irrestrita no Supabase que a LP de
/// produção lê (ADR-CMS-004). Ela só sai daqui para o header do client: nunca
/// para log, resposta ou front. Por isso o <see cref="ToString"/> não a inclui.</para>
/// </remarks>
public sealed class CmsConfiguracao
{
    public CmsConfiguracao(IOptions<CmsOptions> options, ILogger<CmsConfiguracao> logger)
    {
        var valor = options.Value ?? new CmsOptions();

        SupabaseUrl = SemBarraFinal(valor.SupabaseUrl);
        ServiceRoleKey = string.IsNullOrWhiteSpace(valor.ServiceRoleKey) ? null : valor.ServiceRoleKey.Trim();
        ChavesPresentes = SupabaseUrl != null && ServiceRoleKey != null;

        SiteUrl = SemBarraFinal(valor.SiteUrl);
        if (SiteUrl != null && !EhHttpsAbsoluta(SiteUrl))
        {
            logger.LogWarning(
                "Cms:SiteUrl ignorado: precisa ser uma URL https absoluta. O branding vai expor cmsSiteUrl null.");
            SiteUrl = null;
        }

        // Aceita também "A,B" num único item: o compose do preview passa só o
        // índice 0, e separar por vírgula permite habilitar uma segunda afiliada
        // sem mexer no compose.
        AfiliadasHabilitadas = (valor.AfiliadasHabilitadas ?? Array.Empty<string>())
            .Where(x => x != null)
            .SelectMany(x => x.Split(','))
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>URL do projeto Supabase, sem barra final. Null sem configuração.</summary>
    public string SupabaseUrl { get; }

    /// <summary>Chave de serviço. Uso exclusivo do client do Supabase.</summary>
    public string ServiceRoleKey { get; }

    /// <summary>URL pública da LP, https e sem barra final. Null se ausente ou inválida.</summary>
    public string SiteUrl { get; }

    /// <summary>URL e chave presentes. Sem as duas, o módulo fica desligado em todas as afiliadas.</summary>
    public bool ChavesPresentes { get; }

    /// <summary>Códigos de afiliada habilitados, sem espaços nem vazios.</summary>
    public string[] AfiliadasHabilitadas { get; }

    /// <summary>Compara com <c>Trim()</c> e sem diferenciar maiúsculas.</summary>
    public bool AfiliadaHabilitada(string codigo) =>
        !string.IsNullOrWhiteSpace(codigo)
        && AfiliadasHabilitadas.Contains(codigo.Trim(), StringComparer.OrdinalIgnoreCase);

    public override string ToString() =>
        $"CmsConfiguracao {{ ChavesPresentes = {ChavesPresentes}, SupabaseUrl = {SupabaseUrl}, " +
        $"SiteUrl = {SiteUrl}, AfiliadasHabilitadas = [{string.Join(", ", AfiliadasHabilitadas)}] }}";

    private static string SemBarraFinal(string valor)
    {
        if (string.IsNullOrWhiteSpace(valor))
            return null;

        var normalizado = valor.Trim().TrimEnd('/');
        return normalizado.Length == 0 ? null : normalizado;
    }

    private static bool EhHttpsAbsoluta(string valor) =>
        Uri.TryCreate(valor, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
