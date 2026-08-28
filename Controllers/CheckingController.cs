using System;
using System.Data.Entity;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Enums;
using Veiculando.WhiteLabel.Api.Configurations;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;

namespace Veiculando.WhiteLabel.Api.Controllers
{
    [ApiController]
    [Route("api/wl/[controller]")]
    [Authorize(Policy = AuthorizationSetup.Checking)]
    public class CheckingController : ControllerBase
    {
        private readonly VeiculandoDataContext _db;
        private readonly ITenantQueries _tenant;
        private readonly IFileValidationService _fileValidation;

        public CheckingController(VeiculandoDataContext db, ITenantQueries tenant, IFileValidationService fileValidation)
        {
            _db = db;
            _tenant = tenant;
            _fileValidation = fileValidation;
        }

        [HttpGet("pis-autorizadas")]
        public async Task<IActionResult> GetPisAutorizadas()
        {
            var afiliadaId = _tenant.AfiliadaId;

            var pis = await _tenant.PedidosInsercao
                .Where(pi => pi.StatusExibicao == StatusExibicaoEnum.Ativo)
                .Select(pi => new
                {
                    pi.Id,
                    pi.Codigo,
                    pi.DataCadastro,
                    pi.ValorLiquidoVeiculacao
                })
                .ToListAsync();

            return Ok(pis);
        }

        [HttpGet("pi/{codigo}")]
        public async Task<IActionResult> GetPiByCodigo(string codigo)
        {
            var afiliadaId = _tenant.AfiliadaId;

            var pi = await _tenant.PedidosInsercao
                .FirstOrDefaultAsync(p => p.Codigo == codigo && p.StatusExibicao == StatusExibicaoEnum.Ativo);

            if (pi == null)
                return NotFound(new { message = "Pedido de Inserção não encontrado." });


            return Ok(new
            {
                pi.Id,
                pi.Codigo,
                pi.DataCadastro,
                pi.ValorLiquidoVeiculacao,
                ItensCount = pi.Itens != null ? pi.Itens.Count : 0
            });
        }

        /// <summary>
        /// Lista os itens de uma PI para a tela de checking (TP-R4).
        /// </summary>
        /// <remarks>
        /// Sem este endpoint a segunda tela do fluxo de checking era inalcançável:
        /// o <see cref="GetPiByCodigo"/> devolve apenas <c>ItensCount</c>, e o
        /// upload é endereçado por <c>idItemPI</c> — o frontend não tinha como
        /// descobrir esses ids. Espelha o que o <c>PedidosReservaController</c>
        /// já faz na projeção de <c>Itens</c>.
        ///
        /// Somente leitura, com a mesma validação de tenant dos demais.
        /// </remarks>
        [HttpGet("pi/{codigo}/itens")]
        public async Task<IActionResult> GetItensDaPi(string codigo)
        {
            var afiliadaId = _tenant.AfiliadaId;

            var pi = await _tenant.PedidosInsercao
                .FirstOrDefaultAsync(p => p.Codigo == codigo && p.StatusExibicao == StatusExibicaoEnum.Ativo);

            if (pi == null)
                return NotFound(new { message = "Pedido de Inserção não encontrado." });


            var itens = await _tenant.PedidoInsercaoItens
                .Where(i => i.IdPedidoInsercao == pi.Id)
                .Select(i => new
                {
                    i.IdPedidoItem,
                    i.IdPedidoInsercao,
                    Status = i.Status.ToString(),
                    PecaCodigo = i.PedidoItem.Peca.Codigo,
                    LocalCodigo = i.PedidoItem.Peca.Local.Codigo,
                    LocalDescricao = i.PedidoItem.Peca.Local.Descricao,
                    // Status do checking propriamente dito, quando ja houve envio.
                    StatusChecking = i.CheckingItem != null ? i.CheckingItem.Status.ToString() : null
                })
                .ToListAsync();

            return Ok(itens);
        }

        [HttpGet("item/{id}")]
        public async Task<IActionResult> GetItemById(int id)
        {
            var afiliadaId = _tenant.AfiliadaId;

            var item = await _tenant.PedidoInsercaoItens
                .FirstOrDefaultAsync(i => i.IdPedidoItem == id);

            if (item == null)
                return NotFound(new { message = "Item de PI não encontrado." });


            return Ok(new
            {
                item.IdPedidoItem,
                item.IdPedidoInsercao,
                Status = item.Status.ToString()
            });
        }

    }
}
