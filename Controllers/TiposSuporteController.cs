using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Data.Entity.Infrastructure;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Commands.Inputs;
using Veiculando.Domain.Entities;
using Veiculando.Domain.Enums;
using Veiculando.WhiteLabel.Api.Configurations;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;

namespace Veiculando.WhiteLabel.Api.Controllers
{
    /// <summary>
    /// Tipos de Suporte e Formatos da exibidora — Sprint 10.5 BE-2, PRD §5.3/§6.2.
    /// Figma 419-18656 / 419-20409.
    /// </summary>
    /// <remarks>
    /// <para><b>O que a exibidora pode e não pode.</b> TipoSuporte é catálogo
    /// central: não há rota que crie ou edite um tipo. A exibidora habilita tipos
    /// do catálogo (AfiliadaTipoSuporte) e mantém os formatos de cada tipo
    /// habilitado (AfiliadaTipoSuporteFormato).</para>
    ///
    /// <para><b>Formato é global e deduplicado por dimensão.</b> Criar um formato
    /// que já existe no catálogo associa o existente (<c>reaproveitado: true</c>)
    /// em vez de duplicar; formato novo é criado pelo FormatoHandler do core.
    /// Editar só é permitido quando o formato não é usado por nenhuma peça nem
    /// por outra associação — senão a edição reescreveria o histórico de peças
    /// e o cadastro de outras exibidoras. Nesse caso: 409, crie outro e inative.</para>
    ///
    /// <para><b>Ids nas rotas.</b> <c>{id}</c> é sempre o <c>TipoSuporte.Id</c> do
    /// catálogo e <c>{formatoId}</c> o <c>Formato.Id</c>; o recorte por afiliada vem
    /// do <see cref="ITenantQueries"/>. Tipo não habilitado aqui = 404.</para>
    /// </remarks>
    [ApiController]
    [Route("api/wl/tipos-suporte")]
    [Authorize(Policy = AuthorizationSetup.PecaGerenciar)]
    public class TiposSuporteController : WlCoreProxyControllerBase
    {
        private readonly VeiculandoDataContext _db;
        private readonly ITenantQueries _tenant;
        private readonly ICoreCadastroService _coreCadastro;

        public TiposSuporteController(
            VeiculandoDataContext db,
            ITenantQueries tenant,
            ICoreCadastroService coreCadastro)
        {
            _db = db;
            _tenant = tenant;
            _coreCadastro = coreCadastro;
        }

        /// <summary>
        /// Tipos habilitados para a exibidora, com contagens calculadas no SQL.
        /// Tipo desativado no catálogo central some daqui (PRD §5.3: "apenas tipos
        /// existentes e ativos no catálogo").
        /// </summary>
        [HttpGet("habilitados")]
        public async Task<IActionResult> Habilitados([FromQuery] string status)
        {
            var query = _tenant.AfiliadaTiposSuporte.AsNoTracking()
                .Where(h => h.TipoSuporte.StatusExibicao == StatusExibicaoEnum.Ativo);

            if (string.Equals(status, "Ativo", StringComparison.OrdinalIgnoreCase))
                query = query.Where(h => h.Status == StatusVinculoEnum.Ativo);
            else if (string.Equals(status, "Inativo", StringComparison.OrdinalIgnoreCase))
                query = query.Where(h => h.Status == StatusVinculoEnum.Inativo);

            var pecasEmOperacao = _tenant.Pecas.Where(p =>
                p.StatusExibicao == StatusExibicaoEnum.Ativo
                && p.Local.StatusExibicao == StatusExibicaoEnum.Ativo);

            var itens = await query
                .OrderBy(h => h.TipoSuporte.Ordem)
                .ThenBy(h => h.TipoSuporte.Nome)
                .Select(h => new
                {
                    Id = h.IdTipoSuporte,
                    h.TipoSuporte.Nome,
                    h.TipoSuporte.Codigo,
                    h.TipoSuporte.Icone,
                    h.Status,
                    h.DataVinculo,
                    QtdFormatos = h.Formatos.Count(),
                    QtdFormatosAtivos = h.Formatos.Count(f => f.Status == StatusVinculoEnum.Ativo),
                    QtdLocaisEmOperacao = pecasEmOperacao
                        .Where(p => p.IdTipoSuporte == h.IdTipoSuporte)
                        .Select(p => p.IdLocal)
                        .Distinct()
                        .Count()
                })
                .ToListAsync();

            // Categoria e Descricao: decisão pendente do PO (test plan cec4eea1,
            // BE-2 item 2). Sem coluna no catálogo, sempre null — nunca um valor
            // inventado que o front mostraria como dado real.
            return Ok(itens.Select(i => new
            {
                i.Id,
                i.Nome,
                i.Codigo,
                i.Icone,
                Categoria = (string)null,
                Descricao = (string)null,
                i.Status,
                i.DataVinculo,
                i.QtdFormatos,
                i.QtdFormatosAtivos,
                i.QtdLocaisEmOperacao
            }));
        }

