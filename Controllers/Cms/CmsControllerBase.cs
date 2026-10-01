using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using Veiculando.WhiteLabel.Api.Configurations;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;
using Veiculando.WhiteLabel.Api.Services.Cms;

namespace Veiculando.WhiteLabel.Api.Controllers.Cms;

/// <summary>Corpo de <c>PATCH /{id}/status</c>.</summary>
public sealed class CmsStatusRequest
{
    public bool? Ativo { get; set; }
}

/// <summary>
/// O que os três recursos do CMS (banners, marcas, depoimentos) têm em comum:
/// autorização, 503, detalhe, status, paginação, criação e atualização
/// auditadas.
/// </summary>
/// <remarks>
/// <para><b>Camadas antes daqui.</b> O <c>CmsModuloMiddleware</c> já respondeu 404
/// se o módulo está desligado para a afiliada; a policy <c>ConteudoGerenciar</c>
/// responde 403; o <see cref="CmsIndisponivelFiltro"/> transforma Supabase fora do
/// ar em 503.</para>
///
/// <para><b>Sem DELETE.</b> O Figma só tem editar e desligar: inativar é a remoção.
/// Um <c>DELETE</c> em <c>/{id}</c> recebe 405 do roteamento.</para>
///
/// <para><b>Toda escrita</b> exige autor lido do servidor (401 antes de escrever),
/// grava em <c>cms_auditoria</c> e pede a varredura de arquivos antigos, que roda
/// depois da resposta (VEI-RD-19f).</para>
/// </remarks>
[ApiController]
[Authorize(Policy = AuthorizationSetup.ConteudoGerenciar)]
[ServiceFilter(typeof(CmsIndisponivelFiltro))]
public abstract class CmsControllerBase<TLinha> : ControllerBase where TLinha : class
{
    /// <summary>Ordem do site (a mesma da LP), com id como desempate para paginar sem repetir.</summary>
    protected const string Ordem = "order=display_order.asc,created_at.asc,id.asc";

    protected const int TamanhoMaximoTexto = 150;

    private static readonly Regex Ancora = new(@"^#[\w-]+$", RegexOptions.Compiled);

    protected CmsControllerBase(CmsDependencias dependencias) => D = dependencias;

    protected CmsDependencias D { get; }

    /// <summary>Tabela no Supabase, que é também o <c>recurso</c> da auditoria e o prefixo dos objetos.</summary>
    protected abstract string Tabela { get; }

    /// <summary>"Banner", "Marca"...: para as mensagens ao operador.</summary>
    protected abstract string Nome { get; }

    protected abstract object ParaDto(TLinha linha);

    protected abstract Guid IdDe(TLinha linha);

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Detalhe(Guid id, CancellationToken ct)
    {
        var linha = await BuscarAsync(id, ct);
        return linha == null ? NaoEncontrado() : Ok(ParaDto(linha));
    }

    /// <summary>Publicar ou ocultar. Resposta: <c>{ item, avisos: [] }</c>, como no POST e no PUT.</summary>
    [HttpPatch("{id:guid}/status")]
    [EnableRateLimiting(Startup.RateLimitEscrita)]
    public async Task<IActionResult> AlterarStatus(Guid id, [FromBody] CmsStatusRequest corpo, CancellationToken ct)
    {
        if (corpo?.Ativo == null)
            return Erro(StatusCodes.Status400BadRequest, "Informe ativo: true ou false.");
        if (!CmsAutor.TentarLer(User, D.Tenant, out var autor))
            return SemAutor();

        var atual = await BuscarAsync(id, ct);
        if (atual == null)
            return NaoEncontrado();

        var alteradas = await D.Supabase.AtualizarAsync<TLinha>(Tabela, PostgrestFiltro.PorId(id),
            new Dictionary<string, object> { ["is_active"] = corpo.Ativo.Value }, ct);
        if (alteradas.Count == 0)
            return NaoEncontrado();

        await D.Auditoria.RegistrarAsync(autor, corpo.Ativo.Value ? CmsAcao.Ativar : CmsAcao.Inativar,
            Tabela, id, atual, alteradas[0]);
        D.Varredura.Solicitar();

        return Ok(Salvo(alteradas[0], Array.Empty<string>()));
    }

