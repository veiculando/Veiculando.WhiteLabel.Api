using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Commands.Inputs;
using Veiculando.Domain.Enums;
using Veiculando.WhiteLabel.Api.Configurations;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;

namespace Veiculando.WhiteLabel.Api.Controllers
{
    /// <summary>
    /// Valores de peças (VEI-RD-54): consulta paginada e alteração de preço em
    /// lote, delegando a regra de precificação ao <c>PecaAlterarValoresHandler</c>
    /// do core.
    /// </summary>
    [ApiController]
    [Route("api/wl/pecas/valores")]
    [Authorize(Policy = AuthorizationSetup.PecaGerenciar)]
    public class PecasValoresController : WlCoreProxyControllerBase
    {
        private readonly ITenantQueries _tenant;
        private readonly ICoreCadastroService _coreCadastro;
        private readonly VeiculandoDataContext _db;

        public PecasValoresController(ITenantQueries tenant, ICoreCadastroService coreCadastro, VeiculandoDataContext db)
        {
            _tenant = tenant;
            _coreCadastro = coreCadastro;
            _db = db;
        }

        /// <summary>
        /// Lista paginada para a grade de Valores de Peças.
        /// </summary>
        /// <remarks>
        /// Filtros de cidade, tipo de suporte e status vêm do PRD §5.4 e têm
        /// semântica inequívoca. O PRD também pede um quarto filtro — período —
        /// mas o Figma (154:3004) só mostra busca textual, e o significado de
        /// "período" numa grade de VALOR (não de disponibilidade) não está
        /// definido: filtraria peças vendáveis naquele período? Mostraria o valor
        /// sazonal daquele período em vez do padrão? Nenhuma leitura é óbvia o
        /// suficiente para decidir sem o humano — pendência deixada explícita no
        /// plano tático (Ordem 5, seção 6) e no BDD desta feature.
        /// </remarks>
        [HttpGet]
        public async Task<IActionResult> GetAll(
            [FromQuery] WlPaginaRequest pagina,
            [FromQuery] int? idCidade,
            [FromQuery] int? idTipoSuporte,
            [FromQuery] StatusExibicaoEnum? status,
            [FromQuery] string busca)
        {
            var (page, pageSize) = WlPaginacao.Normalizar(pagina);
            var sort = WlPaginacao.Ordenacao(pagina?.Sort, "codigo", "codigo", "valorPadrao", "cidade");
            var desc = pagina?.Desc ?? false;

            var query = _tenant.Pecas
                .AsNoTracking()
                .Include(p => p.Local.Cidade)
                .Include(p => p.Suporte)
                .Where(p => p.StatusExibicao != StatusExibicaoEnum.Deletado);

            if (status.HasValue)
                query = query.Where(p => p.StatusExibicao == status.Value);

            if (idCidade.HasValue)
                query = query.Where(p => p.Local.IdCidade == idCidade.Value);

            if (idTipoSuporte.HasValue)
                query = query.Where(p => p.IdTipoSuporte == idTipoSuporte.Value);

            if (!string.IsNullOrWhiteSpace(busca))
            {
                var termo = busca.Trim();
                query = query.Where(p =>
                    p.Codigo.Contains(termo) ||
                    p.CodigoInterno.Contains(termo) ||
                    p.Local.Codigo.Contains(termo) ||
                    p.Local.Cidade.Nome.Contains(termo));
            }

            var total = await query.CountAsync();

            // Desempate por Id: cidade e valor se repetem entre peças diferentes,
            // e sem ordem total a página 2 pode repetir ou pular registros que a
            // página 1 já trouxe (mesma classe de defeito descrita em
            // ProgramacaoController e PedidosReservaController).
            var ordenada = (sort, desc) switch
            {
                ("valorPadrao", false) => query.OrderBy(p => p.ValorPadrao).ThenBy(p => p.Id),
                ("valorPadrao", true) => query.OrderByDescending(p => p.ValorPadrao).ThenBy(p => p.Id),
                ("cidade", false) => query.OrderBy(p => p.Local.Cidade.Nome).ThenBy(p => p.Id),
                ("cidade", true) => query.OrderByDescending(p => p.Local.Cidade.Nome).ThenBy(p => p.Id),
                (_, false) => query.OrderBy(p => p.Codigo).ThenBy(p => p.Id),
                (_, true) => query.OrderByDescending(p => p.Codigo).ThenBy(p => p.Id),
            };

            // Formato é complex type do EF6 — comparar/projetar dentro do SQL
            // lança NotSupportedException, mesma classe de defeito documentada em
            // PecasController.GetAll. Materializa primeiro, projeta depois.
            var brutos = await ordenada
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            var itens = brutos
                .Select(p => new
                {
                    p.Id,
                    p.Codigo,
                    p.CodigoInterno,
                    Suporte = p.Suporte?.Nome,
                    Cidade = p.Local?.Cidade?.Nome,
                    Endereco = p.Local?.Endereco?.Logradouro,
                    p.ValorPadrao,
                    StatusExibicao = p.StatusExibicao
                })
                .ToList();

            return Ok(WlPaginacao.Montar(itens, page, pageSize, total));
        }

