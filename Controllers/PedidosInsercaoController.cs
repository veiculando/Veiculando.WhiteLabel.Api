using System;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Enums;
using Veiculando.WhiteLabel.Api.Middleware;

namespace Veiculando.WhiteLabel.Api.Controllers
{
    [ApiController]
    [Route("api/wl/pedidos-insercao")]
    [Authorize]
    public class PedidosInsercaoController : ControllerBase
    {
        private readonly VeiculandoDataContext _db;
        private readonly ITenantContext _tenantContext;

        public PedidosInsercaoController(VeiculandoDataContext db, ITenantContext tenantContext)
        {
            _db = db;
            _tenantContext = tenantContext;
        }

        /// <summary>
        /// Lista os Pedidos de Inserção (PIs) da exibidora com colunas Anunciante, Agência e link de detalhe PDF (TP-3).
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var afiliadaId = _tenantContext.AfiliadaId;
            var fileServerUrl = Environment.GetEnvironmentVariable("FILE_SERVER_URL") ?? "https://fileserver.veiculando.com.br";

            var pis = await _db.PedidosInsercao
                .Where(pi => pi.IdAfiliada == afiliadaId && pi.StatusExibicao == StatusExibicaoEnum.Ativo)
                .Select(pi => new
                {
                    pi.Id,
                    pi.Codigo,
                    pi.DataCadastro,
                    Status = pi.Status.ToString(),
                    Agencia = pi.Pedido.Campanha.Agencia != null ? pi.Pedido.Campanha.Agencia.Nome : null,
                    Anunciante = pi.Pedido.Campanha.Cliente != null ? pi.Pedido.Campanha.Cliente.Nome : null,
                    pi.ValorLiquidoVeiculacao,
                    PdfUrl = $"{fileServerUrl}/pedidoinsercao/detalhes/{pi.Id}"
                })
                .ToListAsync();

            return Ok(pis);
        }

        /// <summary>
        /// Obtém o detalhe do PI por código com validação Anti-IDOR.
        /// </summary>
        [HttpGet("{codigo}")]
        public async Task<IActionResult> GetByCodigo(string codigo)
        {
            var afiliadaId = _tenantContext.AfiliadaId;
            var fileServerUrl = Environment.GetEnvironmentVariable("FILE_SERVER_URL") ?? "https://fileserver.veiculando.com.br";

            // Sem estes Includes `pi.Pedido` vem null (lazy loading desligado no
            // contexto do core) e `pi.Pedido.Campanha.Agencia?.Nome` abaixo estoura
            // NullReferenceException — o `?.` protege o último nível, não o
            // primeiro. Era 500 garantido em todo detalhe de PI.
            var pi = await _db.PedidosInsercao
                .Include(p => p.Pedido.Campanha.Agencia)
                .Include(p => p.Pedido.Campanha.Cliente)
                .Include(p => p.Itens)
                .FirstOrDefaultAsync(p => p.Codigo == codigo && p.IdAfiliada == afiliadaId && p.StatusExibicao == StatusExibicaoEnum.Ativo);

            if (pi == null)
                return NotFound(new { message = "Pedido de inserção não encontrado." });

            pi.AssertTenantAccess(afiliadaId);

            return Ok(new
            {
                pi.Id,
                pi.Codigo,
                pi.DataCadastro,
                Status = pi.Status.ToString(),
                Agencia = pi.Pedido.Campanha.Agencia?.Nome,
                Anunciante = pi.Pedido.Campanha.Cliente?.Nome,
                pi.ValorLiquidoVeiculacao,
                PdfUrl = $"{fileServerUrl}/pedidoinsercao/detalhes/{pi.Id}",
                ItensCount = pi.Itens != null ? pi.Itens.Count : 0
            });
        }
    }
}
