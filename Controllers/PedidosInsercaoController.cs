using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Enums;
using Veiculando.Domain.Services;
using Veiculando.WhiteLabel.Api.Configurations;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;

namespace Veiculando.WhiteLabel.Api.Controllers
{
    [ApiController]
    [Route("api/wl/pedidos-insercao")]
    [Authorize(Policy = AuthorizationSetup.PedidoInsercaoGerenciar)]
    public class PedidosInsercaoController : ControllerBase
    {
        private readonly VeiculandoDataContext _db;
        private readonly ITenantQueries _tenant;
        private readonly IWlPiPdfSource _pdf;
        private readonly ILogger<PedidosInsercaoController> _logger;

        public PedidosInsercaoController(
            VeiculandoDataContext db,
            ITenantQueries tenant,
            IWlPiPdfSource pdf,
            ILogger<PedidosInsercaoController> logger)
        {
            _db = db;
            _tenant = tenant;
            _pdf = pdf;
            _logger = logger;
        }

        /// <summary>
        /// Mensagem exata do par Período Inicial/Final invertido — mesmo texto de
        /// <c>ProgramacaoController</c> (VEI-RD-86), pela mesma razão: o frontend
        /// pode comparar a string, não só o status code.
        /// </summary>
        public const string MsgPeriodoInvertido = "Período inicial deve ser anterior ou igual ao período final";

        /// <summary>
        /// Lista os Pedidos de Inserção (PIs) da exibidora com colunas Anunciante e Agência (TP-3),
        /// filtros e o resumo agregado do card VEI-RD-94.
        /// </summary>
        /// <remarks>
        /// Não devolve mais <c>PdfUrl</c>. O campo carregava o host do
        /// <c>FILE_SERVER_URL</c> até o browser, e o FileServer não autentica
        /// ninguém nem filtra por afiliada — ver <see cref="IWlPiPdfSource"/>. O
        /// PDF agora sai por <c>GET {codigo}/pdf</c> neste mesmo controller, que
        /// é onde o recorte de tenant existe.
        ///
        /// <para><b>Resumo na mesma resposta, não em <c>/resumo</c> separado.</b>
        /// O card oferece as duas formas e prefere explicitamente esta: uma
        /// segunda rota recalculando o mesmo agregado sobre um filtro parecido —
        /// mas não idêntico, por um bug de digitação num parâmetro — é exatamente
        /// o jeito de o card e a lista divergirem silenciosamente. Aqui o resumo
        /// sai da MESMA query filtrada, antes do <c>Skip/Take</c>.</para>
        /// </remarks>
        [HttpGet]
        public async Task<IActionResult> GetAll(
            [FromQuery] string busca,
            [FromQuery] string status,
            [FromQuery] int? idPeriodoInicial,
            [FromQuery] int? idPeriodoFinal,
            [FromQuery] WlPaginaRequest pagina)
        {
            var (page, pageSize) = WlPaginacao.Normalizar(pagina);
            var sort = WlPaginacao.Ordenacao(
                pagina?.Sort, "dataCadastro",
                "codigo", "dataCadastro", "status", "valor",
                "periodo", "cidade", "anunciante", "agencia", "campanha", "dataPedido");
            var desc = pagina?.Desc ?? true;

            var query = _tenant.PedidosInsercao
                .Where(pi => pi.StatusExibicao == StatusExibicaoEnum.Ativo);

            // Apenas os status oficiais do dominio (VEI-RD-94): Rejeitado,
            // Cancelado, Novo, Aprovado, Checking, Veiculado — que sao,
            // literalmente, o enum inteiro. Nao existe rotulo do Figma
            // ("Faturado", "Confirmado" etc.) para traduzir; um status fora
            // dessa lista e erro do cliente, nao filtro vazio.
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!Enum.TryParse<StatusPedidoInsercaoEnum>(status, ignoreCase: true, out var statusEnum))
                {
                    return BadRequest(new
                    {
                        message = "Status inválido. Valores aceitos: Rejeitado, Cancelado, Novo, Aprovado, Checking, Veiculado."
                    });
                }
                query = query.Where(pi => pi.Status == statusEnum);
            }

            if (!string.IsNullOrWhiteSpace(busca))
            {
                var termo = busca.Trim();
                query = query.Where(pi =>
                    pi.Codigo.Contains(termo) ||
                    pi.Pedido.Campanha.Nome.Contains(termo) ||
                    pi.Pedido.Campanha.Cliente.Nome.Contains(termo) ||
                    pi.Pedido.Campanha.Agencia.Nome.Contains(termo));
            }