        /// <summary>
        /// Altera o valor padrão de um lote de peças (valor exato, ±R$ ou ±%).
        /// </summary>
        [HttpPost("alterar")]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public async Task<IActionResult> Alterar([FromBody] PecasAlterarValoresRequest request)
        {
            if (request?.PecasIds == null || request.PecasIds.Length == 0)
                return BadRequest(new { message = "Selecione ao menos uma peça." });

            var idsUnicos = request.PecasIds.Distinct().ToArray();

            var antes = await CarregarValoresPadraoAsync(idsUnicos);
            if (antes == null)
                return NotFound(new { message = "Uma ou mais peças não foram encontradas." });

            var command = new PecaAlterarValoresCommand
            {
                PecasIds = idsUnicos,
                Valor = request.Valor,
                TipoValor = request.TipoValor
            };

            var resposta = await _coreCadastro.AlterarValoresPecasAsync(command);
            if (!resposta.Sucesso)
                return RepassarResposta(resposta);

            // Recarrega os valores já persistidos pelo core para montar a
            // auditoria com valor anterior e novo — o resultado bruto do core
            // (PecaAlteacaoListaResult) serializa as entidades Peca inteiras, não
            // um diff, e não vale a pena o BFF reimplementar o parsing quando uma
            // segunda leitura, já recortada por tenant, dá a mesma resposta com
            // muito menos acoplamento ao formato de saída do core.
            var depois = await CarregarValoresPadraoAsync(idsUnicos);

            var alteracoes = idsUnicos.Select(id => new
            {
                Id = id,
                Codigo = depois.TryGetValue(id, out var d) ? d.Codigo : antes[id].Codigo,
                ValorAnterior = antes[id].ValorPadrao,
                ValorNovo = depois.TryGetValue(id, out var d2) ? d2.ValorPadrao : antes[id].ValorPadrao
            }).ToList();

            return Ok(new
            {
                message = "Valores alterados com sucesso.",
                quantidadeAfetada = alteracoes.Count,
                alteracoes
            });
        }

