using System;
using System.Data.Entity;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities;
using Veiculando.Domain.Entities.WhiteLabel;
using Veiculando.Domain.Enums;
using Veiculando.WhiteLabel.Api.Middleware;

namespace Veiculando.WhiteLabel.Api.Services;

public enum ModoSessaoApp
{
    /// <summary>Login comum do anunciante, ou Prospecção aberta já com anunciante escolhido.</summary>
    Comum,

    /// <summary>Prospecção sem anunciante: a vendedora vende só para a própria carteira.</summary>
    Carteira,

    /// <summary>Diz ser Prospecção, mas o resgate não confere. Nunca vira venda "da casa".</summary>
    Recusada
}

public sealed record SessaoApp(ModoSessaoApp Modo, int VendedorId)
{
    public static readonly SessaoApp Comum = new(ModoSessaoApp.Comum, 0);
    public static readonly SessaoApp Recusada = new(ModoSessaoApp.Recusada, 0);
    public const string MensagemRecusada = "Sessão de prospecção não confirmada. Abra a Prospecção de novo pelo painel.";
}

/// <summary>
/// Regras da carteira da vendedora na Prospecção (VEI-RD-157), num lugar só.
/// </summary>
/// <remarks>
/// <para>Contexto, criação de campanha, cotação e checkout usam esta classe: a mesma
/// regra em quatro lugares, uma implementação (matriz do plano estratégico da Sprint 17.0).</para>
///
/// <para><b>Sessão (peer review I1).</b> A sessão sem anunciante abre como o anunciante
/// da casa, que tem onboarding "ad". Sem esta checagem, qualquer falha em achar o
/// resgate cairia no ramo "ad" e venderia como a própria exibidora. Por isso a decisão
/// vem ANTES do ramo por TipoConta, e "não confere" é <see cref="ModoSessaoApp.Recusada"/>.
/// A vendedora vem do resgate gravado no banco, nunca de valor do cliente.</para>
///
/// <para><b>CNPJ (I5).</b> O Core guarda <c>Cnpj.Numero</c> às vezes com máscara. A
/// comparação por dígitos acontece no SQL (REPLACE), não na memória.</para>
/// </remarks>
public sealed class CarteiraProspeccao
{
    private readonly VeiculandoDataContext _db;
    private readonly ITenantQueries _tenant;

    public CarteiraProspeccao(VeiculandoDataContext db, ITenantQueries tenant) => (_db, _tenant) = (db, tenant);

    public async Task<SessaoApp> ResolverAsync(ClaimsPrincipal user, WlUsuarioAnunciante comprador, CancellationToken ct)
    {
        var prospeccao = user.FindFirstValue("Prospeccao") == "true";
        var casa = comprador != null && EhCasa(comprador);
        if (!prospeccao && !casa) return SessaoApp.Comum;

        if (!Guid.TryParseExact(user.FindFirstValue("jti"), "N", out var sessaoId)) return SessaoApp.Recusada;
        var resgate = await _tenant.ProspeccaoEventos.AsNoTracking().SingleOrDefaultAsync(e =>
            e.AppSessaoId == sessaoId && e.Evento == WlProspeccaoEventoTipo.Resgatada, ct);
        if (resgate == null || comprador == null || resgate.AnuncianteId != comprador.Id) return SessaoApp.Recusada;

        // Prospecção aberta já com um anunciante escolhido continua como antes.
        if (!casa) return SessaoApp.Comum;
        return new SessaoApp(ModoSessaoApp.Carteira, resgate.OperadorId);
    }

    public bool EhCasa(WlUsuarioAnunciante comprador) =>
        string.Equals(comprador.Email?.Endereco, AnuncianteDaCasaProvisionamento.EmailDe(_tenant.AfiliadaId),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Clientes que a vendedora pode usar agora: carteira aprovada em nome dela, vínculo
    /// ativo com a exibidora e contrato vigente com a agência de venda direta.
    /// </summary>
    public IQueryable<int> ClientesLiberados(int vendedorId, int agenciaVendaDiretaId)
    {
        var afiliadaId = _tenant.AfiliadaId;
        var agora = DateTime.UtcNow;
        return _db.WlCarteiraClientes
            .Where(c => c.AfiliadaId == afiliadaId && c.VendedorId == vendedorId && c.Status == WlCarteiraStatus.Aprovado &&
                _db.AfiliadaClientes.Any(v => v.IdAfiliada == afiliadaId && v.IdCliente == c.ClienteId &&
                    v.Status == StatusVinculoEnum.Ativo) &&
                _db.AgenciaClientes.Any(l => l.IdAgencia == agenciaVendaDiretaId && l.IdCliente == c.ClienteId &&
                    l.StatusExibicao == StatusExibicaoEnum.Ativo &&
                    l.DataInicioContrato <= agora && l.DataExpiracaoContrato >= agora))
            .Select(c => c.ClienteId);
    }

    /// <summary>
    /// Código novo de Cliente: o Core exige letra no início, até 6 caracteres e unicidade
    /// (UK_Cliente_Codigo). "W" marca a origem WhiteLabel; o resto é aleatório.
    /// </summary>
    public async Task<string> NovoCodigoClienteAsync(CancellationToken ct)
    {
        const string alfabeto = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        for (var tentativa = 0; tentativa < 10; tentativa++)
        {
            var codigo = "W" + string.Concat(Enumerable.Range(0, Cliente.CodigoMaxLength - 1)
                .Select(_ => alfabeto[System.Security.Cryptography.RandomNumberGenerator.GetInt32(alfabeto.Length)]));
            if (!await _db.Clientes.AnyAsync(c => c.Codigo == codigo, ct)) return codigo;
        }
        throw new InvalidOperationException("Não foi possível gerar um código de cliente livre.");
    }

    public static string Digitos(string valor) =>
        new string((valor ?? string.Empty).Where(char.IsDigit).ToArray());

    /// <summary>Clientes do Core com este CNPJ, comparando só os dígitos, no SQL.</summary>
    public IQueryable<Cliente> ClientesPorCnpj(string digitos) =>
        _db.Clientes.Where(c => c.Cnpj.Numero.Replace(".", "").Replace("/", "").Replace("-", "").Replace(" ", "") == digitos);
}
