using System;
using System.Data;
using System.Data.Entity;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities;
using Veiculando.Domain.Entities.WhiteLabel;
using Veiculando.Domain.Enums;
using Veiculando.Domain.ValueObjects;
using Veiculando.WhiteLabel.Api.Configurations;
using Veiculando.WhiteLabel.Api.Middleware;
using BC = BCrypt.Net.BCrypt;

namespace Veiculando.WhiteLabel.Api.Services;

/// <summary>
/// Anunciante "da casa" da prospecção: a própria exibidora vendendo direto.
/// </summary>
/// <remarks>
/// <para><b>Regra do owner (HF-8):</b> a tela de prospecção não escolhe anunciante,
/// então a sessão abre como a afiliada, registrando o operador que a abriu (trilha
/// <c>FonteOrigem/FonteUsuarioId</c> + <c>WL_ProspeccaoSessaoEvento</c>, que não muda).
/// Os pedidos entram na cadeia de venda direta da afiliada — a do KYC "ad".</para>
///
/// <para><b>Como nasce:</b> sob demanda, idempotente, no primeiro resgate sem
/// <c>AnuncianteId</c>. Sem migration e sem seed: tudo é linha nova nas tabelas
/// existentes, então nada de schema do Core muda. É a mesma cadeia que o Approve do
/// KYC "ad" monta, com a afiliada no papel de anunciante:</para>
/// <list type="bullet">
///   <item>agência de venda direta (CNPJ da afiliada) + vínculo — via <see cref="VendaDiretaProvisionamento"/>, o mesmo código do Approve;</item>
///   <item>cliente com o CNPJ da afiliada + vínculo com a exibidora;</item>
///   <item>contrato agência↔cliente;</item>
///   <item>usuário Core (<c>UsuarioAnunciante</c>) habilitado na agência — é o "responsável" das campanhas e dos pedidos;</item>
///   <item><c>WlUsuarioAnunciante</c> com ClienteId/AgenciaId/CNPJ e onboarding "ad" já aprovado, que é o que o contexto, a cotação e o checkout do App exigem.</item>
/// </list>
///
/// <para><b>A conta não é de ninguém.</b> O e-mail é sintético num domínio
/// <c>.invalid</c> (nunca entrega) e a senha é aleatória e descartada: o anunciante da
/// casa só abre por token de prospecção, nunca por login nem por recuperação de senha.</para>
///
/// <para>Conflitos (agência inativa, contrato expirado, exibidora sem e-mail/telefone)
/// voltam como mensagem; quem chama decide o status. Tudo roda numa transação
/// serializável: ou a cadeia inteira nasce, ou nada nasce — e duas réplicas resgatando
/// ao mesmo tempo não criam duas casas.</para>
/// </remarks>
public sealed class AnuncianteDaCasaProvisionamento
{
    public const string DominioEmail = "wl.veiculando.invalid";
    private const string PerfilAnunciante = "UsuarioAnunciante";

    private readonly VeiculandoDataContext _db;
    private readonly ITenantQueries _tenant;
    private readonly ISeedAccountResolver _seed;
    private readonly VendaDiretaProvisionamento _vendaDireta;

    public AnuncianteDaCasaProvisionamento(VeiculandoDataContext db, ITenantQueries tenant,
        ISeedAccountResolver seed, VendaDiretaProvisionamento vendaDireta)
        => (_db, _tenant, _seed, _vendaDireta) = (db, tenant, seed, vendaDireta);

    /// <summary>E-mail sintético do anunciante da casa da afiliada.</summary>
    public static string EmailDe(int afiliadaId) =>
        $"casa.{afiliadaId.ToString(CultureInfo.InvariantCulture)}@{DominioEmail}";