        /// <summary>
        /// Altera o valor de um lote de peças para um ou mais períodos específicos.
        /// </summary>
        /// <remarks>
        /// O cliente envia <c>IdPeriodo</c> (int) — a mesma chave usada em todo o
        /// resto do sistema (invariante "sem data livre onde o domínio exige
        /// Periodo.Id", plano tático seção 8). O <c>Codigo</c> (string) que o
        /// command do core exige é resolvido AQUI, no servidor: expor o Codigo ao
        /// frontend obrigaria <c>/lookups/periodos</c> a devolver um campo que
        /// nenhuma outra tela usa, só para este formulário reconstruir manualmente
        /// o que o próprio Id já identifica.
        /// </remarks>
        [HttpPost("sazonais")]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public async Task<IActionResult> AlterarSazonais([FromBody] PecasAlterarValoresSazonaisRequest request)
        {
            if (request?.PecasIds == null || request.PecasIds.Length == 0)
                return BadRequest(new { message = "Selecione ao menos uma peça." });

            if (request.PeriodosValor == null || request.PeriodosValor.Length == 0)
                return BadRequest(new { message = "Informe ao menos um período e valor." });

            var idsPeriodoRepetidos = request.PeriodosValor
                .GroupBy(p => p.IdPeriodo)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToArray();

            if (idsPeriodoRepetidos.Length > 0)
            {
                return BadRequest(new
                {
                    message = "Há período(s) repetido(s) no envio.",
                    periodos = idsPeriodoRepetidos
                });
            }

            var idsUnicos = request.PecasIds.Distinct().ToArray();

            var existentes = await _tenant.Pecas
                .AsNoTracking()
                .Where(p => idsUnicos.Contains(p.Id) && p.StatusExibicao != StatusExibicaoEnum.Deletado)
                .Select(p => p.Id)
                .ToListAsync();

            if (existentes.Count != idsUnicos.Length)
                return NotFound(new { message = "Uma ou mais peças não foram encontradas." });

            var idsPeriodo = request.PeriodosValor.Select(p => p.IdPeriodo).Distinct().ToArray();

            var periodos = await _db.Periodos
                .AsNoTracking()
                .Where(p => idsPeriodo.Contains(p.Id) && p.StatusExibicao != StatusExibicaoEnum.Deletado)
                .Select(p => new { p.Id, p.Codigo })
                .ToListAsync();

            if (periodos.Count != idsPeriodo.Length)
                return NotFound(new { message = "Um ou mais períodos não foram encontrados." });

            var codigoPorId = periodos.ToDictionary(p => p.Id, p => p.Codigo);

            var command = new PecaAlterarValoresSazonaisCommand
            {
                PecasIds = idsUnicos,
                PeriodosValor = request.PeriodosValor
                    .Select(p => new PecaAlterarValoresSazonaisCommand.PeriodoValor
                    {
                        Periodo = codigoPorId[p.IdPeriodo],
                        Valor = p.Valor
                    })
                    .ToList()
            };

            var resposta = await _coreCadastro.AlterarValoresSazonaisPecasAsync(command);
            if (!resposta.Sucesso)
                return RepassarResposta(resposta);

            return Ok(new
            {
                message = "Valores sazonais alterados com sucesso.",
                quantidadeAfetada = idsUnicos.Length,
                periodos = idsPeriodo
            });
        }

        /// <summary>
        /// Confirma que todas as peças pertencem à afiliada da instância e devolve
        /// o valor padrão atual de cada uma — para IDOR e auditoria na mesma
        /// consulta.
        /// </summary>
        /// <remarks>
        /// Um id de outra afiliada invalida a requisição inteira, sem
        /// processamento parcial: se a contagem não bater, nada é enviado ao
        /// core. Diferente do laço do <c>PecaAlterarValoresHandler</c> (que só
        /// para ao ENCONTRAR a peça alheia, depois de já ter alterado as
        /// anteriores do lote), a checagem acontece aqui inteira, antes de
        /// qualquer chamada remota.
        /// </remarks>
        private async Task<Dictionary<int, (string Codigo, decimal ValorPadrao)>> CarregarValoresPadraoAsync(int[] ids)
        {
            var pecas = await _tenant.Pecas
                .AsNoTracking()
                .Where(p => ids.Contains(p.Id) && p.StatusExibicao != StatusExibicaoEnum.Deletado)
                .Select(p => new { p.Id, p.Codigo, p.ValorPadrao })
                .ToListAsync();

            if (pecas.Count != ids.Length)
                return null;

            return pecas.ToDictionary(p => p.Id, p => (p.Codigo, p.ValorPadrao));
        }
    }

    public sealed class PecasAlterarValoresRequest
    {
        public int[] PecasIds { get; set; }
        public decimal Valor { get; set; }
        public AlteracaoValorTipoEnum TipoValor { get; set; }
    }

    public sealed class PecasAlterarValoresSazonaisRequest
    {
        public int[] PecasIds { get; set; }
        public PeriodoValorRequest[] PeriodosValor { get; set; }
    }

    public sealed class PeriodoValorRequest
    {
        public int IdPeriodo { get; set; }
        public decimal Valor { get; set; }
    }
}