    protected async Task<IActionResult> ListarAsync(WlPaginaRequest pagina, string status, IEnumerable<string> filtros, CancellationToken ct)
    {
        if (!PostgrestFiltro.TentarStatus(status, out var filtroStatus))
            return Erro(StatusCodes.Status400BadRequest, "Status inválido. Use todos, ativo ou inativo.");

        var (page, pageSize) = WlPaginacao.Normalizar(pagina);
        var query = Juntar(new[] { "select=*", filtroStatus }.Concat(filtros).Append(Ordem));

        var resultado = await D.Supabase.ListarAsync<TLinha>(Tabela, query, pageSize, (page - 1) * pageSize, ct);
        return Ok(WlPaginacao.Montar(resultado.Itens.Select(ParaDto).ToList(), page, pageSize, resultado.Total));
    }

    /// <summary>
    /// POST: sobe os arquivos, grava a linha, audita e pede a varredura. Se a
    /// linha não for gravada, os arquivos que subiram são removidos.
    /// </summary>
    protected async Task<IActionResult> CriarAsync(CmsAutor autor, Dictionary<string, object> linha,
        IReadOnlyList<(string Coluna, string Bucket, CmsArquivo Arquivo)> arquivos, IReadOnlyList<string> avisos, CancellationToken ct)
    {
        // Ativo ausente = inativo, e o campo vai SEMPRE no corpo: o default da
        // tabela era true e mudou no TP-2. As duas barreiras são intencionais,
        // porque um registro ativo aparece no site de produção (ADR-CMS-004).
        if (!linha.ContainsKey("is_active") || linha["is_active"] == null)
            linha["is_active"] = false;

        if (!linha.ContainsKey("display_order") || linha["display_order"] == null)
            linha["display_order"] = await ProximaOrdemAsync(ct);

        var enviados = new List<CmsObjeto>();
        TLinha gravada;
        try
        {
            foreach (var (coluna, bucket, arquivo) in arquivos)
            {
                var objeto = new CmsObjeto(bucket, CmsArquivos.NomeObjeto(Tabela, arquivo.Extensao));
                using var conteudo = new MemoryStream(arquivo.Conteudo, writable: false);
                await D.Supabase.EnviarObjetoAsync(objeto.Bucket, objeto.Caminho, conteudo, arquivo.ContentType, ct);
                enviados.Add(objeto);
                linha[coluna] = D.Objetos.ValorDaColuna(objeto);
            }

            gravada = await D.Supabase.InserirAsync<TLinha>(Tabela, linha, ct);
        }
        catch
        {
            foreach (var objeto in enviados)
            {
                try
                {
                    await D.Supabase.RemoverObjetoAsync(objeto.Bucket, objeto.Caminho, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    // Nenhuma linha aponta para ele, então não quebra nada no site;
                    // fica o caminho no log para limpeza manual.
                    D.Logger.LogWarning(ex, "POST em {Tabela} falhou e o arquivo enviado ficou órfão: {Bucket}/{Caminho}",
                        Tabela, objeto.Bucket, objeto.Caminho);
                }
            }
            throw;
        }

        await D.Auditoria.RegistrarAsync(autor, CmsAcao.Criar, Tabela, IdDe(gravada), null, gravada);
        D.Varredura.Solicitar();

        return StatusCode(StatusCodes.Status201Created, Salvo(gravada, avisos));
    }

    /// <summary>PUT: troca de arquivo (VEI-RD-19f), PATCH, auditoria e varredura.</summary>
    protected async Task<IActionResult> AtualizarAsync(Guid id, CmsAutor autor, TLinha atual,
        Dictionary<string, object> alteracoes, IReadOnlyList<CmsTroca> trocas, IReadOnlyList<string> avisos, CancellationToken ct)
    {
        var resultado = await D.Troca.AtualizarAsync(Tabela, id, atual, alteracoes, trocas, autor, ct);
        if (resultado.NaoEncontrada)
            return NaoEncontrado();

        await D.Auditoria.RegistrarAsync(autor, CmsAcao.Editar, Tabela, id, atual, resultado.Linha);
        D.Varredura.Solicitar();

        return Ok(Salvo(resultado.Linha, avisos));
    }

    protected async Task<TLinha> BuscarAsync(Guid id, CancellationToken ct) =>
        (await D.Supabase.SelecionarAsync<TLinha>(Tabela, $"select=*&{PostgrestFiltro.PorId(id)}", ct)).FirstOrDefault();

    /// <summary>
    /// <c>https://</c> absoluta ou âncora <c>#secao</c> da própria LP. Qualquer outra
    /// coisa (<c>http://</c>, <c>javascript:</c>, caminho relativo) é recusada: o
    /// destino vira o href do slide no site.
    /// </summary>
    protected static bool DestinoValido(string destino) =>
        !string.IsNullOrWhiteSpace(destino)
        && (Ancora.IsMatch(destino)
            || (Uri.TryCreate(destino, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps
                && !string.IsNullOrEmpty(uri.Host)));

    /// <summary>Texto obrigatório: aparado, não vazio, até <paramref name="maximo"/>.</summary>
    protected bool TentarTexto(string valor, string campo, int maximo, out string limpo, out IActionResult erro)
    {
        limpo = valor?.Trim();
        erro = null;

        if (string.IsNullOrEmpty(limpo))
            erro = Erro(StatusCodes.Status400BadRequest, $"Informe {campo}.");
        else if (limpo.Length > maximo)
            erro = Erro(StatusCodes.Status400BadRequest, $"{Primeira(campo)} passa do limite de {maximo} caracteres.");

        return erro == null;
    }

    /// <summary>Texto opcional: vazio vira null.</summary>
    protected static string Opcional(string valor) =>
        string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();

    protected IActionResult Erro(int status, string mensagem) =>
        StatusCode(status, new CmsErroDto { Message = mensagem });

    protected IActionResult NaoEncontrado() => Erro(StatusCodes.Status404NotFound, $"{Nome} não encontrado.");

    protected IActionResult SemAutor() =>
        Erro(StatusCodes.Status401Unauthorized, "Sessão sem identificação do operador. Entre de novo.");

    private CmsSalvoDto<object> Salvo(TLinha linha, IReadOnlyList<string> avisos) =>
        new() { Item = ParaDto(linha), Avisos = avisos ?? Array.Empty<string>() };

    private async Task<int> ProximaOrdemAsync(CancellationToken ct)
    {
        var maior = await D.Supabase.SelecionarAsync<CmsOrdemLinha>(Tabela,
            "select=display_order&order=display_order.desc&limit=1", ct);
        return (maior.FirstOrDefault()?.DisplayOrder ?? 0) + 1;
    }

    private static string Juntar(IEnumerable<string> partes) =>
        string.Join("&", partes.Where(p => !string.IsNullOrEmpty(p)));

    private static string Primeira(string texto) =>
        string.IsNullOrEmpty(texto) ? texto : char.ToUpperInvariant(texto[0]) + texto[1..];

    private sealed class CmsOrdemLinha
    {
        public int DisplayOrder { get; set; }
    }
}

/// <summary>Os serviços que todo controller do CMS usa, num só parâmetro de construtor.</summary>
public sealed class CmsDependencias
{
    public CmsDependencias(ISupabaseCmsClient supabase, ITenantContext tenant, ICmsAuditoria auditoria,
        CmsTrocaArquivo troca, CmsObjetos objetos, ICmsVarreduraAgendador varredura, ILogger<CmsDependencias> logger)
    {
        Supabase = supabase;
        Tenant = tenant;
        Auditoria = auditoria;
        Troca = troca;
        Objetos = objetos;
        Varredura = varredura;
        Logger = logger;
    }

    public ILogger Logger { get; }

    public ISupabaseCmsClient Supabase { get; }
    public ITenantContext Tenant { get; }
    public ICmsAuditoria Auditoria { get; }
    public CmsTrocaArquivo Troca { get; }
    public CmsObjetos Objetos { get; }
    public ICmsVarreduraAgendador Varredura { get; }
}
