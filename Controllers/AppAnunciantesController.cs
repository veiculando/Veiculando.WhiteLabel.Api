using System;
using System.ComponentModel.DataAnnotations;
using System.Data.Entity;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities;
using Veiculando.Domain.Entities.WhiteLabel;
using Veiculando.Domain.Enums;
using Veiculando.Domain.ValueObjects;
using Veiculando.WhiteLabel.Api.Configurations;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;

namespace Veiculando.WhiteLabel.Api.Controllers;

/// <summary>
/// "Adicionar anunciante" da agência no App (VEI-RD-67d/67g): a agência cadastra ou pede um
/// cliente pelo CNPJ, informa comissão e vigência, anexa os documentos e manda para a análise
/// da exibidora. A aprovação (VEI-RD-67h) cria o <c>AgenciaCliente</c>.
/// </summary>
/// <remarks>
/// <para>Decisão do PO (10/11): no lugar do convite de KYC ao responsável legal do PRD §10.6,
/// reusa o desenho da carteira da vendedora (<see cref="AppCarteiraController"/>).</para>
///
/// <para><b>Quem pode.</b> Só sessão de agência (conta <c>ag</c>, KYC aprovado, vínculo com a
/// exibidora ativo), resolvida por <see cref="AppCampanhasEscopo"/>. Prospecção e anunciante
/// direto recebem 403. O App não tem papel de dono/admin: qualquer usuário aprovado da agência
/// age por ela, e o histórico guarda quem fez o quê.</para>
///
/// <para><b>Cliente novo</b> nasce com o vínculo com a exibidora INATIVO, como na carteira: só a
/// aprovação o ativa, para cadastro pendente ou reprovado não virar cliente da exibidora.</para>
///
/// <para><b>CNPJ</b> é comparado por dígitos (o Core tem CNPJ com máscara). A criação de cliente
/// usa a mesma trava por exibidora do cadastro da carteira, para os dois fluxos não criarem o
/// mesmo CNPJ ao mesmo tempo.</para>
/// </remarks>
[ApiController]
[Route("api/wl/app/anunciantes")]
[Authorize]
public sealed class AppAnunciantesController : ControllerBase
{
    private const long TamanhoMaximo = 10 * 1024 * 1024;
    private const string MensagemSemKyc = "A aprovação do cadastro é necessária para adicionar anunciantes.";

    private readonly VeiculandoDataContext _db;
    private readonly ITenantQueries _tenant;
    private readonly AppCampanhasEscopo _escopo;
    private readonly CarteiraProspeccao _carteira;
    private readonly ISeedAccountResolver _seed;
    private readonly IFileValidationService _validation;
    private readonly WlUploadPipeline _pipeline;
    private readonly WlUploadReferences _references;

    public AppAnunciantesController(VeiculandoDataContext db, ITenantQueries tenant, AppCampanhasEscopo escopo,
        CarteiraProspeccao carteira, ISeedAccountResolver seed, IFileValidationService validation,
        WlUploadPipeline pipeline, WlUploadReferences references)
        => (_db, _tenant, _escopo, _carteira, _seed, _validation, _pipeline, _references) =
            (db, tenant, escopo, carteira, seed, validation, pipeline, references);

    /// <summary>Pedidos da agência nesta exibidora, do mais recente para o mais antigo.</summary>
    [HttpGet]
    public async Task<IActionResult> Listar(CancellationToken ct)
    {
        var (agencia, _, recusa) = await AgenciaAsync(ct);
        if (recusa != null) return recusa;
        var itens = await _db.WlRepresentacoes.AsNoTracking()
            .Where(r => r.AfiliadaId == _tenant.AfiliadaId && r.AgenciaId == agencia)
            .OrderByDescending(r => r.AtualizadoEm)
            .Select(r => new
            {
                r.Id, r.ClienteId, r.Status, r.Origem, r.Motivo, r.ComissaoAgencia, r.InicioContrato, r.FimContrato, r.AtualizadoEm,
                cliente = _db.Clientes.Where(c => c.Id == r.ClienteId).Select(c => new { c.Nome, c.RazaoSocial, cnpj = c.Cnpj.Numero }).FirstOrDefault(),
                documentos = r.Documentos.Where(d => d.Ativo).Select(d => new { d.Id, d.Tipo, d.Nome })
            })
            .ToListAsync(ct);
        return Ok(itens.Select(r => new
        {
            id = r.Id, clienteId = r.ClienteId, status = r.Status.ToString(), origem = r.Origem.ToString(), motivo = r.Motivo,
            comissaoAgencia = r.ComissaoAgencia, inicioContrato = r.InicioContrato, fimContrato = r.FimContrato,
            atualizadoEm = r.AtualizadoEm, nome = r.cliente?.Nome, razaoSocial = r.cliente?.RazaoSocial,
            cnpj = CarteiraProspeccao.Digitos(r.cliente?.cnpj),
            documentos = r.documentos.Select(d => new { id = d.Id, tipo = d.Tipo, nome = d.Nome })
        }));
    }

