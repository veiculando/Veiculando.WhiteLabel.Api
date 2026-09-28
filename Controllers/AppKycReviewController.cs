using System;
using System.Data;
using System.Data.Entity;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities;
using Veiculando.Domain.Entities.WhiteLabel;
using Veiculando.Domain.Enums;
using Veiculando.Domain.Services;
using Veiculando.Domain.ValueObjects;
using Veiculando.WhiteLabel.Api.Configurations;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;

namespace Veiculando.WhiteLabel.Api.Controllers;

/// <summary>
/// Revisão do KYC iniciado no App. É separada da fila legada de
/// OrganizacaoOnboarding: aquela não é vinculada ao usuário do App.
/// </summary>
[ApiController]
[Route("api/wl/kyc/app")]
[Authorize(Policy = AuthorizationSetup.ClienteGerenciar)]
public sealed class AppKycReviewController : ControllerBase
{
    private readonly VeiculandoDataContext _db;
    private readonly ITenantQueries _tenant;
    private readonly IWlUploadStorage _storage;
    private readonly ISeedAccountResolver _seed;

    public AppKycReviewController(VeiculandoDataContext db, ITenantQueries tenant,
        IWlUploadStorage storage, ISeedAccountResolver seed)
        => (_db, _tenant, _storage, _seed) = (db, tenant, storage, seed);

