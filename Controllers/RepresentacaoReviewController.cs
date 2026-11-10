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
/// Análise, no painel, dos pedidos de representação das agências (VEI-RD-67h): a fila de
/// "Cadastros do App" ganha os anunciantes que as agências cadastraram ou pediram no App.
/// </summary>
/// <remarks>
/// <para><b>Aprovação libera a venda pela agência.</b> Na mesma transação: pedido aprovado,
/// cliente e vínculo com a exibidora ativos e o <c>AgenciaCliente</c> criado com a comissão e a
/// vigência do pedido. Contrato vencido é renovado com as condições novas; contrato vigente é 409
/// (a agência já representa o cliente).</para>
///
/// <para><b>Agência sem vínculo ativo</b> com a exibidora não recebe cliente: 409.</para>
///
/// <para>Quem pede é usuário do App e quem analisa é operador da exibidora, então não há a
/// checagem de autoanálise da carteira.</para>
/// </remarks>
[ApiController]
[Route("api/wl/kyc/app/representacoes")]
[Authorize(Policy = AuthorizationSetup.ClienteGerenciar)]
public sealed class RepresentacaoReviewController : ControllerBase
{
    private readonly VeiculandoDataContext _db;
    private readonly ITenantQueries _tenant;
    private readonly IWlUploadStorage _storage;
    private readonly ISeedAccountResolver _seed;

    public RepresentacaoReviewController(VeiculandoDataContext db, ITenantQueries tenant, IWlUploadStorage storage,
        ISeedAccountResolver seed)
        => (_db, _tenant, _storage, _seed) = (db, tenant, storage, seed);

    /// <summary>Fila: por padrão, os enviados para análise. Rascunho nunca aparece.</summary>
    [HttpGet]
    public async Task<IActionResult> Fila([FromQuery] WlCarteiraStatus? status, CancellationToken ct)
    {
        var alvo = status ?? WlCarteiraStatus.PendenteAnalise;
        if (alvo == WlCarteiraStatus.Rascunho) return BadRequest(new { message = "Rascunho não entra na fila." });
        var itens = await _db.WlRepresentacoes.AsNoTracking()
            .Where(r => r.AfiliadaId == _tenant.AfiliadaId && r.Status == alvo)
            .OrderBy(r => r.AtualizadoEm)
            .Take(200)
            .Select(r => new
            {
                r.Id, r.ClienteId, r.AgenciaId, r.Status, r.Origem, r.Motivo, r.ComissaoAgencia, r.InicioContrato, r.FimContrato, r.AtualizadoEm,
                cliente = _db.Clientes.Where(x => x.Id == r.ClienteId).Select(x => new { x.Nome, x.RazaoSocial, cnpj = x.Cnpj.Numero }).FirstOrDefault(),
                agencia = _db.Agencias.Where(a => a.Id == r.AgenciaId).Select(a => a.Nome).FirstOrDefault()
            })
            .ToListAsync(ct);
        return Ok(itens.Select(r => new
        {
            id = r.Id, clienteId = r.ClienteId, agenciaId = r.AgenciaId, agencia = r.agencia, status = r.Status.ToString(),
            origem = r.Origem.ToString(), motivo = r.Motivo, comissaoAgencia = r.ComissaoAgencia,
            inicioContrato = r.InicioContrato, fimContrato = r.FimContrato, atualizadoEm = r.AtualizadoEm,
            nome = r.cliente?.Nome, razaoSocial = r.cliente?.RazaoSocial, cnpj = CarteiraProspeccao.Digitos(r.cliente?.cnpj)
        }));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Detalhe(Guid id, CancellationToken ct)
    {
        var r = await _db.WlRepresentacoes.AsNoTracking().Include(x => x.Documentos).Include(x => x.Historico)
            .SingleOrDefaultAsync(x => x.Id == id && x.AfiliadaId == _tenant.AfiliadaId, ct);
        if (r == null || r.Status == WlCarteiraStatus.Rascunho) return NotFound();
        var cliente = await _db.Clientes.AsNoTracking().Where(x => x.Id == r.ClienteId)
            .Select(x => new { x.Nome, x.RazaoSocial, cnpj = x.Cnpj.Numero, x.Cidade, x.Uf }).SingleAsync(ct);
        var agencia = await _db.Agencias.AsNoTracking().Where(a => a.Id == r.AgenciaId)
            .Select(a => new { a.Id, a.Nome, cnpj = a.Cnpj.Numero }).SingleAsync(ct);

        // Eventos de agência gravam WL_UsuarioAnunciante; de análise, WL_Usuario (operador).
        var analises = new[] { WlCarteiraEventoTipo.Aprovado, WlCarteiraEventoTipo.AjustesSolicitados, WlCarteiraEventoTipo.Reprovado };
        var ids = r.Historico.Select(e => e.UsuarioId).Concat(new[] { r.SolicitanteId }).Distinct().ToList();
        var operadores = await NomesUsuarioWl.ResolverAsync(_tenant, ids.Select(u => (int?)u), ct);
        var doApp = await _tenant.UsuariosAnunciante.AsNoTracking().Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, email = u.Email.Endereco }).ToListAsync(ct);
        string DaAgencia(int uid) => doApp.Where(u => u.Id == uid).Select(u => u.email).FirstOrDefault();
        string Nome(WlCarteiraEventoTipo evento, int uid) =>
            analises.Contains(evento) ? (operadores.TryGetValue(uid, out var n) ? n : null) : DaAgencia(uid);