    /// <summary>
    /// O que acontece se a agência seguir com este CNPJ: <c>novo</c> (precisa dos dados do
    /// cliente), <c>existente</c> (só comissão, vigência e documentos), <c>pedido</c> (já há um
    /// pedido em andamento) ou <c>representado</c> (contrato vigente).
    /// </summary>
    /// <remarks>O nome do cliente existente só sai quando ele já é cliente ativo desta exibidora:
    /// o App não revela quem é cliente de outra exibidora.</remarks>
    [HttpGet("cnpj/{cnpj}")]
    public async Task<IActionResult> ConsultarCnpj(string cnpj, CancellationToken ct)
    {
        var (agencia, _, recusa) = await AgenciaAsync(ct);
        if (recusa != null) return recusa;
        var digitos = CarteiraProspeccao.Digitos(cnpj);
        if (!WlAppDocuments.Valid(digitos, "pj")) return BadRequest(new { message = "Informe um CNPJ válido." });

        var clientes = await _carteira.ClientesPorCnpj(digitos).Select(c => new { c.Id, c.Nome, c.StatusExibicao }).ToListAsync(ct);
        if (clientes.Count == 0) return Ok(new { situacao = "novo" });
        var ids = clientes.Select(c => c.Id).ToList();

        if (await ContratoVigenteAsync(agencia, ids, ct))
            return Ok(new { situacao = "representado" });
        var pedido = await _db.WlRepresentacoes.AsNoTracking()
            .Where(r => r.AfiliadaId == _tenant.AfiliadaId && r.AgenciaId == agencia && ids.Contains(r.ClienteId) &&
                r.Status != WlCarteiraStatus.Reprovado && r.Status != WlCarteiraStatus.Aprovado)
            .Select(r => new { r.Id, r.Status }).FirstOrDefaultAsync(ct);
        if (pedido != null)
            return Ok(new { situacao = "pedido", representacaoId = pedido.Id, status = pedido.Status.ToString() });
        if (clientes.All(c => c.StatusExibicao != StatusExibicaoEnum.Ativo))
            return Conflict(new { message = "Este CNPJ pertence a um anunciante inativo." });

        var daExibidora = await _db.AfiliadaClientes.AsNoTracking()
            .Where(v => v.IdAfiliada == _tenant.AfiliadaId && ids.Contains(v.IdCliente) && v.Status == StatusVinculoEnum.Ativo)
            .Select(v => v.IdCliente).FirstOrDefaultAsync(ct);
        return Ok(new
        {
            situacao = "existente",
            nome = daExibidora == 0 ? null : clientes.Single(c => c.Id == daExibidora).Nome,
            documentosDispensaveis = await KycAprovadoAsync(digitos, ct)
        });
    }

