using System;
using System.ComponentModel.DataAnnotations;
using System.Data;
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
/// Carteira da vendedora na Prospecção (VEI-RD-157b): cadastro de cliente, documentos e envio
/// para a análise do gestor.
/// </summary>
/// <remarks>
/// <para>Só responde em sessão de Prospecção sem anunciante (<see cref="ModoSessaoApp.Carteira"/>);
/// a vendedora vem do resgate gravado no banco.</para>
///
/// <para><b>Rascunho (peer review I3).</b> O cadastro nasce em rascunho e só entra na fila por
/// <c>POST {id}/enviar</c>, que o domínio recusa sem contrato social e documento do responsável.</para>
///
/// <para><b>Cliente inativo (I4).</b> O vínculo do cliente novo com a exibidora nasce inativo e só
/// é ativado na aprovação: cadastro pendente ou reprovado não aparece como cliente da exibidora.
/// O contrato com a agência de venda direta também só nasce na aprovação (TP2b).</para>
///
/// <para><b>CNPJ repetido (P5).</b> Comparação por dígitos no SQL. Da outra vendedora: 409 com o
/// nome dela. Da própria: 409. Cliente ativo da exibidora sem vendedora: vira solicitação de carteira.</para>
/// </remarks>
[ApiController]
[Route("api/wl/app/prospeccao/carteira")]
[Authorize]
public sealed class AppCarteiraController : ControllerBase
{
    private const long TamanhoMaximo = 10 * 1024 * 1024;

    private readonly VeiculandoDataContext _db;
    private readonly ITenantQueries _tenant;
    private readonly CarteiraProspeccao _carteira;
    private readonly ISeedAccountResolver _seed;
    private readonly IFileValidationService _validation;
    private readonly WlUploadPipeline _pipeline;
    private readonly WlUploadReferences _references;

    public AppCarteiraController(VeiculandoDataContext db, ITenantQueries tenant, CarteiraProspeccao carteira,
        ISeedAccountResolver seed, IFileValidationService validation, WlUploadPipeline pipeline, WlUploadReferences references)
        => (_db, _tenant, _carteira, _seed, _validation, _pipeline, _references) =
            (db, tenant, carteira, seed, validation, pipeline, references);

    [HttpGet]
    public async Task<IActionResult> Listar(CancellationToken ct)
    {
        var (vendedora, recusa) = await VendedoraAsync(ct);
        if (recusa != null) return recusa;
        var itens = await _db.WlCarteiraClientes.AsNoTracking()
            .Where(c => c.AfiliadaId == _tenant.AfiliadaId && c.VendedorId == vendedora)
            .OrderByDescending(c => c.AtualizadoEm)
            .Select(c => new
            {
                c.Id, c.ClienteId, c.Status, c.Origem, c.Motivo, c.AtualizadoEm,
                cliente = _db.Clientes.Where(x => x.Id == c.ClienteId).Select(x => new { x.Nome, x.RazaoSocial, cnpj = x.Cnpj.Numero }).FirstOrDefault(),
                documentos = c.Documentos.Where(d => d.Ativo).Select(d => new { d.Id, d.Tipo, d.Nome })
            })
            .ToListAsync(ct);
        return Ok(itens.Select(c => new
        {
            id = c.Id, clienteId = c.ClienteId, status = c.Status.ToString(), origem = c.Origem.ToString(), motivo = c.Motivo,
            atualizadoEm = c.AtualizadoEm, nome = c.cliente?.Nome, razaoSocial = c.cliente?.RazaoSocial,
            cnpj = CarteiraProspeccao.Digitos(c.cliente?.cnpj), documentos = c.documentos
        }));
    }