        /// <summary>
        /// Catálogo ativo menos o que já tem habilitação aqui (ativa ou inativa —
        /// a inativa se reativa pelo PATCH, não aparece como "nova"). Alimenta o
        /// modal "Adicionar Suporte".
        /// </summary>
        [HttpGet("disponiveis")]
        public async Task<IActionResult> Disponiveis()
        {
            var habilitados = _tenant.AfiliadaTiposSuporte.Select(h => h.IdTipoSuporte);

            var itens = await _db.TiposSuporte.AsNoTracking()
                .Where(t => t.StatusExibicao == StatusExibicaoEnum.Ativo && !habilitados.Contains(t.Id))
                .OrderBy(t => t.Ordem)
                .ThenBy(t => t.Nome)
                .Select(t => new { t.Id, t.Nome, t.Codigo, t.Icone })
                .ToListAsync();

            return Ok(itens.Select(t => new
            {
                t.Id,
                t.Nome,
                t.Codigo,
                t.Icone,
                Categoria = (string)null,
                Descricao = (string)null
            }));
        }

        /// <summary>
        /// Habilita um tipo do catálogo. Idempotente: já habilitado devolve 200 com
        /// o mesmo registro; habilitação inativa é reativada. Tipo inexistente ou
        /// inativo no catálogo = 404.
        /// </summary>
        [HttpPost("{id}/habilitar")]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public async Task<IActionResult> Habilitar(int id)
        {
            var existente = await _tenant.AfiliadaTiposSuporte.FirstOrDefaultAsync(h => h.IdTipoSuporte == id);
            if (existente != null)
                return await ReativarSeInativoAsync(existente);

            var tipo = await _db.TiposSuporte.FirstOrDefaultAsync(t => t.Id == id);
            if (tipo == null || tipo.StatusExibicao != StatusExibicaoEnum.Ativo)
                return NotFound(new { message = "Tipo de suporte não encontrado no catálogo Veiculando." });

            var habilitacao = new AfiliadaTipoSuporte(_tenant.AfiliadaId, tipo);
            if (!habilitacao.IsValid())
                return BadRequest(new { message = Mensagens(habilitacao) });

            habilitacao.RegistrarOrigem(FonteOrigemEnum.WhiteLabel, _tenant.AfiliadaId, WlUsuarioId);
            _db.AfiliadaTiposSuporte.Add(habilitacao);

            try
            {
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // Dois cliques simultâneos: UK_AfiliadaTipoSuporte_Afiliada_Tipo
                // barrou o segundo INSERT. O resultado desejado já existe.
                _db.Entry(habilitacao).State = EntityState.Detached;
                var vencedor = await _tenant.AfiliadaTiposSuporte.FirstAsync(h => h.IdTipoSuporte == id);
                return await ReativarSeInativoAsync(vencedor);
            }

            return StatusCode(StatusCodes.Status201Created,
                new { id = habilitacao.IdTipoSuporte, habilitacao.Status, criado = true });
        }

