using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Data.Entity;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Commands.Handlers.Pedidos;
using Veiculando.Domain.Repositories;
using Veiculando.WhiteLabel.Api.Configurations;
using Veiculando.WhiteLabel.Api.Contracts;
using Veiculando.WhiteLabel.Api.Contracts.PedidosReserva;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;
using Veiculando.WhiteLabel.Api.Validation;

namespace Veiculando.WhiteLabel.Api.Controllers
{
    /// <summary>
    /// Solicitações de Reserva (TP-C §1 e §3).
    ///
    /// A resposta por item NUNCA chama o handler do Core
    /// (<see cref="PedidoReservaRespostaHandler"/>) sem antes: (1) confirmar
    /// que o pedido pertence ao tenant do Host, (2) passar por
    /// <see cref="IPedidoReservaRespostaValidator"/>. O handler do Core tem
    /// um TODO explícito de bloqueio por afiliada — esta é a única guarda
    /// real hoje.
    /// </summary>
    [ApiController]
    [Route("api/wl/pedidos-reserva")]
    [Authorize]
    public class PedidoReservaController : ControllerBase
    {
        private readonly PedidoReservaRespostaHandler _handlerResposta;
        private readonly IPedidoReservaRepository _pedidoReservaRepository;
        private readonly IPedidoReservaRespostaValidator _validator;
        private readonly ITenantContext _tenantContext;
        private readonly IServiceAccountResolver _serviceAccountResolver;
        private readonly VeiculandoDataContext _db;

        public PedidoReservaController(
            PedidoReservaRespostaHandler handlerResposta,
            IPedidoReservaRepository pedidoReservaRepository,
            IPedidoReservaRespostaValidator validator,
            ITenantContext tenantContext,
            IServiceAccountResolver serviceAccountResolver,
            VeiculandoDataContext db = null)
        {
            _handlerResposta = handlerResposta;
            _pedidoReservaRepository = pedidoReservaRepository;
            _validator = validator;
            _tenantContext = tenantContext;
            _serviceAccountResolver = serviceAccountResolver;
            _db = db;
        }

        /// <remarks>
        /// Query direta contra <see cref="VeiculandoDataContext"/> (não passa
        /// pelo repositório do Core, que não pagina) — Skip/Take/Count
        /// executados no SQL, nunca materializando a lista completa.
        /// NOTA: ainda não validado contra SQL Server real nesta execução
        /// (sem instância de banco disponível no ambiente de teste); ver
        /// gap reportado na entrega.
        /// </remarks>
        [HttpGet]
        public async Task<ActionResult<PagedResult<PedidoReservaListItem>>> Listar(
            int? page, int? pageSize, string sortBy, string sortDirection)
        {
            var whitelist = new[] { "dataCadastro", "codigo", "status" };
            var query = PageQuery.Normalize(page, pageSize, sortBy, sortDirection, whitelist, defaultSortBy: "dataCadastro");

            var baseQuery = _db.PedidosReserva
                .AsNoTracking()
                .Where(p => p.IdAfiliada == _tenantContext.AfiliadaId);

            var total = await baseQuery.CountAsync();

            var ordered = query.SortDirection == "desc"
                ? baseQuery.OrderByDescending(p => p.Id)
                : baseQuery.OrderBy(p => p.Id);

            var items = await ordered
                .Skip(query.Skip)
                .Take(query.PageSize)
                .Select(p => new PedidoReservaListItem
                {
                    Id = p.Id,
                    Codigo = p.Codigo,
                    Status = (int)p.Status,
                    QuantidadePecas = p.Itens.Count,
                    ValorLiquido = p.ValorLiquidoVeiculacao,
                    DataCadastro = DateTime.UtcNow, // EntityDefBase.DataCadastro não é público a partir daqui; placeholder documentado
                })
                .ToListAsync();

            return Ok(new PagedResult<PedidoReservaListItem>(items, query.Page, query.PageSize, total));
        }

        [HttpPost("{codigo}/resposta")]
        [Authorize(Policy = AuthorizationSetup.PedidoReservaGerenciar)]
        public IActionResult Responder(string codigo, [FromBody] PedidoReservaRespostaRequest request)
        {
            var pedido = _pedidoReservaRepository.RetornaPorCodigo(codigo);

            // Mesma resposta para "não existe" e "é de outro tenant" — nunca
            // revela que o código pertence a outra afiliada (BDD "IDOR é bloqueado").
            if (pedido == null || pedido.IdAfiliada != _tenantContext.AfiliadaId)
                return NotFound();

            var outcome = _validator.Validate(pedido, request, _tenantContext.AfiliadaId);

            if (!outcome.IsValid)
            {
                return outcome.ErrorCode switch
                {
                    PedidoReservaRespostaErrorCode.NotFound => NotFound(),
                    PedidoReservaRespostaErrorCode.Conflict => Conflict(new { message = outcome.ErrorMessage }),
                    _ => BadRequest(new { message = outcome.ErrorMessage }),
                };
            }

            var usuario = _serviceAccountResolver.Resolve();
            outcome.Command.CodigoPedidoReserva = codigo;
            outcome.Command.IdUsuarioAfiliada = usuario.Id;

            var result = _handlerResposta.Handle(outcome.Command);

            if (result == null || !_handlerResposta.IsValid())
            {
                var mensagens = _handlerResposta.Notifications.Select(n => n.Message);
                return BadRequest(new { messages = mensagens });
            }

            return Ok(result);
        }
    }
}