    [HttpPost]
    [EnableRateLimiting(Startup.RateLimitEscrita)]
    public async Task<IActionResult> Criar([FromBody] NovoAnuncianteRequest request, CancellationToken ct)
    {
        var (agencia, usuario, recusa) = await AgenciaAsync(ct);
        if (recusa != null) return recusa;
        var cnpj = CarteiraProspeccao.Digitos(request?.Cnpj);
        if (request == null || !WlAppDocuments.Valid(cnpj, "pj"))
            return BadRequest(new { message = "Informe um CNPJ válido." });
        var condicoes = request.ValidarCondicoes();
        if (condicoes != null) return BadRequest(new { message = condicoes });
        var (inicio, fim) = request.Vigencia();

        using var transaction = _db.Database.BeginTransaction();
        // Mesma trava do cadastro da carteira (VEI-SUP-40): os dois fluxos criam Cliente por CNPJ.
        await _db.Database.ExecuteSqlCommandAsync(
            "DECLARE @r int; EXEC @r = sp_getapplock @Resource = @p0, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 15000; " +
            "IF @r < 0 THROW 50040, 'Cadastro de anunciante ocupado; tente de novo.', 1;",
            ct, $"wl-carteira-cadastro-{_tenant.AfiliadaId}");

        var existentes = await _carteira.ClientesPorCnpj(cnpj).Select(c => c.Id).ToListAsync(ct);
        WlRepresentacao representacao;
        if (existentes.Count > 0)
        {
            if (await ContratoVigenteAsync(agencia, existentes, ct))
                return Conflict(new { code = "already_represented", message = "Sua agência já representa este anunciante." });
            representacao = await _db.WlRepresentacoes.Include(r => r.Documentos)
                .FirstOrDefaultAsync(r => r.AfiliadaId == _tenant.AfiliadaId && r.AgenciaId == agencia && existentes.Contains(r.ClienteId), ct);
            if (representacao != null)
            {
                if (representacao.Status != WlCarteiraStatus.Reprovado && representacao.Status != WlCarteiraStatus.Aprovado)
                    return Conflict(new { code = "pending_request", message = "Já existe um pedido em andamento para este anunciante.",
                        representacaoId = representacao.Id, status = representacao.Status.ToString() });
                // Reprovado, ou aprovado com o contrato vencido: a agência pede de novo.
                var origem = await VinculoAtivoAsync(representacao.ClienteId, ct) ? WlCarteiraOrigem.Solicitacao : representacao.Origem;
                representacao.Reabrir(usuario, origem, request.ComissaoAgencia.Value, inicio, fim);
            }
            else
            {
                var primeiro = existentes[0];
                var cliente = await _db.Clientes.SingleAsync(c => c.Id == primeiro, ct);
                if (cliente.StatusExibicao != StatusExibicaoEnum.Ativo)
                    return Conflict(new { message = "Este CNPJ pertence a um anunciante inativo." });
                if (!await GarantirVinculoAsync(cliente, usuario, ct))
                    return Conflict(new { message = "A exibidora não possui usuário comercial de cadastro." });
                representacao = new WlRepresentacao(_tenant.AfiliadaId, agencia, cliente.Id, usuario, WlCarteiraOrigem.Solicitacao,
                    request.ComissaoAgencia.Value, inicio, fim);
                _db.WlRepresentacoes.Add(representacao);
            }
        }
        else
        {
            var dados = request.ValidarCliente();
            if (dados != null) return BadRequest(new { message = dados });
            var ator = await AtorAsync(ct);
            if (ator == null) return Conflict(new { message = "A exibidora não possui usuário comercial de cadastro." });
            var cliente = new Cliente(await _carteira.NovoCodigoClienteAsync(ct),
                request.NomeFantasia.Trim(), request.RazaoSocial.Trim(),
                new Endereco(request.Bairro, request.Logradouro, request.Numero, request.Complemento, null, new Cep(request.Cep)),
                request.Cidade, request.Uf, new Telefone(request.Telefone), new Cnpj(cnpj), request.InscricaoEstadual,
                request.InscricaoMunicipal, request.Cidade, null, null, null, string.Empty, 0m, ator);
            if (!cliente.IsValid())
                return BadRequest(new { message = "Dados do cliente não passaram na validação comercial: " +
                    string.Join("; ", cliente.Notifications.Select(n => n.Message)) });
            _db.Clientes.Add(cliente);
            await _db.SaveChangesAsync(ct);
            if (!await GarantirVinculoAsync(cliente, usuario, ct))
                return Conflict(new { message = "A exibidora não possui usuário comercial de cadastro." });
            representacao = new WlRepresentacao(_tenant.AfiliadaId, agencia, cliente.Id, usuario, WlCarteiraOrigem.Cadastro,
                request.ComissaoAgencia.Value, inicio, fim);
            _db.WlRepresentacoes.Add(representacao);
        }

        await _db.SaveChangesAsync(ct);
        transaction.Commit();
        return Ok(await RespostaAsync(representacao, cnpj, ct));
    }