        /// <summary>Ativa/inativa a habilitação (sem exclusão física).</summary>
        [HttpPatch("{id}/status")]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public async Task<IActionResult> AlterarStatus(int id, [FromBody] StatusVinculoRequest request)
        {
            var habilitacao = await _tenant.AfiliadaTiposSuporte.FirstOrDefaultAsync(h => h.IdTipoSuporte == id);
            if (habilitacao == null)
                return NotFound(new { message = "Tipo de suporte não habilitado nesta exibidora." });

            if (request?.Ativo == true)
                habilitacao.Ativar();
            else
                habilitacao.Inativar();

            await _db.SaveChangesAsync();
            return Ok(new { id = habilitacao.IdTipoSuporte, habilitacao.Status });
        }

        /// <summary>
        /// Formatos do tipo nesta exibidora. <c>status=Ativo</c> é o filtro que o
        /// cadastro de peça usa: formato inativo não pode ser escolhido em peça nova.
        /// </summary>
        [HttpGet("{id}/formatos")]
        public async Task<IActionResult> Formatos(int id, [FromQuery] string status)
        {
            var habilitado = await _tenant.AfiliadaTiposSuporte.AnyAsync(h => h.IdTipoSuporte == id);
            if (!habilitado)
                return NotFound(new { message = "Tipo de suporte não habilitado nesta exibidora." });

            var query = _tenant.AfiliadaTipoSuporteFormatos.AsNoTracking()
                .Where(f => f.AfiliadaTipoSuporte.IdTipoSuporte == id);

            if (string.Equals(status, "Ativo", StringComparison.OrdinalIgnoreCase))
                query = query.Where(f => f.Status == StatusVinculoEnum.Ativo);
            else if (string.Equals(status, "Inativo", StringComparison.OrdinalIgnoreCase))
                query = query.Where(f => f.Status == StatusVinculoEnum.Inativo);

            var vinculos = await query
                .Include(f => f.Formato)
                .OrderBy(f => f.Formato.MidiaTipo)
                .ThenBy(f => f.Formato.Largura)
                .ThenBy(f => f.Formato.Altura)
                .ThenBy(f => f.IdFormato)
                .ToListAsync();

            // Nome/Dimensao/Resolucao são propriedades calculadas do Formato
            // (Ignore no FormatoMap): só existem depois de materializar.
            return Ok(vinculos.Select(v => ParaResposta(v.Formato, v.Status)));
        }

        /// <summary>
        /// Cria um formato para o tipo. Dimensão já existente no catálogo associa o
        /// formato existente (200, <c>reaproveitado: true</c>, especificações do
        /// payload NÃO aplicadas ao formato compartilhado); dimensão nova cria pelo
        /// core (201).
        /// </summary>
        [HttpPost("{id}/formatos")]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public async Task<IActionResult> CriarFormato(int id, [FromBody] FormatoRequest request)
        {
            if (request == null)
                return BadRequest(new { message = "Dados do formato são obrigatórios." });

            var habilitacao = await _tenant.AfiliadaTiposSuporte.FirstOrDefaultAsync(h => h.IdTipoSuporte == id);
            if (habilitacao == null)
                return NotFound(new { message = "Tipo de suporte não habilitado nesta exibidora." });
            if (habilitacao.Status != StatusVinculoEnum.Ativo)
                return Conflict(new { message = "Reative o tipo de suporte antes de cadastrar formatos." });

            var command = ParaCommand(request, id: 0);
            var erro = ValidarNoDominio(command);
            if (erro != null) return erro;

            var formato = await BuscarPorDimensaoAsync(command);
            var reaproveitado = formato != null;

            if (formato == null)
            {
                var resposta = await _coreCadastro.SalvarFormatoAsync(command);
                if (!resposta.Sucesso)
                    return RepassarResposta(resposta);

                formato = await BuscarPorDimensaoAsync(command);
                if (formato == null)
                    return StatusCode(StatusCodes.Status502BadGateway, new
                    {
                        message = "O Veiculando Core confirmou o formato, mas ele não foi encontrado no catálogo."
                    });
            }

            var jaAssociado = await _tenant.AfiliadaTipoSuporteFormatos.AsNoTracking()
                .Where(f => f.IdAfiliadaTipoSuporte == habilitacao.Id && f.IdFormato == formato.Id)
                .Select(f => new { f.Status })
                .FirstOrDefaultAsync();
            if (jaAssociado != null)
                return Conflict(new
                {
                    message = "Este formato já está associado ao tipo de suporte.",
                    id = formato.Id,
                    status = jaAssociado.Status
                });

            var vinculo = new AfiliadaTipoSuporteFormato(habilitacao, formato);
            vinculo.RegistrarOrigem(FonteOrigemEnum.WhiteLabel, _tenant.AfiliadaId, WlUsuarioId);
            _db.AfiliadaTipoSuporteFormatos.Add(vinculo);
            await _db.SaveChangesAsync();

            var corpo = new { formato = ParaResposta(formato, vinculo.Status), reaproveitado };
            return reaproveitado ? Ok(corpo) : StatusCode(StatusCodes.Status201Created, corpo);
        }

