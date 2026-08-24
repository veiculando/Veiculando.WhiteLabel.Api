using System.Data.Entity;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Repositories;
using Veiculando.WhiteLabel.Api.Configurations;
using Veiculando.WhiteLabel.Api.Contracts;
using Veiculando.WhiteLabel.Api.Contracts.PedidosInsercao;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;

namespace Veiculando.WhiteLabel.Api.Controllers
{
    /// <summary>
    /// Pedidos de Inserção (TP-C §2 e §3): listagem paginada e PDF seguro
    /// via BFF. O FileServer nunca é exposto ao navegador — nem sua URL,
    /// nem seus erros crus.
    /// </summary>
    [ApiController]
    [Route("api/wl/pedidos-insercao")]
    [Authorize]
    public class PedidoInsercaoController : ControllerBase
    {
        private readonly IPedidoInsercaoRepository _pedidoInsercaoRepository;
        private readonly IFileServerClient _fileServerClient;
        private readonly ITenantContext _tenantContext;
        private readonly VeiculandoDataContext _db;

        public PedidoInsercaoController(
            IPedidoInsercaoRepository pedidoInsercaoRepository,
            IFileServerClient fileServerClient,
            ITenantContext tenantContext,
            VeiculandoDataContext db = null)
        {
            _pedidoInsercaoRepository = pedidoInsercaoRepository;
            _fileServerClient = fileServerClient;
            _tenantContext = tenantContext;
            _db = db;
        }

        /// <remarks>
        /// Mesmo padrão de PedidoReservaController.Listar — query direta
        /// contra o DbContext (EF6) porque o repositório do Core não pagina.
        /// NÃO validado contra SQL Server real nesta execução.
        /// </remarks>
        [HttpGet]
        [Authorize(Policy = AuthorizationSetup.PedidoInsercaoVisualizar)]
        public async Task<ActionResult<PagedResult<PedidoInsercaoListItem>>> Listar(
            int? page, int? pageSize, string sortBy, string sortDirection)
        {
            var whitelist = new[] { "codigo", "status" };
            var query = PageQuery.Normalize(page, pageSize, sortBy, sortDirection, whitelist, defaultSortBy: "codigo");

            var baseQuery = _db.PedidosInsercao
                .AsNoTracking()
                .Where(p => p.IdAfiliada == _tenantContext.AfiliadaId);

            var total = await baseQuery.CountAsync();

            var ordered = query.SortDirection == "desc"
                ? baseQuery.OrderByDescending(p => p.Id)
                : baseQuery.OrderBy(p => p.Id);

            var items = await ordered
                .Skip(query.Skip)
                .Take(query.PageSize)
                .Select(p => new PedidoInsercaoListItem
                {
                    Id = p.Id,
                    Codigo = p.Codigo,
                    Status = (int)p.Status,
                    QuantidadePecas = p.Itens.Count,
                    ValorLiquido = p.ValorLiquidoVeiculacao,
                })
                .ToListAsync();

            return Ok(new PagedResult<PedidoInsercaoListItem>(items, query.Page, query.PageSize, total));
        }

        /// <summary>
        /// GET /api/wl/pedidos-insercao/{codigo}/pdf — TP-C §2.
        /// O tenant é validado ANTES de qualquer chamada ao FileServer; PI
        /// inexistente e PI de outro tenant recebem a mesma resposta 404
        /// (BDD "PDF de outro tenant não chega ao FileServer").
        /// </summary>
        [HttpGet("{codigo}/pdf")]
        public async Task<IActionResult> Pdf(string codigo, CancellationToken cancellationToken)
        {
            var pi = _pedidoInsercaoRepository.RetornaPIPorCodigo(codigo);

            if (pi == null || pi.IdAfiliada != _tenantContext.AfiliadaId)
                return NotFound();

            var resultado = await _fileServerClient.GetPedidoInsercaoPdfAsync(codigo, cancellationToken);

            switch (resultado.Status)
            {
                case FileServerResultStatus.Success:
                    return File(resultado.Content, resultado.ContentType, $"PI-{codigo}.pdf");

                case FileServerResultStatus.NotFound:
                    // PI existe no tenant mas o FileServer não tem o arquivo —
                    // 404 controlado, nunca a URL/erro cru do upstream.
                    return NotFound(new { message = "PDF indisponível para este pedido." });

                default:
                    // Bad Gateway: o BFF é o proxy e o upstream falhou — nunca
                    // repassa exceção, stack trace ou a URL interna do FileServer.
                    return StatusCode(502, new { message = "Não foi possível obter o PDF no momento. Tente novamente." });
            }
        }
    }
}