        return Ok(new
        {
            id = r.Id, clienteId = r.ClienteId, status = r.Status.ToString(), origem = r.Origem.ToString(), motivo = r.Motivo,
            comissaoAgencia = r.ComissaoAgencia, inicioContrato = r.InicioContrato, fimContrato = r.FimContrato,
            solicitante = DaAgencia(r.SolicitanteId),
            agencia = new { agencia.Id, agencia.Nome, cnpj = CarteiraProspeccao.Digitos(agencia.cnpj) },
            cliente = new { cliente.Nome, cliente.RazaoSocial, cnpj = CarteiraProspeccao.Digitos(cliente.cnpj), cliente.Cidade, cliente.Uf },
            documentos = r.Documentos.Where(d => d.Ativo).Select(d => new { d.Id, d.Tipo, d.Nome, d.ContentType, d.Tamanho }),
            historico = r.Historico.OrderBy(e => e.Em).Select(e => new
            {
                evento = e.Evento.ToString(), usuario = Nome(e.Evento, e.UsuarioId),
                statusDe = e.StatusDe?.ToString(), statusPara = e.StatusPara.ToString(), e.Motivo, e.Em
            })
        });
    }

    [HttpGet("{id:guid}/documentos/{documentoId:guid}")]
    public async Task<IActionResult> Documento(Guid id, Guid documentoId, CancellationToken ct)
    {
        var d = await _db.WlRepresentacaoDocumentos.AsNoTracking().Include(x => x.Representacao)
            .SingleOrDefaultAsync(x => x.Id == documentoId && x.RepresentacaoId == id && x.Ativo &&
                x.Representacao.AfiliadaId == _tenant.AfiliadaId, ct);
        if (d == null) return NotFound();
        var key = WlUploadKey.Parse(d.StorageKey);
        if (key.TenantId != _tenant.AfiliadaId || key.Kind != WlUploadKey.Representacao || key.ResourceId != d.Representacao.ClienteId)
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
        var representacao = await CarregarAsync(id, ct);
        if (representacao == null) return NotFound();
        if (representacao.Status != WlCarteiraStatus.PendenteAnalise)
            return Conflict(new { message = "Operação inválida para o estado atual do pedido." });

        if (!await _db.AfiliadaAgencias.AnyAsync(l => l.IdAfiliada == _tenant.AfiliadaId &&
                l.IdAgencia == representacao.AgenciaId && l.Status == StatusVinculoEnum.Ativo, ct))
            return Conflict(new { message = "A agência não tem vínculo ativo com a exibidora." });
        var afiliada = await _db.Afiliadas.Include(a => a.UsuarioCadastro).SingleAsync(a => a.Id == _tenant.AfiliadaId, ct);
        var ator = afiliada.UsuarioCadastro ?? await AtorDoSeedAsync(ct);
        if (ator == null) return Conflict(new { message = "A exibidora não possui usuário comercial de cadastro." });

        var cliente = await _db.Clientes.SingleAsync(c => c.Id == representacao.ClienteId, ct);
        if (cliente.StatusExibicao == StatusExibicaoEnum.Inativo && await SoDoPedidoAsync(representacao, ct))
        {
            // Inativado numa reprovação anterior e pedido de novo; aprovado, volta a valer.
            await CarteiraClienteCore.AlterarStatusAsync(_db, cliente.Id, StatusExibicaoEnum.Ativo, ct);
            await _db.Entry(cliente).ReloadAsync(ct);
        }
        if (cliente.StatusExibicao != StatusExibicaoEnum.Ativo)
            return Conflict(new { message = "O cliente está inativo no Core." });

        var hoje = DateTime.UtcNow.Date;
        var contrato = await _db.AgenciaClientes.SingleOrDefaultAsync(l => l.IdAgencia == representacao.AgenciaId &&
            l.IdCliente == cliente.Id, ct);
        if (contrato != null && contrato.StatusExibicao != StatusExibicaoEnum.Ativo)
            return Conflict(new { message = "O contrato desta agência com o cliente está inativo no Core." });
        if (contrato != null && contrato.DataExpiracaoContrato >= hoje)
            return Conflict(new { message = "A agência já representa este cliente com contrato vigente." });

        var vinculo = await _db.AfiliadaClientes.SingleOrDefaultAsync(v => v.IdAfiliada == afiliada.Id && v.IdCliente == cliente.Id, ct);
        if (vinculo == null)
        {
            vinculo = new AfiliadaCliente(afiliada, cliente, ator);
            vinculo.RegistrarOrigem(FonteOrigemEnum.WhiteLabel, afiliada.Id, operador);
            _db.AfiliadaClientes.Add(vinculo);
        }
        else vinculo.Ativar();

        if (contrato == null)
        {
            var agencia = await _db.Agencias.SingleAsync(a => a.Id == representacao.AgenciaId, ct);
            contrato = new AgenciaCliente(agencia, cliente, ComissaoCore(representacao), null,
                representacao.InicioContrato, representacao.FimContrato, ator);
            if (!contrato.IsValid())
                return Conflict(new { message = "Não foi possível criar o contrato: " +
                    string.Join("; ", contrato.Notifications.Select(n => n.Message)) });
            _db.AgenciaClientes.Add(contrato);
        }

        representacao.Aprovar(operador);
        await _db.SaveChangesAsync(ct);
        if (contrato.Id > 0 && contrato.DataExpiracaoContrato < hoje)
        {
            // Contrato vencido: renova com as condições do pedido. O AgenciaCliente do Core não
            // expõe alteração; o UPDATE fica restrito a estas colunas e roda nesta transação.
            await _db.Database.ExecuteSqlCommandAsync(@"
UPDATE dbo.AgenciaCliente SET ComissaoAgencia = @p0, DataInicioContrato = @p1, DataExpiracaoContrato = @p2,
    DataAtualizacao = GETDATE() WHERE Id = @p3", ct,
                ComissaoCore(representacao), representacao.InicioContrato, representacao.FimContrato, contrato.Id);
        }
        transaction.Commit();
        return Ok(new { id = representacao.Id, status = representacao.Status.ToString() });
    }

    [HttpPost("{id:guid}/ajustes")]
    [EnableRateLimiting(Startup.RateLimitEscrita)]
    public Task<IActionResult> Ajustes(Guid id, [FromBody] CarteiraReviewController.Motivo request, CancellationToken ct) =>
        DecidirAsync(id, request?.Reason, WlCarteiraStatus.PendenteAnalise, (r, op, m) => r.SolicitarAjustes(op, m), ct);

    [HttpPost("{id:guid}/reprovar")]
    [EnableRateLimiting(Startup.RateLimitEscrita)]
    public Task<IActionResult> Reprovar(Guid id, [FromBody] CarteiraReviewController.Motivo request, CancellationToken ct) =>
        DecidirAsync(id, request?.Reason, null, (r, op, m) => r.Reprovar(op, m), ct, async r =>
        {
            // Cliente que só existia por este pedido não fica ativo no Core (mesma regra do VEI-SUP-39).
            if (await SoDoPedidoAsync(r, ct))
                await CarteiraClienteCore.AlterarStatusAsync(_db, r.ClienteId, StatusExibicaoEnum.Inativo, ct);
        });

    private async Task<IActionResult> DecidirAsync(Guid id, string motivo, WlCarteiraStatus? exigido,
        Action<WlRepresentacao, int, string> decidir, CancellationToken ct, Func<WlRepresentacao, Task> depois = null)
    {
        if (!TryOperador(out var operador)) return Unauthorized();
        if (string.IsNullOrWhiteSpace(motivo)) return BadRequest(new { message = "Informe o motivo da decisão." });
        var representacao = await CarregarAsync(id, ct);
        if (representacao == null) return NotFound();
        if (exigido.HasValue && representacao.Status != exigido.Value)
            return Conflict(new { message = "Operação inválida para o estado atual do pedido." });
        try { decidir(representacao, operador, motivo); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
        using var transaction = _db.Database.BeginTransaction();
        await _db.SaveChangesAsync(ct);
        if (depois != null) await depois(representacao);
        transaction.Commit();
        return Ok(new { id = representacao.Id, status = representacao.Status.ToString() });
    }

    /// <summary>
    /// O cliente nasceu deste pedido e não é usado em mais nada: nenhum outro vínculo ativo,
    /// nenhum contrato ativo com agência, nenhuma campanha e nenhuma carteira de vendedora.
    /// </summary>
    private async Task<bool> SoDoPedidoAsync(WlRepresentacao r, CancellationToken ct)
    {
        if (r.Origem != WlCarteiraOrigem.Cadastro) return false;
        var id = r.ClienteId;
        return !await _db.AfiliadaClientes.AnyAsync(v => v.IdCliente == id && v.IdAfiliada != r.AfiliadaId &&
                   v.Status == StatusVinculoEnum.Ativo, ct)
            && !await _db.AgenciaClientes.AnyAsync(l => l.IdCliente == id && l.StatusExibicao == StatusExibicaoEnum.Ativo, ct)
            && !await _db.Campanhas.AnyAsync(c => c.IdCliente == id, ct)
            && !await _db.WlCarteiraClientes.AnyAsync(c => c.ClienteId == id && c.Status != WlCarteiraStatus.Reprovado, ct)
            && !await _db.WlRepresentacoes.AnyAsync(o => o.ClienteId == id && o.Id != r.Id && o.Status != WlCarteiraStatus.Reprovado, ct);
    }

    private Task<WlRepresentacao> CarregarAsync(Guid id, CancellationToken ct) =>
        _db.WlRepresentacoes.Include(r => r.Documentos)
            .SingleOrDefaultAsync(r => r.Id == id && r.AfiliadaId == _tenant.AfiliadaId, ct);

    private async Task<Usuario> AtorDoSeedAsync(CancellationToken ct)
    {
        var conta = _seed.Resolve();
        return await _db.UsuariosAfiliada.SingleOrDefaultAsync(u => u.IdAfiliada == _tenant.AfiliadaId &&
            u.Email.Endereco == conta.Email && u.StatusExibicao == StatusExibicaoEnum.Ativo, ct);
    }

    /// <summary>O pedido guarda percentual; o AgenciaCliente do Core guarda fração (0 a 0,30).</summary>
    private static decimal ComissaoCore(WlRepresentacao r) => r.ComissaoAgencia / 100m;

    private bool TryOperador(out int id) => int.TryParse(User.FindFirstValue("WlUsuarioId"), out id);
}