            // Mesmo desenho de Periodo Inicial/Final do card VEI-RD-86: um
            // INTERVALO de datas, validado contra Periodo.DataInicio antes de
            // tocar a query principal. Uma PI casa se ALGUM item dela cai no
            // intervalo — ela pode ter itens de mais de um periodo.
            if (idPeriodoInicial.HasValue || idPeriodoFinal.HasValue)
            {
                DateTime? dataInicio = null;
                DateTime? dataFim = null;

                if (idPeriodoInicial.HasValue)
                {
                    dataInicio = await _db.Periodos
                        .Where(p => p.Id == idPeriodoInicial.Value)
                        .Select(p => (DateTime?)p.DataInicio)
                        .FirstOrDefaultAsync();

                    if (dataInicio == null)
                        return BadRequest(new { message = "Período inicial não encontrado." });
                }

                if (idPeriodoFinal.HasValue)
                {
                    dataFim = await _db.Periodos
                        .Where(p => p.Id == idPeriodoFinal.Value)
                        .Select(p => (DateTime?)p.DataInicio)
                        .FirstOrDefaultAsync();

                    if (dataFim == null)
                        return BadRequest(new { message = "Período final não encontrado." });
                }

                if (dataInicio.HasValue && dataFim.HasValue && dataInicio.Value > dataFim.Value)
                    return BadRequest(new { message = MsgPeriodoInvertido });

                // UM UNICO item precisa satisfazer os dois limites ao mesmo
                // tempo. Dois `.Where(...Any(...))` encadeados checariam "algum
                // item >= inicio" E "algum item <= fim" separadamente — uma PI
                // com um item de Marco e outro de Janeiro passaria num filtro
                // "Fevereiro a Fevereiro" porque cada limite acha um item
                // diferente. Por isso os dois bounds entram no MESMO Any.
                if (dataInicio.HasValue || dataFim.HasValue)
                {
                    query = query.Where(pi => pi.Itens.Any(i =>
                        (!dataInicio.HasValue || i.PedidoItem.Periodo.DataInicio >= dataInicio.Value) &&
                        (!dataFim.HasValue || i.PedidoItem.Periodo.DataInicio <= dataFim.Value)));
                }
            }

            var total = await query.CountAsync();

            // Resumo (VEI-RD-94): sobre o MESMO `query` filtrado, antes de
            // Skip/Take — nunca a lista inteira carregada no navegador.
            var resumo = await MontarResumoAsync(query);

            // O desempate por Id e o que torna a paginacao estavel: sem ele, PIs
            // com o mesmo DataCadastro (ou o mesmo Status) nao tem ordem total e
            // o SQL Server pode devolve-las em ordem diferente entre duas
            // consultas — a pagina 2 repetiria ou pularia registros da 1.
            var ordenada = (sort, desc) switch
            {
                ("codigo", false) => query.OrderBy(pi => pi.Codigo).ThenBy(pi => pi.Id),
                ("codigo", true) => query.OrderByDescending(pi => pi.Codigo).ThenBy(pi => pi.Id),
                ("status", false) => query.OrderBy(pi => pi.Status).ThenBy(pi => pi.Id),
                ("status", true) => query.OrderByDescending(pi => pi.Status).ThenBy(pi => pi.Id),
                ("valor", false) => query.OrderBy(pi => pi.ValorLiquidoVeiculacao).ThenBy(pi => pi.Id),
                ("valor", true) => query.OrderByDescending(pi => pi.ValorLiquidoVeiculacao).ThenBy(pi => pi.Id),
                ("periodo", false) => query.OrderBy(pi => pi.Itens.Min(i => (DateTime?)i.PedidoItem.Periodo.DataInicio)).ThenBy(pi => pi.Id),
                ("periodo", true) => query.OrderByDescending(pi => pi.Itens.Min(i => (DateTime?)i.PedidoItem.Periodo.DataInicio)).ThenBy(pi => pi.Id),
                ("cidade", false) => query.OrderBy(pi => pi.Itens.Min(i => i.PedidoItem.Peca.Local.Cidade.Nome)).ThenBy(pi => pi.Id),
                ("cidade", true) => query.OrderByDescending(pi => pi.Itens.Min(i => i.PedidoItem.Peca.Local.Cidade.Nome)).ThenBy(pi => pi.Id),
                ("anunciante", false) => query.OrderBy(pi => pi.Pedido.Campanha.Cliente.Nome).ThenBy(pi => pi.Id),
                ("anunciante", true) => query.OrderByDescending(pi => pi.Pedido.Campanha.Cliente.Nome).ThenBy(pi => pi.Id),
                ("agencia", false) => query.OrderBy(pi => pi.Pedido.Campanha.Agencia.Nome).ThenBy(pi => pi.Id),
                ("agencia", true) => query.OrderByDescending(pi => pi.Pedido.Campanha.Agencia.Nome).ThenBy(pi => pi.Id),
                ("campanha", false) => query.OrderBy(pi => pi.Pedido.Campanha.Nome).ThenBy(pi => pi.Id),
                ("campanha", true) => query.OrderByDescending(pi => pi.Pedido.Campanha.Nome).ThenBy(pi => pi.Id),
                ("dataPedido", false) => query.OrderBy(pi => pi.Pedido.DataCadastro).ThenBy(pi => pi.Id),
                ("dataPedido", true) => query.OrderByDescending(pi => pi.Pedido.DataCadastro).ThenBy(pi => pi.Id),
                (_, false) => query.OrderBy(pi => pi.DataCadastro).ThenBy(pi => pi.Id),
                (_, true) => query.OrderByDescending(pi => pi.DataCadastro).ThenBy(pi => pi.Id),
            };