    /// <summary>Corrige comissão e vigência enquanto o pedido está com a agência.</summary>
    [HttpPut("{id:guid}/condicoes")]
    [EnableRateLimiting(Startup.RateLimitEscrita)]
    public async Task<IActionResult> AlterarCondicoes(Guid id, [FromBody] CondicoesRequest request, CancellationToken ct)
    {
        var (agencia, _, recusa) = await AgenciaAsync(ct);
        if (recusa != null) return recusa;
        if (request == null) return BadRequest(new { message = "Informe a comissão e a vigência." });
        var erro = request.ValidarCondicoes();
        if (erro != null) return BadRequest(new { message = erro });
        var representacao = await MinhaAsync(id, agencia, ct);
        if (representacao == null) return NotFound();
        if (!representacao.Editavel)
            return Conflict(new { message = "O pedido só pode ser alterado no rascunho ou quando há ajustes pedidos." });
        var (inicio, fim) = request.Vigencia();
        representacao.AlterarCondicoes(request.ComissaoAgencia.Value, inicio, fim);
        await _db.SaveChangesAsync(ct);
        return Ok(new { id = representacao.Id, comissaoAgencia = representacao.ComissaoAgencia,
            inicioContrato = representacao.InicioContrato, fimContrato = representacao.FimContrato });
    }

    [HttpPost("{id:guid}/documentos")]
    [EnableRateLimiting(Startup.RateLimitEscrita)]
    [RequestSizeLimit(11 * 1024 * 1024)]
    public async Task<IActionResult> AnexarDocumento(Guid id, [FromForm] string type, [FromForm] IFormFile file, CancellationToken ct)
    {
        var (agencia, usuario, recusa) = await AgenciaAsync(ct);
        if (recusa != null) return recusa;
        if (type != WlRepresentacaoDocumento.TipoContratoSocial && type != WlRepresentacaoDocumento.TipoResponsavel &&
            type != WlRepresentacaoDocumento.TipoCartaRepresentacao)
            return BadRequest(new { message = "Tipo de documento inválido." });
        if (!_validation.IsValidFile(file, TamanhoMaximo, out var erro)) return BadRequest(new { message = erro });
        var representacao = await MinhaAsync(id, agencia, ct);
        if (representacao == null) return NotFound();
        // Checado antes do upload: o domínio recusaria depois, com o arquivo já no storage.
        if (!representacao.Editavel)
            return Conflict(new { message = "Documentos só podem ser anexados no rascunho ou quando há ajustes pedidos." });

        using var body = new MemoryStream();
        using (var source = file.OpenReadStream()) await source.CopyToAsync(body, ct);
        if (body.Length != file.Length || body.Length == 0) return BadRequest(new { message = "Upload incompleto." });
        body.Position = 0;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(body, ct)).ToLowerInvariant();
        body.Position = 0;
        var extensao = file.ContentType switch { "application/pdf" => ".pdf", "image/png" => ".png", _ => ".jpg" };
        var nome = "wl-" + Guid.NewGuid().ToString("N") + extensao;
        var key = $"tenant-{_tenant.AfiliadaId}/{WlUploadKey.Representacao}/{representacao.ClienteId}/{nome}";
        var original = Path.GetFileName(file.FileName);
        original = original.Substring(0, Math.Min(original.Length, 255));

