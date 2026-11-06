using System;
using System.Data;
using System.Data.Entity;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities;
using Veiculando.Domain.Entities.WhiteLabel;
using Veiculando.Domain.Enums;
using Veiculando.WhiteLabel.Api.Configurations;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;

namespace Veiculando.WhiteLabel.Api.Controllers;

/// <summary>
/// Análise da carteira das vendedoras no painel (VEI-RD-157d): a fila de "Cadastros do App"
/// passa a receber os clientes que a vendedora cadastrou ou pediu na Prospecção.
/// </summary>
/// <remarks>
/// <para><b>Quem envia não analisa (P2).</b> Aprovar, pedir ajustes ou reprovar o próprio cadastro
/// é 403, mesmo com ClienteGerenciar. A regra mora no domínio; aqui ela só vira status HTTP.</para>
///
/// <para><b>Aprovação libera a venda (P3, I4).</b> Na mesma transação: carteira aprovada, vínculo do
/// cliente com a exibidora ativado e contrato com a agência de venda direta, com ComissaoAgencia = 0
/// (no Core essa comissão é da agência e entra no valor do pedido; a da vendedora não mora aqui).</para>
///
/// <para><b>Troca de responsável</b> é do gestor (UsuarioAfiliadaGerenciar), com motivo. Pedidos já
/// feitos continuam com quem vendeu: o relatório lê o operador da sessão, não a carteira.</para>
/// </remarks>
[ApiController]
[Route("api/wl/kyc/app/carteira")]
[Authorize(Policy = AuthorizationSetup.ClienteGerenciar)]
public sealed class CarteiraReviewController : ControllerBase
{
    private readonly VeiculandoDataContext _db;
    private readonly ITenantQueries _tenant;
    private readonly IWlUploadStorage _storage;
    private readonly ISeedAccountResolver _seed;
    private readonly VendaDiretaProvisionamento _vendaDireta;

    public CarteiraReviewController(VeiculandoDataContext db, ITenantQueries tenant, IWlUploadStorage storage,
        ISeedAccountResolver seed, VendaDiretaProvisionamento vendaDireta)
        => (_db, _tenant, _storage, _seed, _vendaDireta) = (db, tenant, storage, seed, vendaDireta);