            // A interpolacao de string vira String.Format, que o EF6 nao traduz:
            // dentro do Select isso lancava NotSupportedException e a listagem de
            // PIs respondia 500 sempre. O mesmo vale para `Status.ToString()`.
            //
            // Materializa as colunas reais primeiro e monta Status depois, ja em
            // memoria. Mesma classe de defeito de Periodo.Nome em
            // LookupsController e ProgramacaoController.
            var brutos = await ordenada
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(pi => new
                {
                    pi.Id,
                    pi.Codigo,
                    pi.DataCadastro,
                    DataPedido = pi.Pedido.DataCadastro,
                    pi.Status,
                    Campanha = pi.Pedido.Campanha.Nome,
                    // Agencia.Nome nunca sai null "em silencio": VEI-RD-79 garante
                    // que toda Campanha tem uma Agencia (a de verdade, ou o
                    // espelho de venda direta). O `?.` aqui e so defesa contra
                    // dado legado sem a migracao de backfill; o fallback e o
                    // MESMO texto que a agencia-espelho usa.
                    Agencia = pi.Pedido.Campanha.Agencia != null ? pi.Pedido.Campanha.Agencia.Nome : null,
                    Anunciante = pi.Pedido.Campanha.Cliente != null ? pi.Pedido.Campanha.Cliente.Nome : null,
                    pi.ValorLiquidoVeiculacao,
                    ItensCount = pi.Itens.Count,
                })
                .ToListAsync();

            var pis = brutos
                .Select(pi => new
                {
                    pi.Id,
                    pi.Codigo,
                    pi.DataCadastro,
                    pi.DataPedido,
                    Status = pi.Status.ToString(),
                    pi.Campanha,
                    Agencia = pi.Agencia ?? AgenciaVendaDiretaProvisionamento.NomeFantasia,
                    pi.Anunciante,
                    pi.ValorLiquidoVeiculacao,
                    pi.ItensCount,
                })
                .ToList();

            var paginaMontada = WlPaginacao.Montar(pis, page, pageSize, total);