        await _pipeline.SaveAsync(new WlStoredFile(key, nome, file.ContentType, body.Length, hash, DateTimeOffset.UtcNow),
            body, usuario, async () =>
            {
                representacao.AnexarDocumento(original, key, file.ContentType, body.Length, type, usuario);
                await _db.SaveChangesAsync(ct);
            }, () => _references.ExistsAsync(key, CancellationToken.None), ct);
        return Ok(new { tipo = type, documentos = Documentos(representacao) });
    }

    [HttpPost("{id:guid}/enviar")]
    [EnableRateLimiting(Startup.RateLimitEscrita)]
    public async Task<IActionResult> Enviar(Guid id, CancellationToken ct)
    {
        var (agencia, usuario, recusa) = await AgenciaAsync(ct);
        if (recusa != null) return recusa;
        var representacao = await MinhaAsync(id, agencia, ct);
        if (representacao == null) return NotFound();
        var cnpj = await _db.Clientes.Where(c => c.Id == representacao.ClienteId).Select(c => c.Cnpj.Numero).SingleAsync(ct);
        var dispensar = representacao.Origem == WlCarteiraOrigem.Solicitacao &&
            !(representacao.TemDocumentoAtivo(WlRepresentacaoDocumento.TipoContratoSocial) &&
              representacao.TemDocumentoAtivo(WlRepresentacaoDocumento.TipoResponsavel)) &&
            await KycAprovadoAsync(CarteiraProspeccao.Digitos(cnpj), ct);
        try { representacao.Enviar(usuario, dispensar); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
        await _db.SaveChangesAsync(ct);
        return Ok(new { id = representacao.Id, status = representacao.Status.ToString() });
    }

    /// <summary>A agência da sessão e o usuário, ou a resposta de recusa.</summary>
    private async Task<(int Agencia, int Usuario, IActionResult Recusa)> AgenciaAsync(CancellationToken ct)
    {
        if (User.FindFirstValue("WlPerfil") != "Anunciante" || !int.TryParse(User.FindFirstValue("WlAnuncianteId"), out var userId))
            return (0, 0, Unauthorized());
        var escopo = await _escopo.ResolverAsync(User, userId, MensagemSemKyc, ct);
        if (escopo.StatusErro == 401) return (0, 0, Unauthorized());
        if (escopo.StatusErro.HasValue) return (0, 0, StatusCode(escopo.StatusErro.Value, new { message = escopo.MensagemErro }));
        if (escopo.Perfil != "agencia" || !escopo.Comprador.AgenciaId.HasValue)
            return (0, 0, StatusCode(403, new { message = "Só agências adicionam anunciantes." }));
        return (escopo.Comprador.AgenciaId.Value, userId, null);
    }

    private Task<WlRepresentacao> MinhaAsync(Guid id, int agencia, CancellationToken ct) =>
        _db.WlRepresentacoes.Include(r => r.Documentos)
            .SingleOrDefaultAsync(r => r.Id == id && r.AfiliadaId == _tenant.AfiliadaId && r.AgenciaId == agencia, ct);

    private Task<bool> ContratoVigenteAsync(int agencia, System.Collections.Generic.List<int> clientes, CancellationToken ct) =>
        _db.AgenciaClientes.Where(AppCampanhasEscopo.AgenciaClienteVigente(agencia, DateTime.UtcNow))
            .AnyAsync(l => clientes.Contains(l.IdCliente), ct);

    private Task<bool> VinculoAtivoAsync(int clienteId, CancellationToken ct) =>
        _db.AfiliadaClientes.AnyAsync(v => v.IdAfiliada == _tenant.AfiliadaId && v.IdCliente == clienteId &&
            v.Status == StatusVinculoEnum.Ativo, ct);

    /// <summary>Cliente com KYC aprovado nesta exibidora dispensa contrato social e documento do responsável.</summary>
    private Task<bool> KycAprovadoAsync(string cnpj, CancellationToken ct) =>
        _db.WlAppOnboardings.AnyAsync(o => o.AfiliadaId == _tenant.AfiliadaId && o.Documento == cnpj &&
            o.Status == WlAppKycStatus.Aprovado && o.Documentos.Any(d => d.Ativo), ct);

    private async Task<object> RespostaAsync(WlRepresentacao r, string cnpj, CancellationToken ct) => new
    {
        id = r.Id, clienteId = r.ClienteId, status = r.Status.ToString(), origem = r.Origem.ToString(),
        comissaoAgencia = r.ComissaoAgencia, inicioContrato = r.InicioContrato, fimContrato = r.FimContrato,
        documentosDispensaveis = r.Origem == WlCarteiraOrigem.Solicitacao && await KycAprovadoAsync(cnpj, ct),
        documentos = Documentos(r)
    };

    private static object Documentos(WlRepresentacao r) =>
        r.Documentos.Where(d => d.Ativo).Select(d => new { id = d.Id, tipo = d.Tipo, nome = d.Nome }).ToList();

    /// <summary>Vínculo com a exibidora; o novo nasce INATIVO e só a aprovação o ativa.</summary>
    private async Task<bool> GarantirVinculoAsync(Cliente cliente, int usuario, CancellationToken ct)
    {
        if (await _db.AfiliadaClientes.AnyAsync(v => v.IdAfiliada == _tenant.AfiliadaId && v.IdCliente == cliente.Id, ct))
            return true;
        var ator = await AtorAsync(ct);
        if (ator == null) return false;
        var afiliada = await _db.Afiliadas.SingleAsync(a => a.Id == _tenant.AfiliadaId, ct);
        var vinculo = new AfiliadaCliente(afiliada, cliente, ator);
        vinculo.RegistrarOrigem(FonteOrigemEnum.WhiteLabel, afiliada.Id, usuario);
        vinculo.Inativar();
        _db.AfiliadaClientes.Add(vinculo);
        return true;
    }

    private async Task<Usuario> AtorAsync(CancellationToken ct)
    {
        var afiliada = await _db.Afiliadas.Include(a => a.UsuarioCadastro).SingleAsync(a => a.Id == _tenant.AfiliadaId, ct);
        if (afiliada.UsuarioCadastro != null) return afiliada.UsuarioCadastro;
        var conta = _seed.Resolve();
        return await _db.UsuariosAfiliada.SingleOrDefaultAsync(u => u.IdAfiliada == _tenant.AfiliadaId &&
            u.Email.Endereco == conta.Email && u.StatusExibicao == StatusExibicaoEnum.Ativo, ct);
    }

    public class CondicoesRequest
    {
        /// <summary>Comissão da agência, em %.</summary>
        public decimal? ComissaoAgencia { get; set; }
        public DateTime? InicioContrato { get; set; }
        public DateTime? FimContrato { get; set; }

        /// <summary>Início padrão hoje; fim padrão 2 anos depois do início (igual à venda direta).</summary>
        public (DateTime Inicio, DateTime Fim) Vigencia()
        {
            var inicio = (InicioContrato ?? DateTime.UtcNow).Date;
            return (inicio, (FimContrato ?? inicio.AddYears(2)).Date);
        }

        public string ValidarCondicoes()
        {
            if (!ComissaoAgencia.HasValue) return "Informe a comissão da agência.";
            if (ComissaoAgencia < 0m || ComissaoAgencia > WlRepresentacao.ComissaoMaxima)
                return "A comissão deve ficar entre 0% e 100%.";
            var (inicio, fim) = Vigencia();
            if (fim <= inicio) return "O fim do contrato deve ser depois do início.";
            if (fim <= DateTime.UtcNow.Date) return "O contrato já estaria vencido.";
            return null;
        }
    }

    public sealed class NovoAnuncianteRequest : CondicoesRequest
    {
        [Required] public string Cnpj { get; set; }
        public string NomeFantasia { get; set; }
        public string RazaoSocial { get; set; }
        public string Telefone { get; set; }
        public string Logradouro { get; set; }
        public string Numero { get; set; }
        public string Complemento { get; set; }
        public string Bairro { get; set; }
        public string Cep { get; set; }
        public string Cidade { get; set; }
        public string Uf { get; set; }
        public string InscricaoEstadual { get; set; }
        public string InscricaoMunicipal { get; set; }

        /// <summary>Mensagem do primeiro campo faltante, ou nulo. Só vale para cliente novo.</summary>
        public string ValidarCliente()
        {
            if (string.IsNullOrWhiteSpace(NomeFantasia) || string.IsNullOrWhiteSpace(RazaoSocial))
                return "Informe nome fantasia e razão social.";
            if (string.IsNullOrWhiteSpace(Telefone)) return "Informe o telefone.";
            if (string.IsNullOrWhiteSpace(Logradouro) || string.IsNullOrWhiteSpace(Numero) ||
                string.IsNullOrWhiteSpace(Bairro) || string.IsNullOrWhiteSpace(Cep))
                return "Informe o endereço completo.";
            if (string.IsNullOrWhiteSpace(Cidade) || string.IsNullOrWhiteSpace(Uf)) return "Informe cidade e UF.";
            return null;
        }
    }
}
