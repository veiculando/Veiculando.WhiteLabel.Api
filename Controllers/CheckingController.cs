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
    [Authorize]
    public class CheckingController : ControllerBase
    {
        private readonly VeiculandoDataContext _db;
        private readonly ITenantContext _tenantContext;
        private readonly IFileValidationService _fileValidation;

        public CheckingController(VeiculandoDataContext db, ITenantContext tenantContext, IFileValidationService fileValidation)
        {
            _db = db;
            _tenantContext = tenantContext;
            _fileValidation = fileValidation;
        }

        [HttpGet("pis-autorizadas")]
        public async Task<IActionResult> GetPisAutorizadas()
        {
            var afiliadaId = _tenantContext.AfiliadaId;

            var pis = await _db.PedidosInsercao
                .Where(pi => pi.IdAfiliada == afiliadaId && pi.StatusExibicao == StatusExibicaoEnum.Ativo)
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
            var afiliadaId = _tenantContext.AfiliadaId;

            var pi = await _db.PedidosInsercao
                .FirstOrDefaultAsync(p => p.Codigo == codigo && p.IdAfiliada == afiliadaId && p.StatusExibicao == StatusExibicaoEnum.Ativo);

            if (pi == null)
                return NotFound(new { message = "Pedido de Inserção não encontrado." });

            pi.AssertTenantAccess(afiliadaId);

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
            var afiliadaId = _tenantContext.AfiliadaId;

            var pi = await _db.PedidosInsercao
                .FirstOrDefaultAsync(p => p.Codigo == codigo && p.IdAfiliada == afiliadaId && p.StatusExibicao == StatusExibicaoEnum.Ativo);

            if (pi == null)
                return NotFound(new { message = "Pedido de Inserção não encontrado." });

            pi.AssertTenantAccess(afiliadaId);

            var itens = await _db.PedidoInsercaoItens
                .Where(i => i.IdPedidoInsercao == pi.Id && i.PedidoInsercao.IdAfiliada == afiliadaId)
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
            var afiliadaId = _tenantContext.AfiliadaId;

            var item = await _db.PedidoInsercaoItens
                .FirstOrDefaultAsync(i => i.IdPedidoItem == id && i.PedidoInsercao.IdAfiliada == afiliadaId);

            if (item == null)
                return NotFound(new { message = "Item de PI não encontrado." });

            item.PedidoInsercao.AssertTenantAccess(afiliadaId);

            return Ok(new
            {
                item.IdPedidoItem,
                item.IdPedidoInsercao,
                Status = item.Status.ToString()
            });
        }

        /// <summary>
        /// Valida a foto de comprovação de um item de PI. **Não persiste o
        /// arquivo** — o armazenamento é escopo do TP-2.
        /// </summary>
        /// <remarks>
        /// Respondia 200 com "recebida e validada com sucesso" e descartava o
        /// arquivo no fim do request. Num fluxo de checking isso é pior do que
        /// falhar: a comprovação fotográfica é a evidência de que a inserção
        /// aconteceu, e o operador encerrava o trabalho acreditando tê-la
        /// enviado. Enquanto a gravação não existe, o status honesto é 501.
        /// </remarks>
        [HttpPost("enviar-foto/{idItemPI}")]
        [Authorize(Policy = AuthorizationSetup.Checking)]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public async Task<IActionResult> EnviarFotoChecking(int idItemPI, IFormFile foto)
        {
            var afiliadaId = _tenantContext.AfiliadaId;

            var item = await _db.PedidoInsercaoItens
                .FirstOrDefaultAsync(i => i.IdPedidoItem == idItemPI && i.PedidoInsercao.IdAfiliada == afiliadaId);

            if (item == null)
                return NotFound(new { message = "Item de PI não encontrado." });

            item.PedidoInsercao.AssertTenantAccess(afiliadaId);

            const long maxBytes = 15 * 1024 * 1024; // Max 15MB conforme TP-2
            if (!_fileValidation.IsValidFile(foto, maxBytes, out var errorMessage))
            {
                return BadRequest(new { message = errorMessage });
            }

            return StatusCode(501, new
            {
                message = "O envio da comprovação fotográfica ainda não está disponível: " +
                          "o arquivo foi validado, mas o armazenamento será entregue no " +
                          "TP-2. Nenhuma foto foi salva."
            });
        }
    }
}