            return Ok(new
            {
                paginaMontada.Itens,
                paginaMontada.Page,
                paginaMontada.PageSize,
                paginaMontada.Total,
                paginaMontada.TotalPaginas,
                Resumo = resumo
            });
        }

        /// <summary>
        /// Agregados do card VEI-RD-94, calculados no servidor sobre o MESMO
        /// <paramref name="query"/> filtrado que alimenta a listagem — nunca a
        /// lista inteira carregada e somada no navegador.
        /// </summary>
        private async Task<object> MontarResumoAsync(IQueryable<Veiculando.Domain.Entities.Pedidos.PedidoInsercao> query)
        {
            var totais = await query
                .GroupBy(pi => 1)
                .Select(g => new
                {
                    TotalPIs = g.Count(),
                    TotalPecas = g.Sum(pi => pi.Itens.Count),
                    ValorLiquidoTotal = g.Sum(pi => pi.ValorLiquidoVeiculacao)
                })
                .FirstOrDefaultAsync();

            // GroupBy(pi => pi.Status) + Select(g => g.Key.ToString()) e a mesma
            // classe de defeito documentada acima (String/enum formatado dentro
            // do Select traduzido): materializa o enum cru primeiro.
            var porStatusBruto = await query
                .GroupBy(pi => pi.Status)
                .Select(g => new
                {
                    Status = g.Key,
                    Quantidade = g.Count(),
                    Valor = g.Sum(pi => pi.ValorLiquidoVeiculacao)
                })
                .ToListAsync();

            // Os 6 status oficiais sempre aparecem, mesmo com 0 no conjunto
            // filtrado — um dashboard que some a coluna "Cancelado" quando não
            // há PI cancelada no filtro atual é pior do que mostrar zero.
            var porStatus = new List<object>();
            foreach (StatusPedidoInsercaoEnum statusOficial in Enum.GetValues(typeof(StatusPedidoInsercaoEnum)))
            {
                var encontrado = porStatusBruto.FirstOrDefault(x => x.Status == statusOficial);
                porStatus.Add(new
                {
                    Status = statusOficial.ToString(),
                    Quantidade = encontrado?.Quantidade ?? 0,
                    Valor = encontrado?.Valor ?? 0m
                });
            }

            return new
            {
                TotalPIs = totais?.TotalPIs ?? 0,
                TotalPecas = totais?.TotalPecas ?? 0,
                ValorLiquidoTotal = totais?.ValorLiquidoTotal ?? 0m,
                PorStatus = porStatus
            };
        }

        /// <summary>
        /// Obtém o detalhe do PI por código com validação Anti-IDOR.
        /// </summary>
        [HttpGet("{codigo}")]
        public async Task<IActionResult> GetByCodigo(string codigo)
        {
            // Sem estes Includes `pi.Pedido` vem null (lazy loading desligado no
            // contexto do core) e `pi.Pedido.Campanha.Agencia?.Nome` abaixo estoura
            // NullReferenceException — o `?.` protege o último nível, não o
            // primeiro. Era 500 garantido em todo detalhe de PI.
            var pi = await _tenant.PedidosInsercao
                .Include(p => p.Pedido.Campanha.Agencia)
                .Include(p => p.Pedido.Campanha.Cliente)
                .Include(p => p.Itens)
                .FirstOrDefaultAsync(p => p.Codigo == codigo && p.StatusExibicao == StatusExibicaoEnum.Ativo);

            if (pi == null)
                return NotFound(new { message = "Pedido de inserção não encontrado." });

            return Ok(new
            {
                pi.Id,
                pi.Codigo,
                pi.DataCadastro,
                DataPedido = pi.Pedido.DataCadastro,
                Status = pi.Status.ToString(),
                Campanha = pi.Pedido.Campanha.Nome,
                // Mesmo fallback da listagem (VEI-RD-94): nunca null silencioso.
                Agencia = pi.Pedido.Campanha.Agencia?.Nome ?? AgenciaVendaDiretaProvisionamento.NomeFantasia,
                Anunciante = pi.Pedido.Campanha.Cliente?.Nome,
                pi.ValorLiquidoVeiculacao,
                ItensCount = pi.Itens != null ? pi.Itens.Count : 0
            });
        }

        /// <summary>
        /// Entrega o PDF da PI pelo próprio BFF, sem expor o FileServer.
        /// </summary>
        /// <remarks>
        /// A ordem importa e é o ponto do endpoint: o código é resolvido primeiro
        /// contra <see cref="ITenantQueries.PedidosInsercao"/>, que já vem
        /// recortado pela afiliada. Um código de outra exibidora sai daqui como
        /// 404 <b>antes</b> de qualquer chamada ao FileServer — que, se fosse
        /// alcançado, entregaria o arquivo sem perguntar de quem é.
        /// </remarks>
        [HttpGet("{codigo}/pdf")]
        public async Task<IActionResult> GetPdf(string codigo, CancellationToken ct)
        {
            var existeNesteTenant = await _tenant.PedidosInsercao
                .AnyAsync(p => p.Codigo == codigo && p.StatusExibicao == StatusExibicaoEnum.Ativo);

            if (!existeNesteTenant)
                return NotFound(new { message = "Pedido de inserção não encontrado." });

            WlPiPdf? pdf;
            try
            {
                pdf = await _pdf.ObterAsync(codigo, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // A mensagem ao cliente não diz qual serviço falhou nem onde ele
                // fica — o log interno é que carrega o diagnóstico. A exceção
                // pode trazer o host na Message, por isso ela não vira resposta.
                _logger.LogError(ex, "Falha ao obter o PDF da PI {Codigo} na origem.", codigo);

                return StatusCode(502, new
                {
                    message = "Não foi possível gerar o PDF deste pedido agora. Tente novamente."
                });
            }

            if (pdf == null)
                return NotFound(new { message = "PDF do pedido de inserção não encontrado." });

            Response.Headers["X-Content-Type-Options"] = "nosniff";
            return File(pdf.Conteudo, pdf.ContentType, pdf.NomeArquivo);
        }
    }
}
