using System;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Enums;
using Veiculando.WhiteLabel.Api.Configurations;
using Veiculando.WhiteLabel.Api.Middleware;

namespace Veiculando.WhiteLabel.Api.Controllers
{
    [ApiController]
    [Route("api/wl/pedidos-reserva")]
    [Authorize]
    [ServiceFilter(typeof(InputSanitizationFilter))]
    public class PedidosReservaController : ControllerBase
    {
        private readonly VeiculandoDataContext _db;
        private readonly ITenantContext _tenantContext;

        public PedidosReservaController(VeiculandoDataContext db, ITenantContext tenantContext)
        {
            _db = db;
            _tenantContext = tenantContext;
        }

        /// <summary>
        /// Lista as solicitações de reserva da exibidora ativa com coluna Agência (TP-3).
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var afiliadaId = _tenantContext.AfiliadaId;

            var reservas = await _db.PedidosReserva
                .Where(pr => pr.IdAfiliada == afiliadaId)
                .Select(pr => new
                {
                    pr.Id,
                    pr.Codigo,
                    Status = pr.Status.ToString(),
                    pr.DataCadastro,
                    Agencia = pr.Pedido.Campanha.Agencia != null ? pr.Pedido.Campanha.Agencia.Nome : null,
                    Cliente = pr.Pedido.Campanha.Cliente != null ? pr.Pedido.Campanha.Cliente.Nome : null,
                    ItensCount = pr.Itens.Count
                })
                .ToListAsync();

            return Ok(reservas);
        }

        /// <summary>
        /// Obtém o detalhe do pedido de reserva por código com validação Anti-IDOR.
        /// </summary>
        [HttpGet("{codigo}")]
        public async Task<IActionResult> GetByCodigo(string codigo)
        {
            var afiliadaId = _tenantContext.AfiliadaId;

            var pr = await _db.PedidosReserva
                .FirstOrDefaultAsync(x => x.Codigo == codigo && x.IdAfiliada == afiliadaId);

            if (pr == null)
                return NotFound(new { message = "Pedido de reserva não encontrado." });

            pr.AssertTenantAccess(afiliadaId);

            return Ok(new
            {
                pr.Id,
                pr.Codigo,
                Status = pr.Status.ToString(),
                pr.DataCadastro,
                Agencia = pr.Pedido.Campanha.Agencia?.Nome,
                Cliente = pr.Pedido.Campanha.Cliente?.Nome,
                ValorTotalBruto = pr.ValorTotalBruto,
                Itens = pr.Itens.Select(i => new
                {
                    Id = i.IdPedidoItem,
                    PecaCodigo = i.PedidoItem?.Peca?.Codigo,
                    LocalCodigo = i.PedidoItem?.Peca?.Local?.Codigo,
                    Status = i.Status.ToString()
                })
            });
        }

        /// <summary>
        /// Responde a uma solicitação de reserva (aceitar ou rejeitar) com validação Anti-IDOR (TP-3).
        /// </summary>
        [HttpPost("resposta")]
        [Authorize(Policy = AuthorizationSetup.PedidoReservaGerenciar)]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public async Task<IActionResult> ResponderReserva([FromBody] PedidoReservaRespostaDto dto)
        {
            var afiliadaId = _tenantContext.AfiliadaId;

            var pedido = await _db.PedidosReserva
                .FirstOrDefaultAsync(pr => pr.Id == dto.PedidoReservaId && pr.IdAfiliada == afiliadaId);

            if (pedido == null)
                return NotFound(new { message = "Pedido de reserva não encontrado." });

            pedido.AssertTenantAccess(afiliadaId);

            if (dto.Aceitar)
            {
                pedido.AtualizaStatus();
            }

            await _db.SaveChangesAsync();
            return Ok(new { message = dto.Aceitar ? "Reserva confirmada com sucesso." : "Reserva rejeitada com sucesso." });
        }
    }

    public class PedidoReservaRespostaDto
    {
        public int PedidoReservaId { get; set; }
        public bool Aceitar { get; set; }
    }
}