        /// <summary>
        /// Edita o formato — só quando ele é exclusivo desta associação (nenhuma
        /// peça e nenhuma outra associação o usa). Caso contrário 409: editar
        /// reescreveria o histórico das peças e o cadastro de outras exibidoras.
        /// </summary>
        [HttpPut("{id}/formatos/{formatoId}")]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public async Task<IActionResult> EditarFormato(int id, int formatoId, [FromBody] FormatoRequest request)
        {
            if (request == null)
                return BadRequest(new { message = "Dados do formato são obrigatórios." });

            var vinculo = await BuscarVinculoAsync(id, formatoId);
            if (vinculo == null)
                return NotFound(new { message = "Formato não encontrado para este tipo de suporte." });

            var command = ParaCommand(request, formatoId);
            var erro = ValidarNoDominio(command);
            if (erro != null) return erro;

            var emUsoPorPeca = await _db.Pecas.AnyAsync(p => p.IdFormatoArteFinal == formatoId);
            var outrasAssociacoes = await _db.AfiliadaTipoSuporteFormatos
                .AnyAsync(f => f.IdFormato == formatoId && f.Id != vinculo.Id);
            if (emUsoPorPeca || outrasAssociacoes)
                return Conflict(new
                {
                    message = "Formato em uso por peças ou por outro cadastro. Para mudar, crie um novo formato e inative este."
                });

            var resposta = await _coreCadastro.SalvarFormatoAsync(command);
            if (!resposta.Sucesso)
                return RepassarResposta(resposta);

            var atualizado = await _db.Formatos.AsNoTracking().FirstAsync(f => f.Id == formatoId);
            return Ok(ParaResposta(atualizado, vinculo.Status));
        }

        /// <summary>Ativa/inativa o formato no tipo (sem exclusão física).</summary>
        [HttpPatch("{id}/formatos/{formatoId}/status")]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public async Task<IActionResult> AlterarStatusFormato(int id, int formatoId, [FromBody] StatusVinculoRequest request)
        {
            var vinculo = await BuscarVinculoAsync(id, formatoId);
            if (vinculo == null)
                return NotFound(new { message = "Formato não encontrado para este tipo de suporte." });

            if (request?.Ativo == true)
                vinculo.Ativar();
            else
                vinculo.Inativar();

            await _db.SaveChangesAsync();
            return Ok(new { id = vinculo.IdFormato, vinculo.Status });
        }

        private async Task<IActionResult> ReativarSeInativoAsync(AfiliadaTipoSuporte habilitacao)
        {
            if (habilitacao.Status != StatusVinculoEnum.Ativo)
            {
                habilitacao.Ativar();
                await _db.SaveChangesAsync();
            }

            return Ok(new { id = habilitacao.IdTipoSuporte, habilitacao.Status, criado = false });
        }

        private Task<AfiliadaTipoSuporteFormato> BuscarVinculoAsync(int idTipoSuporte, int formatoId) =>
            _tenant.AfiliadaTipoSuporteFormatos
                .FirstOrDefaultAsync(f => f.AfiliadaTipoSuporte.IdTipoSuporte == idTipoSuporte && f.IdFormato == formatoId);

