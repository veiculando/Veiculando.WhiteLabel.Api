using System;
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
        /// Lista os Pedidos de Inserção (PIs) da exibidora com colunas Anunciante e Agência (TP-3).
        /// </summary>
        /// <remarks>
        /// Não devolve mais <c>PdfUrl</c>. O campo carregava o host do
        /// <c>FILE_SERVER_URL</c> até o browser, e o FileServer não autentica
        /// ninguém nem filtra por afiliada — ver <see cref="IWlPiPdfSource"/>. O
        /// PDF agora sai por <c>GET {codigo}/pdf</c> neste mesmo controller, que
        /// é onde o recorte de tenant existe.
        /// </remarks>
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            // A interpolacao de string vira String.Format, que o EF6 nao traduz:
            // dentro do Select isso lancava NotSupportedException e a listagem de
            // PIs respondia 500 sempre. O mesmo vale para `Status.ToString()`.
            //
            // Materializa as colunas reais primeiro e monta Status depois, ja em
            // memoria. Mesma classe de defeito de Periodo.Nome em
            // LookupsController e ProgramacaoController.
            var brutos = await _tenant.PedidosInsercao
                .Where(pi => pi.StatusExibicao == StatusExibicaoEnum.Ativo)
                .Select(pi => new
                {
                    pi.Id,
                    pi.Codigo,
                    pi.DataCadastro,
                    pi.Status,
                    Agencia = pi.Pedido.Campanha.Agencia != null ? pi.Pedido.Campanha.Agencia.Nome : null,
                    Anunciante = pi.Pedido.Campanha.Cliente != null ? pi.Pedido.Campanha.Cliente.Nome : null,
                    pi.ValorLiquidoVeiculacao,
                })
                .ToListAsync();

            var pis = brutos
                .Select(pi => new
                {
                    pi.Id,
                    pi.Codigo,
                    pi.DataCadastro,
                    Status = pi.Status.ToString(),
                    pi.Agencia,
                    pi.Anunciante,
                    pi.ValorLiquidoVeiculacao,
                })
                .ToList();

            return Ok(pis);
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
                Status = pi.Status.ToString(),
                Agencia = pi.Pedido.Campanha.Agencia?.Nome,
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
