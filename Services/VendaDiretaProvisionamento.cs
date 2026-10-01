using System.Data.Entity;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities;
using Veiculando.Domain.Enums;
using Veiculando.Domain.Services;

namespace Veiculando.WhiteLabel.Api.Services;

/// <summary>
/// Agência de venda direta da exibidora: a agência com o CNPJ da própria afiliada e
/// o vínculo ativo dela com a exibidora.
/// </summary>
/// <remarks>
/// Extraído do Approve do KYC (<c>AppKycReviewController</c>) para que o anunciante
/// da casa da prospecção (<see cref="AnuncianteDaCasaProvisionamento"/>) caia na
/// MESMA cadeia de venda direta do KYC "ad", em vez de uma cópia que divergiria no
/// dia em que uma das duas mudasse. Cada método devolve a mensagem de recusa em vez
/// de lançar: o chamador decide o status HTTP.
/// </remarks>
public sealed class VendaDiretaProvisionamento
{
    private readonly VeiculandoDataContext _db;

    public VendaDiretaProvisionamento(VeiculandoDataContext db) => _db = db;

    /// <summary>Acha ou cria a agência de venda direta (CNPJ da afiliada).</summary>
    public async Task<(Agencia Agencia, string Erro)> GarantirAgenciaAsync(
        Afiliada afiliada, Usuario actor, CancellationToken ct)
    {
        var agency = await _db.Agencias.SingleOrDefaultAsync(a => a.Cnpj.Numero == afiliada.Cnpj.Numero, ct);
        if (agency != null && agency.StatusExibicao != StatusExibicaoEnum.Ativo)
            return (null, "A agência de venda direta está inativa no Core.");
        if (agency != null) return (agency, null);

        if (afiliada.Email == null || afiliada.Telefone == null)
            return (null, "Configure e-mail e telefone da exibidora antes de habilitar a venda direta.");
        agency = new Agencia(AgenciaVendaDiretaProvisionamento.NomeFantasia,
            AgenciaVendaDiretaProvisionamento.RazaoSocial, afiliada.Endereco, afiliada.Cidade,
            afiliada.Uf, afiliada.Telefone, afiliada.Email, afiliada.Site, afiliada.Cnpj,
            afiliada.InscricaoEstadual, afiliada.InscricaoMunicipal, null, null, string.Empty, 0m, actor);
        if (!agency.IsValid()) return (null, "Não foi possível provisionar a venda direta.");
        _db.Agencias.Add(agency);
        await _db.SaveChangesAsync(ct);
        return (agency, null);
    }

    /// <summary>
    /// Garante o vínculo ativo agência↔exibidora, registrando a origem WhiteLabel
    /// em nome de <paramref name="origemUsuarioId"/> quando o vínculo nasce aqui.
    /// </summary>
    public async Task<string> GarantirVinculoAsync(
        Afiliada afiliada, Agencia agency, Usuario actor, int origemUsuarioId, CancellationToken ct)
    {
        var agencyLink = await _db.AfiliadaAgencias.SingleOrDefaultAsync(v =>
            v.IdAfiliada == afiliada.Id && v.IdAgencia == agency.Id, ct);
        if (agencyLink != null)
        {
            agencyLink.Ativar();
            return null;
        }

        agencyLink = new AfiliadaAgencia(afiliada, agency, actor);
        agencyLink.RegistrarOrigem(FonteOrigemEnum.WhiteLabel, afiliada.Id, origemUsuarioId);
        if (!agencyLink.IsValid()) return "Não foi possível vincular a agência à exibidora.";
        _db.AfiliadaAgencias.Add(agencyLink);
        return null;
    }
}