    /// <summary>Fila: por padrão, os enviados para análise. Rascunho nunca aparece.</summary>
    [HttpGet]
    public async Task<IActionResult> Fila([FromQuery] WlCarteiraStatus? status, CancellationToken ct)
    {
        var alvo = status ?? WlCarteiraStatus.PendenteAnalise;
        if (alvo == WlCarteiraStatus.Rascunho) return BadRequest(new { message = "Rascunho não entra na fila." });
        var itens = await _db.WlCarteiraClientes.AsNoTracking()
            .Where(c => c.AfiliadaId == _tenant.AfiliadaId && c.Status == alvo)
            .OrderBy(c => c.AtualizadoEm)
            .Take(200)
            .Select(c => new
            {
                c.Id, c.ClienteId, c.Status, c.Origem, c.VendedorId, c.SolicitanteId, c.Motivo, c.AtualizadoEm,
                cliente = _db.Clientes.Where(x => x.Id == c.ClienteId).Select(x => new { x.Nome, x.RazaoSocial, cnpj = x.Cnpj.Numero }).FirstOrDefault(),
                vendedora = _tenant.Usuarios.Where(u => u.Id == c.VendedorId).Select(u => u.Email.Endereco).FirstOrDefault()
            })
            .ToListAsync(ct);
        return Ok(itens.Select(c => new
        {
            id = c.Id, clienteId = c.ClienteId, status = c.Status.ToString(), origem = c.Origem.ToString(),
            vendedorId = c.VendedorId, vendedora = c.vendedora, motivo = c.Motivo, atualizadoEm = c.AtualizadoEm,
            nome = c.cliente?.Nome, razaoSocial = c.cliente?.RazaoSocial, cnpj = CarteiraProspeccao.Digitos(c.cliente?.cnpj)
        }));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Detalhe(Guid id, CancellationToken ct)
    {
        var c = await _db.WlCarteiraClientes.AsNoTracking().Include(x => x.Documentos).Include(x => x.Historico)
            .SingleOrDefaultAsync(x => x.Id == id && x.AfiliadaId == _tenant.AfiliadaId, ct);
        if (c == null || c.Status == WlCarteiraStatus.Rascunho) return NotFound();
        var cliente = await _db.Clientes.AsNoTracking().Where(x => x.Id == c.ClienteId)
            .Select(x => new { x.Nome, x.RazaoSocial, cnpj = x.Cnpj.Numero, x.Cidade, x.Uf }).SingleAsync(ct);
        var usuarios = c.Historico.Select(e => e.UsuarioId).Concat(new[] { c.VendedorId ?? 0, c.SolicitanteId }).Distinct().ToList();
        var emails = await _tenant.Usuarios.Where(u => usuarios.Contains(u.Id))
            .Select(u => new { u.Id, email = u.Email.Endereco }).ToDictionaryAsync(u => u.Id, u => u.email, ct);
        string Email(int? uid) => uid.HasValue && emails.TryGetValue(uid.Value, out var e) ? e : null;
        return Ok(new
        {
            id = c.Id, clienteId = c.ClienteId, status = c.Status.ToString(), origem = c.Origem.ToString(), motivo = c.Motivo,
            vendedorId = c.VendedorId, vendedora = Email(c.VendedorId), solicitante = Email(c.SolicitanteId),
            cliente = new { cliente.Nome, cliente.RazaoSocial, cnpj = CarteiraProspeccao.Digitos(cliente.cnpj), cliente.Cidade, cliente.Uf },
            documentos = c.Documentos.Where(d => d.Ativo).Select(d => new { d.Id, d.Tipo, d.Nome, d.ContentType, d.Tamanho }),
            historico = c.Historico.OrderBy(e => e.Em).Select(e => new
            {
                evento = e.Evento.ToString(), usuario = Email(e.UsuarioId), vendedoraDe = Email(e.VendedorDe),
                vendedoraPara = Email(e.VendedorPara), statusDe = e.StatusDe?.ToString(), statusPara = e.StatusPara.ToString(), e.Motivo, e.Em
            })
        });
    }

    [HttpGet("{id:guid}/documentos/{documentoId:guid}")]
    public async Task<IActionResult> Documento(Guid id, Guid documentoId, CancellationToken ct)
    {
        var d = await _db.WlCarteiraDocumentos.AsNoTracking().Include(x => x.Carteira)
            .SingleOrDefaultAsync(x => x.Id == documentoId && x.CarteiraId == id && x.Ativo &&
                x.Carteira.AfiliadaId == _tenant.AfiliadaId, ct);
        if (d == null) return NotFound();
        var key = WlUploadKey.Parse(d.StorageKey);
        if (key.TenantId != _tenant.AfiliadaId || key.Kind != WlUploadKey.Carteira || key.ResourceId != d.Carteira.ClienteId)
            return NotFound();
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(await _storage.ReadAsync(d.StorageKey, ct), d.ContentType, d.Nome);
    }

    [HttpPost("{id:guid}/aprovar")]
    [EnableRateLimiting(Startup.RateLimitEscrita)]
    public async Task<IActionResult> Aprovar(Guid id, CancellationToken ct)
    {
        if (!TryOperador(out var operador)) return Unauthorized();
        using var transaction = _db.Database.BeginTransaction(IsolationLevel.Serializable);
        var carteira = await CarregarAsync(id, ct);
        if (carteira == null) return NotFound();
        var recusa = Recusa(carteira, operador, WlCarteiraStatus.PendenteAnalise);
        if (recusa != null) return recusa;

        var afiliada = await _db.Afiliadas.Include(a => a.UsuarioCadastro).SingleAsync(a => a.Id == _tenant.AfiliadaId, ct);
        var ator = afiliada.UsuarioCadastro ?? await AtorDoSeedAsync(ct);
        if (ator == null) return Conflict(new { message = "A exibidora não possui usuário comercial de cadastro." });
        var (agencia, erro) = await _vendaDireta.GarantirAgenciaAsync(afiliada, ator, ct);
        if (erro != null) return Conflict(new { message = erro });
        var erroVinculo = await _vendaDireta.GarantirVinculoAsync(afiliada, agencia, ator, operador, ct);
        if (erroVinculo != null) return Conflict(new { message = erroVinculo });

        var cliente = await _db.Clientes.SingleAsync(c => c.Id == carteira.ClienteId, ct);
        if (cliente.StatusExibicao != StatusExibicaoEnum.Ativo)
            return Conflict(new { message = "O cliente está inativo no Core." });
        var vinculo = await _db.AfiliadaClientes.SingleOrDefaultAsync(v => v.IdAfiliada == afiliada.Id && v.IdCliente == cliente.Id, ct);
        if (vinculo == null)
        {
            vinculo = new AfiliadaCliente(afiliada, cliente, ator);
            vinculo.RegistrarOrigem(FonteOrigemEnum.WhiteLabel, afiliada.Id, carteira.SolicitanteId);
            _db.AfiliadaClientes.Add(vinculo);
        }
        else vinculo.Ativar();

        var hoje = DateTime.UtcNow.Date;
        var contrato = await _db.AgenciaClientes.SingleOrDefaultAsync(l => l.IdAgencia == agencia.Id && l.IdCliente == cliente.Id, ct);
        if (contrato != null && contrato.StatusExibicao != StatusExibicaoEnum.Ativo)
            return Conflict(new { message = "O contrato de venda direta deste cliente está inativo no Core." });
        if (contrato == null)
        {
            contrato = new AgenciaCliente(agencia, cliente, 0m, null, hoje, hoje.AddYears(2), ator);
            if (!contrato.IsValid()) return Conflict(new { message = "Não foi possível criar o contrato de venda direta." });
            _db.AgenciaClientes.Add(contrato);
        }
        else if (contrato.DataExpiracaoContrato < hoje)
            return Conflict(new { message = "O contrato de venda direta deste cliente expirou e precisa ser renovado no Core." });

        carteira.Aprovar(operador);
        await _db.SaveChangesAsync(ct);
        transaction.Commit();
        return Ok(new { id = carteira.Id, status = carteira.Status.ToString() });
    }

    [HttpPost("{id:guid}/ajustes")]
    [EnableRateLimiting(Startup.RateLimitEscrita)]
    public Task<IActionResult> Ajustes(Guid id, [FromBody] Motivo request, CancellationToken ct) =>
        DecidirAsync(id, request?.Reason, WlCarteiraStatus.PendenteAnalise, (c, op, m) => c.SolicitarAjustes(op, m), ct);

    [HttpPost("{id:guid}/reprovar")]
    [EnableRateLimiting(Startup.RateLimitEscrita)]
    public Task<IActionResult> Reprovar(Guid id, [FromBody] Motivo request, CancellationToken ct) =>
        DecidirAsync(id, request?.Reason, null, (c, op, m) => c.Reprovar(op, m), ct);

    /// <summary>Vendedoras que podem receber carteira: usuários ativos da exibidora.</summary>
    [HttpGet("vendedoras")]
    [Authorize(Policy = AuthorizationSetup.UsuarioAfiliadaGerenciar)]
    public async Task<IActionResult> Vendedoras(CancellationToken ct) =>
        Ok(await _tenant.UsuariosAfiliada.AsNoTracking()
            .Where(u => u.StatusExibicao == StatusExibicaoEnum.Ativo && u.DataExclusao == null)
            .OrderBy(u => u.Email.Endereco)
            .Select(u => new { id = u.Id, email = u.Email.Endereco })
            .ToListAsync(ct));

    [HttpPost("{id:guid}/responsavel")]
    [Authorize(Policy = AuthorizationSetup.UsuarioAfiliadaGerenciar)]
    [EnableRateLimiting(Startup.RateLimitEscrita)]
    public async Task<IActionResult> TrocarResponsavel(Guid id, [FromBody] TrocaResponsavel request, CancellationToken ct)
    {
        if (!TryOperador(out var gestor)) return Unauthorized();
        if (request == null || request.VendedorId <= 0 || string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest(new { message = "Informe a nova vendedora e o motivo." });
        if (!await _tenant.UsuariosAfiliada.AnyAsync(u => u.Id == request.VendedorId &&
                u.StatusExibicao == StatusExibicaoEnum.Ativo && u.DataExclusao == null, ct))
            return BadRequest(new { message = "Vendedora não encontrada nesta exibidora." });
        var carteira = await CarregarAsync(id, ct);
        if (carteira == null) return NotFound();
        try { carteira.TrocarResponsavel(request.VendedorId, gestor, request.Reason); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
        await _db.SaveChangesAsync(ct);
        return Ok(new { id = carteira.Id, vendedorId = carteira.VendedorId });
    }

    [HttpDelete("{id:guid}/responsavel")]
    [Authorize(Policy = AuthorizationSetup.UsuarioAfiliadaGerenciar)]
    [EnableRateLimiting(Startup.RateLimitEscrita)]
    public async Task<IActionResult> RemoverResponsavel(Guid id, [FromBody] Motivo request, CancellationToken ct)
    {
        if (!TryOperador(out var gestor)) return Unauthorized();
        if (string.IsNullOrWhiteSpace(request?.Reason)) return BadRequest(new { message = "Informe o motivo." });
        var carteira = await CarregarAsync(id, ct);
        if (carteira == null) return NotFound();
        try { carteira.RemoverResponsavel(gestor, request.Reason); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
        await _db.SaveChangesAsync(ct);
        return Ok(new { id = carteira.Id, vendedorId = carteira.VendedorId });
    }

    private async Task<IActionResult> DecidirAsync(Guid id, string motivo, WlCarteiraStatus? exigido,
        Action<WlCarteiraCliente, int, string> decidir, CancellationToken ct)
    {
        if (!TryOperador(out var operador)) return Unauthorized();
        if (string.IsNullOrWhiteSpace(motivo)) return BadRequest(new { message = "Informe o motivo da decisão." });
        var carteira = await CarregarAsync(id, ct);
        if (carteira == null) return NotFound();
        var recusa = Recusa(carteira, operador, exigido);
        if (recusa != null) return recusa;
        try { decidir(carteira, operador, motivo); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
        await _db.SaveChangesAsync(ct);
        return Ok(new { id = carteira.Id, status = carteira.Status.ToString() });
    }

    /// <summary>403 para autoanálise; 409 para estado errado. Antes de qualquer gravação.</summary>
    private IActionResult Recusa(WlCarteiraCliente carteira, int operador, WlCarteiraStatus? exigido)
    {
        if (operador == carteira.SolicitanteId)
            return StatusCode(403, new { message = "Quem enviou o cadastro não pode analisá-lo." });
        if (exigido.HasValue && carteira.Status != exigido.Value)
            return Conflict(new { message = "Operação inválida para o estado atual do cadastro." });
        return null;
    }

    private Task<WlCarteiraCliente> CarregarAsync(Guid id, CancellationToken ct) =>
        _db.WlCarteiraClientes.Include(c => c.Documentos)
            .SingleOrDefaultAsync(c => c.Id == id && c.AfiliadaId == _tenant.AfiliadaId, ct);

    private async Task<Usuario> AtorDoSeedAsync(CancellationToken ct)
    {
        var conta = _seed.Resolve();
        return await _db.UsuariosAfiliada.SingleOrDefaultAsync(u => u.IdAfiliada == _tenant.AfiliadaId &&
            u.Email.Endereco == conta.Email && u.StatusExibicao == StatusExibicaoEnum.Ativo, ct);
    }

    private bool TryOperador(out int id) => int.TryParse(User.FindFirstValue("WlUsuarioId"), out id);

    public sealed class Motivo { public string Reason { get; set; } }

    public sealed class TrocaResponsavel
    {
        public int VendedorId { get; set; }
        public string Reason { get; set; }
    }
}
