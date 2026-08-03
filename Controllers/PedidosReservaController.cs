using System;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Commands.Inputs.Pedidos;
using Veiculando.Domain.Enums;
using Veiculando.WhiteLabel.Api.Configurations;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;

namespace Veiculando.WhiteLabel.Api.Controllers
{
    [ApiController]
    [Route("api/wl/pedidos-reserva")]
    [Authorize]
    public class PedidosReservaController : WlCoreProxyControllerBase
    {
        private readonly VeiculandoDataContext _db;
        private readonly ITenantContext _tenantContext;
        private readonly ICoreCadastroService _coreCadastro;

        public PedidosReservaController(
            VeiculandoDataContext db,
            ITenantContext tenantContext,
            ICoreCadastroService coreCadastro)
        {
            _db = db;
            _tenantContext = tenantContext;
            _coreCadastro = coreCadastro;
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

            // Mesma armadilha do PedidosInsercaoController: sem Include, `pr.Pedido`
            // vem null e a projeção estoura NRE. `pr.Itens` não estoura (o ctor
            // protegido inicializa a lista) mas viria vazia, e o detalhe mostraria
            // um pedido sem itens.
            var pr = await _db.PedidosReserva
                .Include(x => x.Pedido.Campanha.Agencia)
                .Include(x => x.Pedido.Campanha.Cliente)
                .Include(x => x.Itens.Select(i => i.PedidoItem.Peca.Local))
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
        /// Responde a uma solicitação de reserva (aceitar ou rejeitar), delegando
        /// ao core.
        /// </summary>
        /// <remarks>
        /// A implementação anterior estava quebrada nos dois ramos:
        ///
        /// <para><b>Rejeitar não fazia nada.</b> O corpo era
        /// <c>if (dto.Aceitar) { pedido.AtualizaStatus(); }</c> seguido de um
        /// <c>SaveChanges</c> sem alteração alguma — e respondia "Reserva rejeitada
        /// com sucesso". A reserva ficava <c>Solicitado</c> para sempre e seguia
        /// contando no KPI de pendentes do dashboard.</para>
        ///
        /// <para><b>Aceitar confirmava sem olhar os itens.</b>
        /// <c>AtualizaStatus()</c> decide a partir de <c>Itens</c>, que nunca era
        /// carregada (lazy loading desligado). Como o ctor protegido inicializa a
        /// lista vazia, <c>Itens.All(...)</c> retornava <c>true</c> por vacuidade e
        /// o status virava <c>Confirmado</c> mesmo que os itens reais estivessem
        /// indisponíveis. Pior: a grade <c>PecaPeriodoStatus</c> não era tocada, de
        /// modo que a peça continuava livre para ser reservada por outro pedido.</para>
        ///
        /// <para>Agora a operação é delegada ao <c>PedidoReservaRespostaHandler</c>
        /// do core, pelo mesmo caminho autenticado que Locais e Peças já usam. É
        /// ele quem confirma ou marca itens como indisponíveis, atualiza a grade de
        /// disponibilidade, grava o usuário que respondeu e propaga o status para o
        /// <c>Pedido</c> pai — regra que não deve existir em duplicata aqui.</para>
        ///
        /// <para>A resposta continua sendo tudo-ou-nada por item, que é o contrato
        /// do DTO atual (<c>{ pedidoReservaId, aceitar }</c>). Resposta item a item
        /// existe no command do core e pode ser exposta depois sem mudar este
        /// caminho.</para>
        /// </remarks>
        [HttpPost("resposta")]
        [Authorize(Policy = AuthorizationSetup.PedidoReservaGerenciar)]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public async Task<IActionResult> ResponderReserva([FromBody] PedidoReservaRespostaDto dto)
        {
            if (dto == null)
                return BadRequest(new { message = "Dados da resposta são obrigatórios." });

            var afiliadaId = _tenantContext.AfiliadaId;

            // Itens e PedidoItem são necessários de verdade aqui: o command do core
            // exige IdPeca e IdPeriodo de cada item para localizar a linha da grade
            // de disponibilidade.
            var pedido = await _db.PedidosReserva
                .Include(pr => pr.Itens.Select(i => i.PedidoItem))
                .FirstOrDefaultAsync(pr => pr.Id == dto.PedidoReservaId && pr.IdAfiliada == afiliadaId);

            if (pedido == null)
                return NotFound(new { message = "Pedido de reserva não encontrado." });

            pedido.AssertTenantAccess(afiliadaId);

            // Mesma guarda do handler do core, aplicada antes da chamada remota
            // para devolver uma mensagem clara em vez de uma notificação genérica.
            // Sem ela, responder duas vezes o mesmo pedido era aceito.
            if (pedido.Status != StatusPedidoReservaEnum.Solicitado)
                return Conflict(new { message = "Este pedido não está mais disponível para resposta." });

            if (pedido.Itens == null || !pedido.Itens.Any())
                return Conflict(new { message = "Este pedido de reserva não possui itens para responder." });

            var disponibilidade = dto.Aceitar
                ? StatusPedidoReservaItemEnum.Reservado
                : StatusPedidoReservaItemEnum.Indisponivel;

            var command = new PedidoReservaRespostaCommand
            {
                CodigoPedidoReserva = pedido.Codigo,
                Itens = pedido.Itens.Select(i => new PedidoReservaRespostaCommand.PedidoReservaItemResposta
                {
                    IdItemPedidoReserva = i.IdPedidoItem,
                    IdPeca = i.PedidoItem.IdPeca,
                    IdPeriodo = i.PedidoItem.IdPeriodo,
                    Disponibilidade = disponibilidade,
                    // Array vazio, não null: o handler do core acessa
                    // `IdsPecaSugerida.Length` sem checar nulidade.
                    IdsPecaSugerida = Array.Empty<int>()
                }).ToList()
            };

            var resposta = await _coreCadastro.ResponderReservaAsync(command);
            return RepassarResposta(resposta);
        }
    }

    public class PedidoReservaRespostaDto
    {
        public int PedidoReservaId { get; set; }
        public bool Aceitar { get; set; }
    }
}