    public async Task<(WlUsuarioAnunciante Usuario, string Erro)> GarantirAsync(CancellationToken ct)
    {
        var afiliadaId = _tenant.AfiliadaId;
        using var transaction = _db.Database.BeginTransaction(IsolationLevel.Serializable);

        var afiliada = await _db.Afiliadas.Include(a => a.UsuarioCadastro)
            .SingleOrDefaultAsync(a => a.Id == afiliadaId, ct);
        if (afiliada == null) return (null, "Exibidora não encontrada.");
        if (afiliada.Cnpj == null || string.IsNullOrWhiteSpace(afiliada.Cnpj.Numero))
            return (null, "Informe o CNPJ da exibidora antes de habilitar a venda direta.");
        var documento = afiliada.Cnpj.Numero;
        // Telefone e Email são objetos complexos do EF6: nunca chegam nulos, o vazio mora no valor.
        if (string.IsNullOrWhiteSpace(afiliada.Telefone?.Numero) || string.IsNullOrWhiteSpace(afiliada.Email?.Endereco))
            return (null, "Configure e-mail e telefone da exibidora antes de habilitar a venda direta.");

        var actor = afiliada.UsuarioCadastro;
        if (actor == null)
        {
            var account = _seed.Resolve();
            actor = await _db.UsuariosAfiliada.SingleOrDefaultAsync(u =>
                u.IdAfiliada == afiliadaId && u.Email.Endereco == account.Email &&
                u.StatusExibicao == StatusExibicaoEnum.Ativo, ct);
        }
        if (actor == null) return (null, "A exibidora não possui usuário comercial de cadastro.");

        var profile = await _db.PerfilUsuario.SingleOrDefaultAsync(p => p.Codigo == PerfilAnunciante, ct);
        if (profile == null) return (null, "Perfil comercial de anunciante ausente.");

        var email = EmailDe(afiliadaId);
        var casa = await _db.WlUsuariosAnunciante.SingleOrDefaultAsync(u =>
            u.AfiliadaId == afiliadaId && u.Email.Endereco == email, ct);
        if (casa != null && casa.DataExclusao != null)
            return (null, "O anunciante da casa foi removido; reative-o antes de prospectar.");

        // Atalho do caminho quente: identidade completa e onboarding aprovado.
        if (casa?.ClienteId != null && casa.AgenciaId != null &&
            await _db.WlAppOnboardings.AnyAsync(o => o.UsuarioId == casa.Id && o.Status == WlAppKycStatus.Aprovado, ct))
        {
            transaction.Commit();
            return (casa, null);
        }

        if (casa == null)
        {
            casa = new WlUsuarioAnunciante(email, BC.HashPassword(SenhaDescartada()), afiliadaId);
            _db.WlUsuariosAnunciante.Add(casa);
            await _db.SaveChangesAsync(ct);
        }

        var (agency, agencyError) = await _vendaDireta.GarantirAgenciaAsync(afiliada, actor, ct);
        if (agencyError != null) return (null, agencyError);
        var linkError = await _vendaDireta.GarantirVinculoAsync(afiliada, agency, actor, casa.Id, ct);
        if (linkError != null) return (null, linkError);

        var client = await _db.Clientes.SingleOrDefaultAsync(c => c.Cnpj.Numero == documento, ct);
        if (client != null && client.StatusExibicao != StatusExibicaoEnum.Ativo)
            return (null, "O CNPJ da exibidora já pertence a um anunciante inativo no Core.");
        if (client == null)
        {
            client = new Cliente(CodigoDoCliente(afiliadaId), afiliada.Nome,
                string.IsNullOrWhiteSpace(afiliada.RazaoSocial) ? afiliada.Nome : afiliada.RazaoSocial,
                afiliada.Endereco, afiliada.Cidade, afiliada.Uf, afiliada.Telefone, afiliada.Cnpj,
                afiliada.InscricaoEstadual, afiliada.InscricaoMunicipal, afiliada.Cidade,
                null, null, null, string.Empty, 0m, actor);
            if (!client.IsValid())
                return (null, "Dados da exibidora não passaram na validação comercial de anunciante: " +
                    string.Join("; ", client.Notifications.Select(n => n.Message)));
            _db.Clientes.Add(client);
            await _db.SaveChangesAsync(ct);
        }

        var clientLink = await _db.AfiliadaClientes.SingleOrDefaultAsync(v =>
            v.IdAfiliada == afiliadaId && v.IdCliente == client.Id, ct);
        if (clientLink == null)
        {
            clientLink = new AfiliadaCliente(afiliada, client, actor);
            clientLink.RegistrarOrigem(FonteOrigemEnum.WhiteLabel, afiliadaId, casa.Id);
            if (!clientLink.IsValid()) return (null, "Não foi possível vincular a exibidora como anunciante de si mesma.");
            _db.AfiliadaClientes.Add(clientLink);
        }
        else clientLink.Ativar();

        var contract = await _db.AgenciaClientes.SingleOrDefaultAsync(c =>
            c.IdAgencia == agency.Id && c.IdCliente == client.Id, ct);
        if (contract != null && contract.DataExpiracaoContrato <= DateTime.UtcNow)
            return (null, "O contrato de venda direta expirou e precisa ser renovado.");
        if (contract != null && contract.StatusExibicao != StatusExibicaoEnum.Ativo)
            return (null, "O contrato de venda direta está inativo no Core.");
        if (contract == null)
        {
            contract = new AgenciaCliente(agency, client, 0m, null, DateTime.UtcNow.Date,
                DateTime.UtcNow.Date.AddYears(2), actor);
            if (!contract.IsValid()) return (null, "Não foi possível criar o contrato de venda direta.");
            _db.AgenciaClientes.Add(contract);
        }

        var coreUser = await _db.UsuariosAnunciantes.Include(u => u.Agencia)
            .SingleOrDefaultAsync(u => u.Email.Endereco == email, ct);
        if (coreUser != null && (coreUser.Agencia != null && coreUser.Agencia.Id != agency.Id ||
            coreUser.StatusAprovacao != StatusUsuarioEnum.Aprovado && coreUser.Agencia != null ||
            coreUser.StatusExibicao != StatusExibicaoEnum.Ativo))
            return (null, "O anunciante da casa está bloqueado ou ligado a outra agência no Core.");
        if (coreUser == null)
        {
            coreUser = new UsuarioAnunciante(profile, afiliada.Nome, new Email(email),
                Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)), afiliada.Nome,
                afiliada.Nome, afiliada.Telefone, new Telefone(afiliada.Telefone.Numero), actor);
            _db.UsuariosAnunciantes.Add(coreUser);
        }
        if (coreUser.Agencia == null) coreUser.HabilitarParaWhiteLabel(agency, actor);
        if (!coreUser.IsValid()) return (null, "Não foi possível habilitar o responsável comercial da casa.");
        await _db.SaveChangesAsync(ct);

        casa.VincularIdentidadeComercial(client.Id, agency.Id, documento);

        var onboarding = await _db.WlAppOnboardings.SingleOrDefaultAsync(o => o.UsuarioId == casa.Id, ct);
        if (onboarding == null)
        {
            onboarding = new WlAppOnboarding(casa);
            onboarding.SalvarRascunho("ad", documento, DadosEmpresariais(afiliada), 4);
            onboarding.ReivindicarDocumento(documento);
            onboarding.Enviar();
            onboarding.Transicionar(WlAppKycStatus.EmAnalise, "Anunciante da casa provisionado pela prospecção.");
            onboarding.Transicionar(WlAppKycStatus.Aprovado,
                "Venda direta da própria exibidora: aprovado automaticamente, sem documentos.");
            _db.WlAppOnboardings.Add(onboarding);
        }
        else if (onboarding.Status != WlAppKycStatus.Aprovado)
            return (null, "O cadastro do anunciante da casa não está aprovado.");

        await _db.SaveChangesAsync(ct);
        transaction.Commit();
        return (casa, null);
    }

    /// <summary>
    /// Código do cliente da casa. O Core exige começar por letra (<c>^[a-zA-Z][a-zA-Z0-9]*$</c>),
    /// então o CNPJ cru não serve; o id da afiliada dá um código estável e único por exibidora.
    /// </summary>
    private static string CodigoDoCliente(int afiliadaId) =>
        ("A" + afiliadaId.ToString("D5", CultureInfo.InvariantCulture)).Substring(0, Cliente.CodigoMaxLength);

    private static string SenhaDescartada() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

    private static string DadosEmpresariais(Afiliada afiliada) => JsonSerializer.Serialize(new AppKycBusinessData
    {
        TradeName = afiliada.Nome,
        LegalName = string.IsNullOrWhiteSpace(afiliada.RazaoSocial) ? afiliada.Nome : afiliada.RazaoSocial,
        City = afiliada.Cidade,
        State = afiliada.Uf,
        StateTaxId = afiliada.InscricaoEstadual,
        MunicipalTaxId = afiliada.InscricaoMunicipal
    });
}