    [HttpPost]
    [EnableRateLimiting(Startup.RateLimitEscrita)]
    public async Task<IActionResult> Cadastrar([FromBody] CadastroClienteRequest request, CancellationToken ct)
    {
        var (vendedora, recusa) = await VendedoraAsync(ct);
        if (recusa != null) return recusa;
        var cnpj = CarteiraProspeccao.Digitos(request?.Cnpj);
        if (request == null || !WlAppDocuments.Valid(cnpj, "pj"))
            return BadRequest(new { message = "Informe um CNPJ válido." });

        using var transaction = _db.Database.BeginTransaction(IsolationLevel.Serializable);
        var agora = DateTime.UtcNow;
        var existentes = await _carteira.ClientesPorCnpj(cnpj).Select(c => c.Id).ToListAsync(ct);
        WlCarteiraCliente carteira;
        if (existentes.Count > 0)
        {
            var clienteIds = existentes;
            carteira = await _db.WlCarteiraClientes.Include(c => c.Documentos)
                .FirstOrDefaultAsync(c => c.AfiliadaId == _tenant.AfiliadaId && clienteIds.Contains(c.ClienteId), ct);
            var clienteId = carteira?.ClienteId ?? existentes[0];
            var vinculoAtivo = await _db.AfiliadaClientes.AnyAsync(v => v.IdAfiliada == _tenant.AfiliadaId &&
                v.IdCliente == clienteId && v.Status == StatusVinculoEnum.Ativo, ct);
            var origem = vinculoAtivo ? WlCarteiraOrigem.Solicitacao : WlCarteiraOrigem.Cadastro;

            if (carteira != null)
            {
                if (carteira.VendedorId == vendedora)
                    return Conflict(new { code = "own_client", message = "Este cliente já está na sua carteira.", carteiraId = carteira.Id, status = carteira.Status.ToString() });
                if (!carteira.PodeSerRetomado(agora))
                    return Conflict(new { code = "other_seller", message = $"Este cliente já é atendido por {await NomeVendedoraAsync(carteira.VendedorId, ct)}." });
                carteira.Reabrir(vendedora, origem, agora);
            }
            else
            {
                var cliente = await _db.Clientes.SingleAsync(c => c.Id == clienteId, ct);
                if (cliente.StatusExibicao != StatusExibicaoEnum.Ativo)
                    return Conflict(new { message = "Este CNPJ pertence a um anunciante inativo." });
                if (!await GarantirVinculoAsync(cliente, vendedora, ct))
                    return Conflict(new { message = "A exibidora não possui usuário comercial de cadastro." });
                carteira = new WlCarteiraCliente(_tenant.AfiliadaId, clienteId, vendedora, origem);
                _db.WlCarteiraClientes.Add(carteira);
            }
        }
        else
        {
            var dados = request.Validar();
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
            if (!await GarantirVinculoAsync(cliente, vendedora, ct))
                return Conflict(new { message = "A exibidora não possui usuário comercial de cadastro." });
            carteira = new WlCarteiraCliente(_tenant.AfiliadaId, cliente.Id, vendedora, WlCarteiraOrigem.Cadastro);
            _db.WlCarteiraClientes.Add(carteira);
        }

        await _db.SaveChangesAsync(ct);
        transaction.Commit();
        return Ok(new
        {
            id = carteira.Id, clienteId = carteira.ClienteId, status = carteira.Status.ToString(), origem = carteira.Origem.ToString(),
            documentosDispensaveis = await DocumentosDispensaveisAsync(carteira, cnpj, ct)
        });
    }

    [HttpPost("{id:guid}/documentos")]
    [EnableRateLimiting(Startup.RateLimitEscrita)]
    [RequestSizeLimit(11 * 1024 * 1024)]
    public async Task<IActionResult> AnexarDocumento(Guid id, [FromForm] string type, [FromForm] IFormFile file, CancellationToken ct)
    {
        var (vendedora, recusa) = await VendedoraAsync(ct);
        if (recusa != null) return recusa;
        if (type != WlCarteiraDocumento.TipoContratoSocial && type != WlCarteiraDocumento.TipoResponsavel)
            return BadRequest(new { message = "Tipo de documento inválido." });
        if (!_validation.IsValidFile(file, TamanhoMaximo, out var erro)) return BadRequest(new { message = erro });
        var carteira = await MinhaAsync(id, vendedora, ct);
        if (carteira == null) return NotFound();
        // Checado antes do upload: o domínio recusaria depois, com o arquivo já no storage.
        if (carteira.Status != WlCarteiraStatus.Rascunho && carteira.Status != WlCarteiraStatus.AjustesSolicitados)
            return Conflict(new { message = "Documentos só podem ser anexados no rascunho ou quando há ajustes pedidos." });

        using var body = new MemoryStream();
        using (var source = file.OpenReadStream()) await source.CopyToAsync(body, ct);
        if (body.Length != file.Length || body.Length == 0) return BadRequest(new { message = "Upload incompleto." });
        body.Position = 0;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(body, ct)).ToLowerInvariant();
        body.Position = 0;
        var extensao = file.ContentType switch { "application/pdf" => ".pdf", "image/png" => ".png", _ => ".jpg" };
        var nome = "wl-" + Guid.NewGuid().ToString("N") + extensao;
        var key = $"tenant-{_tenant.AfiliadaId}/{WlUploadKey.Carteira}/{carteira.ClienteId}/{nome}";
        var original = Path.GetFileName(file.FileName);
        original = original.Substring(0, Math.Min(original.Length, 255));