    [HttpGet]
    public async Task<IActionResult> Queue(CancellationToken ct)
    {
        var entries = await _db.WlAppOnboardings.AsNoTracking()
            .Where(o => o.AfiliadaId == _tenant.AfiliadaId && o.Status != WlAppKycStatus.Rascunho)
            .OrderByDescending(o => o.AtualizadoEm)
            .Take(100)
            .Select(o => new { o.Id, o.UsuarioId, o.TipoConta, o.Documento, o.Status, o.AtualizadoEm })
            .ToListAsync(ct);
        return Ok(entries);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Detail(Guid id, CancellationToken ct)
    {
        var onboarding = await _db.WlAppOnboardings.AsNoTracking()
            .Include(o => o.Historico).Include(o => o.Documentos)
            .SingleOrDefaultAsync(o => o.Id == id && o.AfiliadaId == _tenant.AfiliadaId, ct);
        if (onboarding == null) return NotFound();
        return Ok(new
        {
            onboarding.Id, onboarding.UsuarioId, onboarding.TipoConta, onboarding.Documento,
            onboarding.Status, onboarding.AtualizadoEm, onboarding.Motivo,
            business = Deserialize(onboarding.DadosJson),
            documents = onboarding.Documentos.Where(d => d.Ativo)
                .Select(d => new { d.Id, d.Nome, d.Tipo, d.ContentType, d.Tamanho }),
            history = onboarding.Historico.OrderBy(e => e.Em)
                .Select(e => new { e.De, e.Para, e.Motivo, e.Em })
        });
    }

    [HttpGet("{id:guid}/documents/{documentId:guid}")]
    public async Task<IActionResult> Download(Guid id, Guid documentId, CancellationToken ct)
    {
        var document = await _db.WlAppDocumentos.AsNoTracking().Include(d => d.Onboarding)
            .SingleOrDefaultAsync(d => d.Id == documentId && d.OnboardingId == id && d.Ativo &&
                d.Onboarding.AfiliadaId == _tenant.AfiliadaId, ct);
        if (document == null) return NotFound();
        var key = WlUploadKey.Parse(document.StorageKey);
        if (key.TenantId != _tenant.AfiliadaId || key.ResourceId != document.Onboarding.UsuarioId || key.Kind != "kyc")
            return NotFound();
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(await _storage.ReadAsync(document.StorageKey, ct), document.ContentType, document.Nome);
    }

    [HttpPost("{id:guid}/review")]
    [EnableRateLimiting(Startup.RateLimitEscrita)]
    public Task<IActionResult> Review(Guid id, CancellationToken ct) => Decide(id, WlAppKycStatus.EmAnalise, null, ct);

    [HttpPost("{id:guid}/adjustments")]
    [EnableRateLimiting(Startup.RateLimitEscrita)]
    public Task<IActionResult> Adjustments(Guid id, [FromBody] ReviewReason request, CancellationToken ct)
        => Decide(id, WlAppKycStatus.AjustesSolicitados, request?.Reason, ct);

    [HttpPost("{id:guid}/reject")]
    [EnableRateLimiting(Startup.RateLimitEscrita)]
    public Task<IActionResult> Reject(Guid id, [FromBody] ReviewReason request, CancellationToken ct)
        => Decide(id, WlAppKycStatus.Rejeitado, request?.Reason, ct);

    [HttpPost("{id:guid}/suspend")]
    [EnableRateLimiting(Startup.RateLimitEscrita)]
    public Task<IActionResult> Suspend(Guid id, [FromBody] ReviewReason request, CancellationToken ct)
        => Decide(id, WlAppKycStatus.Suspenso, request?.Reason, ct);

    private async Task<IActionResult> Decide(Guid id, WlAppKycStatus target, string reason, CancellationToken ct)
    {
        if (!int.TryParse(User.FindFirstValue("WlUsuarioId"), out var operatorId)) return Unauthorized();
        if (target != WlAppKycStatus.EmAnalise && string.IsNullOrWhiteSpace(reason))
            return BadRequest(new { message = "Informe o motivo da decisão." });
        using var transaction = _db.Database.BeginTransaction(IsolationLevel.Serializable);
        var onboarding = await _db.WlAppOnboardings
            .SingleOrDefaultAsync(o => o.Id == id && o.AfiliadaId == _tenant.AfiliadaId, ct);
        if (onboarding == null) return NotFound();
        try
        {
            onboarding.Transicionar(target, target == WlAppKycStatus.EmAnalise
                ? $"Análise assumida pelo operador WL #{operatorId}." : $"Operador WL #{operatorId}: {reason.Trim()}");
            await _db.SaveChangesAsync(ct);
            transaction.Commit();
            return Ok(new { onboarding.Id, onboarding.Status });
        }
        catch (InvalidOperationException) { return Conflict(new { message = "Transição KYC inválida para o estado atual." }); }
    }

    [HttpPost("{id:guid}/approve")]
    [EnableRateLimiting(Startup.RateLimitEscrita)]
    public async Task<IActionResult> Approve(Guid id, CancellationToken ct)
    {
        if (!int.TryParse(User.FindFirstValue("WlUsuarioId"), out var operatorId)) return Unauthorized();
        using var transaction = _db.Database.BeginTransaction(IsolationLevel.Serializable);
        var onboarding = await _db.WlAppOnboardings.Include(o => o.Usuario).Include(o => o.Documentos)
            .SingleOrDefaultAsync(o => o.Id == id && o.AfiliadaId == _tenant.AfiliadaId, ct);
        if (onboarding == null) return NotFound();
        if (onboarding.Status == WlAppKycStatus.Aprovado) return Ok(new { onboarding.Id, onboarding.Status });
        if (onboarding.Status != WlAppKycStatus.EmAnalise)
            return Conflict(new { message = "Assuma a análise antes de aprovar." });
        if (onboarding.TipoConta != "ad" && onboarding.TipoConta != "ag")
            return Conflict(new { message = "O cadastro legado precisa ser atualizado para anunciante direto ou agência." });
        if (!WlAppDocuments.Valid(onboarding.Documento, "pj"))
            return Conflict(new { message = "CNPJ do cadastro inválido." });
        var business = Deserialize(onboarding.DadosJson);
        if (business == null || !ValidBusiness(business))
            return Conflict(new { message = "Dados empresariais ou do responsável incompletos." });
        if (!onboarding.Documentos.Any(d => d.Ativo && d.Tipo == "corporate") ||
            !onboarding.Documentos.Any(d => d.Ativo && d.Tipo == "representative"))
            return Conflict(new { message = "Faltam o contrato social e a identificação do responsável." });
        var identity = await _db.WlAppIdentidades.AsNoTracking()
            .SingleOrDefaultAsync(i => i.Id == onboarding.UsuarioId, ct);
        if (identity?.EmailConfirmado != true)
            return Conflict(new { message = "E-mail do solicitante não confirmado." });
        var afiliada = await _db.Afiliadas.Include(a => a.UsuarioCadastro)
            .SingleOrDefaultAsync(a => a.Id == _tenant.AfiliadaId, ct);
        if (afiliada == null) return NotFound(new { message = "Exibidora não encontrada." });
        Usuario actor = afiliada?.UsuarioCadastro;
        if (actor == null)
        {
            var account = _seed.Resolve();
            actor = await _db.UsuariosAfiliada.SingleOrDefaultAsync(u =>
                u.IdAfiliada == _tenant.AfiliadaId && u.Email.Endereco == account.Email &&
                u.StatusExibicao == StatusExibicaoEnum.Ativo, ct);
        }
        if (actor == null) return Conflict(new { message = "A exibidora não possui usuário comercial de cadastro." });

        var profile = await _db.PerfilUsuario.SingleOrDefaultAsync(p => p.Codigo == "UsuarioAnunciante", ct);
        if (profile == null) return Conflict(new { message = "Perfil comercial de anunciante ausente." });
        var userEmail = onboarding.Usuario.Email.Endereco;
        var coreUser = await _db.UsuariosAnunciantes.Include(u => u.Agencia)
            .SingleOrDefaultAsync(u => u.Email.Endereco == userEmail, ct);
        if (coreUser != null && (coreUser.Agencia == null ||
            coreUser.Agencia.Cnpj.Numero != (onboarding.TipoConta == "ag" ? onboarding.Documento : afiliada.Cnpj.Numero) ||
            coreUser.StatusAprovacao != StatusUsuarioEnum.Aprovado || !coreUser.EmailConfirmado ||
            coreUser.StatusExibicao != StatusExibicaoEnum.Ativo))
            return Conflict(new { message = "Este e-mail já possui outra identidade ou um acesso comercial bloqueado no Core." });

        Agencia agency;
        Cliente client = null;
        if (onboarding.TipoConta == "ad")
        {
            agency = await _db.Agencias.SingleOrDefaultAsync(a => a.Cnpj.Numero == afiliada.Cnpj.Numero, ct);
            if (agency != null && agency.StatusExibicao != StatusExibicaoEnum.Ativo)
                return Conflict(new { message = "A agência de venda direta está inativa no Core." });
            if (agency == null)
            {
                if (afiliada.Email == null || afiliada.Telefone == null)
                    return Conflict(new { message = "Configure e-mail e telefone da exibidora antes de habilitar a venda direta." });
                agency = new Agencia(AgenciaVendaDiretaProvisionamento.NomeFantasia,
                    AgenciaVendaDiretaProvisionamento.RazaoSocial, afiliada.Endereco, afiliada.Cidade,
                    afiliada.Uf, afiliada.Telefone, afiliada.Email, afiliada.Site, afiliada.Cnpj,
                    afiliada.InscricaoEstadual, afiliada.InscricaoMunicipal, null, null, string.Empty, 0m, actor);
                if (!agency.IsValid()) return Conflict(new { message = "Não foi possível provisionar a venda direta." });
                _db.Agencias.Add(agency);
                await _db.SaveChangesAsync(ct);
            }
            client = await _db.Clientes.SingleOrDefaultAsync(c => c.Cnpj.Numero == onboarding.Documento, ct);
            if (client != null && client.StatusExibicao != StatusExibicaoEnum.Ativo)
                return Conflict(new { message = "Este CNPJ já pertence a um anunciante inativo no Core." });
            if (client == null)
            {
                client = new Cliente(onboarding.Documento.Substring(0, Cliente.CodigoMaxLength),
                    business.TradeName, business.LegalName, Address(business), business.City, business.State,
                    new Telefone(business.Phone), new Cnpj(onboarding.Documento), business.StateTaxId,
                    business.MunicipalTaxId, business.City, null, null, null, string.Empty, 0m, actor);
                if (!client.IsValid()) return Conflict(new { message = "Dados do anunciante não passaram na validação comercial." });
                _db.Clientes.Add(client);
                await _db.SaveChangesAsync(ct);
            }
            var clientLink = await _db.AfiliadaClientes.SingleOrDefaultAsync(v =>
                v.IdAfiliada == afiliada.Id && v.IdCliente == client.Id, ct);
            if (clientLink == null)
            {
                clientLink = new AfiliadaCliente(afiliada, client, actor);
                clientLink.RegistrarOrigem(FonteOrigemEnum.WhiteLabel, afiliada.Id, onboarding.UsuarioId);
                if (!clientLink.IsValid()) return Conflict(new { message = "Não foi possível vincular o anunciante à exibidora." });
                _db.AfiliadaClientes.Add(clientLink);
            }
            else clientLink.Ativar();
        }
        else
        {
            agency = await _db.Agencias.SingleOrDefaultAsync(a => a.Cnpj.Numero == onboarding.Documento, ct);
            if (agency != null && agency.StatusExibicao != StatusExibicaoEnum.Ativo)
                return Conflict(new { message = "Esta agência já está inativa no Core." });
            if (agency == null)
            {
                agency = new Agencia(business.TradeName, business.LegalName, Address(business), business.City,
                    business.State, new Telefone(business.Phone), new Email(userEmail), business.Website,
                    new Cnpj(onboarding.Documento), business.StateTaxId, business.MunicipalTaxId,
                    null, null, string.Empty, 0m, actor);
                if (!agency.IsValid()) return Conflict(new { message = "Dados da agência não passaram na validação comercial." });
                _db.Agencias.Add(agency);
                await _db.SaveChangesAsync(ct);
            }
        }
        var agencyLink = await _db.AfiliadaAgencias.SingleOrDefaultAsync(v =>
            v.IdAfiliada == afiliada.Id && v.IdAgencia == agency.Id, ct);
        if (agencyLink == null)
        {
            agencyLink = new AfiliadaAgencia(afiliada, agency, actor);
            agencyLink.RegistrarOrigem(FonteOrigemEnum.WhiteLabel, afiliada.Id, onboarding.UsuarioId);
            if (!agencyLink.IsValid()) return Conflict(new { message = "Não foi possível vincular a agência à exibidora." });
            _db.AfiliadaAgencias.Add(agencyLink);
        }
        else agencyLink.Ativar();

        if (client != null)
        {
            var contract = await _db.AgenciaClientes.SingleOrDefaultAsync(c =>
                c.IdAgencia == agency.Id && c.IdCliente == client.Id, ct);
            if (contract != null && contract.DataExpiracaoContrato <= DateTime.UtcNow)
                return Conflict(new { message = "O contrato de venda direta expirou e precisa ser renovado." });
            if (contract != null && contract.StatusExibicao != StatusExibicaoEnum.Ativo)
                return Conflict(new { message = "O contrato de venda direta está inativo no Core." });
            if (contract == null)
            {
                contract = new AgenciaCliente(agency, client, 0m, null, DateTime.UtcNow.Date,
                    DateTime.UtcNow.Date.AddYears(2), actor);
                if (!contract.IsValid()) return Conflict(new { message = "Não foi possível criar o contrato de venda direta." });
                _db.AgenciaClientes.Add(contract);
            }
        }
        if (coreUser == null)
        {
            coreUser = new UsuarioAnunciante(profile, identity.Nome, new Email(userEmail),
                Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)), identity.Nome,
                business.TradeName, new Telefone(business.Phone), new Telefone(identity.Telefone), actor);
            _db.UsuariosAnunciantes.Add(coreUser);
        }
        if (coreUser.Agencia == null) coreUser.HabilitarParaWhiteLabel(agency, actor);
        if (!coreUser.IsValid()) return Conflict(new { message = "Não foi possível habilitar o responsável comercial." });
        await _db.SaveChangesAsync(ct);
        onboarding.Usuario.VincularIdentidadeComercial(client?.Id, agency.Id, onboarding.Documento);
        onboarding.Transicionar(WlAppKycStatus.Aprovado, $"Aprovado pelo operador WL #{operatorId}.");
        await _db.SaveChangesAsync(ct);
        transaction.Commit();
        return Ok(new { onboarding.Id, onboarding.Status, clientId = client?.Id, agencyId = agency.Id });
    }

    private static AppKycBusinessData Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<AppKycBusinessData>(json ?? "{}"); }
        catch (JsonException) { return null; }
    }

    private static bool ValidBusiness(AppKycBusinessData data) =>
        !string.IsNullOrWhiteSpace(data.TradeName) && !string.IsNullOrWhiteSpace(data.LegalName) &&
        !string.IsNullOrWhiteSpace(data.Phone) && !string.IsNullOrWhiteSpace(data.City) &&
        !string.IsNullOrWhiteSpace(data.State) && !string.IsNullOrWhiteSpace(data.Street) &&
        !string.IsNullOrWhiteSpace(data.Number) && !string.IsNullOrWhiteSpace(data.District) &&
        !string.IsNullOrWhiteSpace(data.ZipCode) && !string.IsNullOrWhiteSpace(data.RepresentativeName) &&
        WlAppDocuments.Valid(data.RepresentativeCpf, "pf") &&
        !string.IsNullOrWhiteSpace(data.RepresentativeEmail);

    private static Endereco Address(AppKycBusinessData data) => new(
        data.District, data.Street, data.Number, data.Complement, null, new Cep(data.ZipCode));

    public sealed class ReviewReason { public string Reason { get; set; } }
}