        /// <summary>Mesma chave de <c>FormatoRepository.FormatoJaExite</c> do core.</summary>
        /// <remarks>
        /// RASTREADO de propósito, sem AsNoTracking: o resultado vira navegação do
        /// vínculo novo, e no Add o EF6 marca como Added todo objeto do grafo que
        /// não conhece — um Formato destacado seria inserido de novo no catálogo.
        /// </remarks>
        private Task<Formato> BuscarPorDimensaoAsync(FormatoCadastroCommand c)
        {
            var resolucaoLargura = c.ResolucaoLargura ?? 0;
            var resolucaoAltura = c.ResolucaoAltura ?? 0;
            var duracao = c.Duracao ?? 0;

            return _db.Formatos
                .Where(f => f.MidiaTipo == c.MidiaTipo
                         && f.Largura == c.Largura
                         && f.Altura == c.Altura
                         && f.ResolucaoLargura == resolucaoLargura
                         && f.ResolucaoAltura == resolucaoAltura
                         && f.Duracao == duracao)
                .OrderBy(f => f.Id)
                .FirstOrDefaultAsync();
        }

        /// <summary>
        /// Valida com a própria entidade do core antes de qualquer escrita, para
        /// devolver 400 com a mensagem do domínio em vez de depender do repasse.
        /// </summary>
        private IActionResult ValidarNoDominio(FormatoCadastroCommand c)
        {
            if (!Enum.IsDefined(typeof(MidiaTipoEnum), c.MidiaTipo))
                return BadRequest(new { message = "Tipo de mídia inválido. Use 1 (estática) ou 2 (digital)." });

            var candidato = new Formato(c.MidiaTipo, c.Largura, c.Altura,
                c.ResolucaoLargura ?? 0, c.ResolucaoAltura ?? 0, c.Duracao ?? 0,
                c.EspecificacaoCriacao, c.EspecificacaoArquivo, c.ExtensoesAceitas, null);

            return candidato.IsValid() ? null : BadRequest(new { message = Mensagens(candidato) });
        }

        private static FormatoCadastroCommand ParaCommand(FormatoRequest r, int id)
        {
            var digital = r.MidiaTipo == MidiaTipoEnum.Digital;

            return new FormatoCadastroCommand
            {
                Id = id,
                MidiaTipo = r.MidiaTipo,
                Largura = r.Largura,
                Altura = r.Altura,
                // Mídia estática não tem resolução nem duração. Zerar aqui mantém a
                // deduplicação do core coerente: "9 x 3 m" é um formato só, venha
                // ou não um número perdido nesses campos.
                ResolucaoLargura = digital ? r.ResolucaoLargura : (short)0,
                ResolucaoAltura = digital ? r.ResolucaoAltura : (short)0,
                Duracao = digital ? r.Duracao : (short)0,
                EspecificacaoCriacao = r.EspecificacaoCriacao,
                EspecificacaoArquivo = r.EspecificacaoArquivo,
                ExtensoesAceitas = r.ExtensoesAceitas?
                    .Where(e => !string.IsNullOrWhiteSpace(e))
                    .Select(e => e.Trim().ToLowerInvariant())
                    .ToArray()
            };
        }

        private static object ParaResposta(Formato f, StatusVinculoEnum status) => new
        {
            f.Id,
            f.Nome,
            f.Dimensao,
            f.MidiaTipo,
            f.Largura,
            f.Altura,
            f.ResolucaoLargura,
            f.ResolucaoAltura,
            f.Duracao,
            f.Resolucao,
            f.EspecificacaoCriacao,
            f.EspecificacaoArquivo,
            ExtensoesAceitas = f.ExtensoesAceitasArray,
            Status = status
        };

        private static List<string> Mensagens(Veiculando.Shared.Notifications.Notifiable n) =>
            n.Notifications.Select(x => x.Message).ToList();
    }

    public sealed class FormatoRequest
    {
        public MidiaTipoEnum MidiaTipo { get; set; }
        public decimal Largura { get; set; }
        public decimal Altura { get; set; }
        public short? ResolucaoLargura { get; set; }
        public short? ResolucaoAltura { get; set; }
        public short? Duracao { get; set; }
        public string EspecificacaoCriacao { get; set; }
        public string EspecificacaoArquivo { get; set; }
        public string[] ExtensoesAceitas { get; set; }
    }

    public sealed class StatusVinculoRequest
    {
        public bool Ativo { get; set; }
    }
}