        await _pipeline.SaveAsync(new WlStoredFile(key, nome, file.ContentType, body.Length, hash, DateTimeOffset.UtcNow),
            body, vendedora, async () =>
            {
                carteira.AnexarDocumento(original, key, file.ContentType, body.Length, type, vendedora);
                await _db.SaveChangesAsync(ct);
            }, () => _references.ExistsAsync(key, CancellationToken.None), ct);
        return Ok(new { type, documentosCompletos = carteira.DocumentosCompletos });
    }

    [HttpPost("{id:guid}/enviar")]
    [EnableRateLimiting(Startup.RateLimitEscrita)]
    public async Task<IActionResult> Enviar(Guid id, CancellationToken ct)
    {
        var (vendedora, recusa) = await VendedoraAsync(ct);
        if (recusa != null) return recusa;
        var carteira = await MinhaAsync(id, vendedora, ct);
        if (carteira == null) return NotFound();
        var cnpj = await _db.Clientes.Where(c => c.Id == carteira.ClienteId).Select(c => c.Cnpj.Numero).SingleAsync(ct);
        var dispensar = !carteira.DocumentosCompletos &&
            await DocumentosDispensaveisAsync(carteira, CarteiraProspeccao.Digitos(cnpj), ct);
        try { carteira.Enviar(vendedora, dispensar); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
        await _db.SaveChangesAsync(ct);
        return Ok(new { id = carteira.Id, status = carteira.Status.ToString() });
    }

    /// <summary>A vendedora da sessão, ou a resposta de recusa.</summary>
    private async Task<(int Vendedora, IActionResult Recusa)> VendedoraAsync(CancellationToken ct)
    {
        if (User.FindFirstValue("WlPerfil") != "Anunciante" || !int.TryParse(User.FindFirstValue("WlAnuncianteId"), out var userId))
            return (0, Unauthorized());
        var comprador = await _tenant.UsuariosAnunciante.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (comprador == null) return (0, Unauthorized());
        var sessao = await _carteira.ResolverAsync(User, comprador, ct);
        if (sessao.Modo == ModoSessaoApp.Recusada) return (0, StatusCode(403, new { message = SessaoApp.MensagemRecusada }));
        if (sessao.Modo != ModoSessaoApp.Carteira)
            return (0, StatusCode(403, new { message = "A carteira só existe na Prospecção aberta pela vendedora." }));
        return (sessao.VendedorId, null);
    }

    private Task<WlCarteiraCliente> MinhaAsync(Guid id, int vendedora, CancellationToken ct) =>
        _db.WlCarteiraClientes.Include(c => c.Documentos)
            .SingleOrDefaultAsync(c => c.Id == id && c.AfiliadaId == _tenant.AfiliadaId && c.VendedorId == vendedora, ct);

    /// <summary>Só a solicitação de cliente que passou por KYC aprovado nesta exibidora dispensa documentos.</summary>
    private async Task<bool> DocumentosDispensaveisAsync(WlCarteiraCliente carteira, string cnpj, CancellationToken ct) =>
        carteira.Origem == WlCarteiraOrigem.Solicitacao &&
        await _db.WlAppOnboardings.AnyAsync(o => o.AfiliadaId == _tenant.AfiliadaId && o.Documento == cnpj &&
            o.Status == WlAppKycStatus.Aprovado && o.Documentos.Any(d => d.Ativo), ct);

    /// <summary>Vínculo com a exibidora; o novo nasce INATIVO e só a aprovação o ativa (I4).</summary>
    private async Task<bool> GarantirVinculoAsync(Cliente cliente, int vendedora, CancellationToken ct)
    {
        if (await _db.AfiliadaClientes.AnyAsync(v => v.IdAfiliada == _tenant.AfiliadaId && v.IdCliente == cliente.Id, ct))
            return true;
        var ator = await AtorAsync(ct);
        if (ator == null) return false;
        var afiliada = await _db.Afiliadas.SingleAsync(a => a.Id == _tenant.AfiliadaId, ct);
        var vinculo = new AfiliadaCliente(afiliada, cliente, ator);
        vinculo.RegistrarOrigem(FonteOrigemEnum.WhiteLabel, afiliada.Id, vendedora);
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

    private async Task<string> NomeVendedoraAsync(int? vendedorId, CancellationToken ct)
    {
        var nome = await NomesUsuarioWl.ResolverAsync(_tenant, vendedorId, ct);
        return string.IsNullOrWhiteSpace(nome) ? "outra vendedora" : nome;
    }

    public sealed class CadastroClienteRequest
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
        public string Validar()
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
