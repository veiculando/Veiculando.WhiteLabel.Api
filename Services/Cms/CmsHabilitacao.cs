using System;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Veiculando.Data.Contexts;

namespace Veiculando.WhiteLabel.Api.Services.Cms;

/// <summary>
/// Decide se o módulo CMS está ligado para a afiliada da requisição.
/// </summary>
public interface ICmsHabilitacao
{
    Task<bool> HabilitadoAsync(int afiliadaId);
}

/// <summary>
/// <c>habilitado = chaves presentes && Afiliada.Codigo ∈ Cms:AfiliadasHabilitadas</c>.
/// </summary>
/// <remarks>
/// <para><b>Por que por afiliada, e não só pelas chaves.</b> O BFF é um só para
/// todas as exibidoras: o tenant sai do Host da requisição. As chaves do Supabase
/// estão no ambiente, logo valem para todas. E o grant de <c>ConteudoGerenciar</c>
/// (TP-1) vai para todo admin de toda afiliada. Com um flag só pelas chaves, o
/// admin de outra exibidora gravaria no site da Aurum. A ADR-CMS-004 exige que as
/// outras exibidoras nem carreguem o módulo.</para>
///
/// <para><b>Por que o código, e não o id.</b> O id da afiliada muda entre preview
/// e produção; o código (<c>PRVIEW</c>, por exemplo) é o que o owner conhece e
/// cadastra no Snaps.</para>
///
/// <para>O mesmo cálculo alimenta o branding (<c>cmsHabilitado</c>) e o 404 do
/// <see cref="Middleware.CmsModuloMiddleware"/>, para o front nunca mostrar um
/// menu que a API recusa.</para>
/// </remarks>
public sealed class CmsHabilitacao : ICmsHabilitacao
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private readonly CmsConfiguracao _config;
    private readonly VeiculandoDataContext _db;
    private readonly IMemoryCache _cache;

    public CmsHabilitacao(CmsConfiguracao config, VeiculandoDataContext db, IMemoryCache cache)
    {
        _config = config;
        _db = db;
        _cache = cache;
    }

    public async Task<bool> HabilitadoAsync(int afiliadaId)
    {
        // Sem chaves ou sem lista, nem consulta o banco: é o caso de toda
        // instância que não é a da Aurum.
        if (!_config.ChavesPresentes || _config.AfiliadasHabilitadas.Length == 0 || afiliadaId <= 0)
            return false;

        return _config.AfiliadaHabilitada(await CodigoAsync(afiliadaId));
    }

    private async Task<string> CodigoAsync(int afiliadaId)
    {
        var chave = $"CmsAfiliadaCodigo:{afiliadaId}";
        if (_cache.TryGetValue(chave, out string cacheado))
            return cacheado;

        var codigo = await _db.Afiliadas
            .AsNoTracking()
            .Where(x => x.Id == afiliadaId)
            .Select(x => x.Codigo)
            .SingleOrDefaultAsync();

        if (codigo != null)
            _cache.Set(chave, codigo, CacheDuration);

        return codigo;
    }
}
