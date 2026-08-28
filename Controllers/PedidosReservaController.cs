using System;
using System.Collections.Generic;
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
    [Authorize(Policy = AuthorizationSetup.PedidoReservaGerenciar)]
    public class PedidosReservaController : WlCoreProxyControllerBase
    {
        private readonly VeiculandoDataContext _db;
        private readonly ITenantQueries _tenant;
        private readonly ICoreCadastroService _coreCadastro;

        public PedidosReservaController(
            VeiculandoDataContext db,
            ITenantQueries tenant,
            ICoreCadastroService coreCadastro)
        {
            _db = db;
            _tenant = tenant;
            _coreCadastro = coreCadastro;
        }

        /// <summary>
        /// Lista as solicitações de reserva da exibidora ativa com coluna Agência (TP-3).
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetAll([FromQuery] WlPaginaRequest pagina)
        {
            var (page, pageSize) = WlPaginacao.Normalizar(pagina);
            var sort = WlPaginacao.Ordenacao(
                pagina?.Sort, "dataCadastro", "codigo", "dataCadastro", "status");
            var desc = pagina?.Desc ?? true;

            var query = _tenant.PedidosReserva;
            var total = await query.CountAsync();

            // Desempate por Id: varios pedidos compartilham DataCadastro e Status,
            // e sem ordem total a navegacao entre paginas repete ou pula registros.
            var ordenada = (sort, desc) switch
            {
                ("codigo", false) => query.OrderBy(pr => pr.Codigo).ThenBy(pr => pr.Id),
                ("codigo", true) => query.OrderByDescending(pr => pr.Codigo).ThenBy(pr => pr.Id),
                ("status", false) => query.OrderBy(pr => pr.Status).ThenBy(pr => pr.Id),
                ("status", true) => query.OrderByDescending(pr => pr.Status).ThenBy(pr => pr.Id),
                (_, false) => query.OrderBy(pr => pr.DataCadastro).ThenBy(pr => pr.Id),
                (_, true) => query.OrderByDescending(pr => pr.DataCadastro).ThenBy(pr => pr.Id),
            };

            var brutos = await ordenada
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(pr => new
                {
                    pr.Id,
                    pr.Codigo,
                    pr.Status,
                    pr.DataCadastro,
                    Agencia = pr.Pedido.Campanha.Agencia != null ? pr.Pedido.Campanha.Agencia.Nome : null,
                    Cliente = pr.Pedido.Campanha.Cliente != null ? pr.Pedido.Campanha.Cliente.Nome : null,
                    ItensCount = pr.Itens.Count
                })
                .ToListAsync();

            var reservas = brutos
                .Select(pr => new
                {
                    pr.Id,
                    pr.Codigo,
                    Status = pr.Status.ToString(),
                    pr.DataCadastro,
                    pr.Agencia,
                    pr.Cliente,
                    pr.ItensCount
                })
                .ToList();

            return Ok(WlPaginacao.Montar(reservas, page, pageSize, total));
        }

        /// <summary>
        /// Obtém o detalhe do pedido de reserva por código com validação Anti-IDOR.
        /// </summary>
        [HttpGet("{codigo}")]
        public async Task<IActionResult> GetByCodigo(string codigo)
        {
            var afiliadaId = _tenant.AfiliadaId;

            // Mesma armadilha do PedidosInsercaoController: sem Include, `pr.Pedido`
            // vem null e a projeção estoura NRE. `pr.Itens` não estoura (o ctor
            // protegido inicializa a lista) mas viria vazia, e o detalhe mostraria
            // um pedido sem itens.
            var pr = await _tenant.PedidosReserva
                .Include(x => x.Pedido.Campanha.Agencia)
                .Include(x => x.Pedido.Campanha.Cliente)
                .Include(x => x.Itens.Select(i => i.PedidoItem.Peca.Local))
                .FirstOrDefaultAsync(x => x.Codigo == codigo);

            if (pr == null)
                return NotFound(new { message = "Pedido de reserva não encontrado." });


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
        /// <para><b>Resposta mista por item.</b> O contrato anterior era
        /// <c>{ pedidoReservaId, aceitar }</c> e aplicava a mesma decisão a todos
        /// os itens. O command do core sempre suportou decisão por item — o que
        /// faltava era expor isso. Agora o cliente envia uma decisão por item, e
        /// a rejeição pode carregar peças sugeridas como alternativa (o handler
        /// grava a primeira em <c>IdPecaRecomendada</c>; é o mecanismo de
        /// "motivo" que o domínio tem).</para>
        ///
        /// <para><b>O que é validado aqui e não no core.</b> O handler do core
        /// itera sobre os itens que recebe e não exige que todos os pendentes
        /// venham — omitir um item o deixaria pendente para sempre, com o pedido
        /// já marcado como respondido. Ele também não valida a afiliada da peça
        /// sugerida (a checagem de tenant lá está comentada, com um TODO). As
        /// duas coisas são barradas aqui, antes da chamada remota.</para>
        /// </remarks>
        [HttpPost("resposta")]
        [Authorize(Policy = AuthorizationSetup.PedidoReservaGerenciar)]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public async Task<IActionResult> ResponderReserva([FromBody] PedidoReservaRespostaDto dto)
        {
            if (dto == null)
                return BadRequest(new { message = "Dados da resposta são obrigatórios." });

            var afiliadaId = _tenant.AfiliadaId;

            // Itens e PedidoItem são necessários de verdade aqui: o command do core
            // exige IdPeca e IdPeriodo de cada item para localizar a linha da grade
            // de disponibilidade.
            var pedido = await _tenant.PedidosReserva
                .Include(pr => pr.Itens.Select(i => i.PedidoItem))
                .FirstOrDefaultAsync(pr => pr.Id == dto.PedidoReservaId);

            if (pedido == null)
                return NotFound(new { message = "Pedido de reserva não encontrado." });


            // Mesma guarda do handler do core, aplicada antes da chamada remota
            // para devolver uma mensagem clara em vez de uma notificação genérica.
            // Sem ela, responder duas vezes o mesmo pedido era aceito.
            if (pedido.Status != StatusPedidoReservaEnum.Solicitado)
                return Conflict(new { message = "Este pedido não está mais disponível para resposta." });

            if (pedido.Itens == null || !pedido.Itens.Any())
                return Conflict(new { message = "Este pedido de reserva não possui itens para responder." });

            if (dto.Itens == null || dto.Itens.Count == 0)
                return BadRequest(new { message = "Informe a decisão de cada item do pedido." });

            var pendentes = pedido.Itens
                .Where(i => i.Status == StatusPedidoReservaItemEnum.Solicitado)
                .ToList();

            if (pendentes.Count == 0)
                return Conflict(new { message = "Este pedido não possui itens pendentes de resposta." });

            var recebidos = dto.Itens.Select(i => i.IdItemPedidoReserva).ToList();

            var duplicados = recebidos.GroupBy(id => id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (duplicados.Any())
            {
                return BadRequest(new
                {
                    message = "Cada item deve ser respondido uma única vez.",
                    itens = duplicados
                });
            }

            var esperados = pendentes.Select(i => i.IdPedidoItem).ToHashSet();

            var desconhecidos = recebidos.Where(id => !esperados.Contains(id)).ToList();
            if (desconhecidos.Any())
            {
                return BadRequest(new
                {
                    message = "Há itens que não pertencem a este pedido.",
                    itens = desconhecidos
                });
            }

            // Omitir um item o deixaria pendente para sempre enquanto o pedido ja
            // constaria respondido — por isso a resposta e completa ou nao e.
            var faltantes = esperados.Where(id => !recebidos.Contains(id)).ToList();
            if (faltantes.Any())
            {
                return BadRequest(new
                {
                    message = "Todos os itens pendentes precisam de uma decisão.",
                    itens = faltantes
                });
            }

            // Peca sugerida so pode ser do proprio tenant. `_tenant.Pecas` ja
            // recorta pela afiliada do Local, entao o que nao voltar da consulta
            // ou e de outra exibidora ou nao existe — nos dois casos, recusa.
            var sugeridas = dto.Itens
                .Where(i => i.IdsPecaSugerida != null)
                .SelectMany(i => i.IdsPecaSugerida)
                .Where(id => id > 0)
                .Distinct()
                .ToList();

            if (sugeridas.Any())
            {
                var proprias = await _tenant.Pecas
                    .Where(p => sugeridas.Contains(p.Id))
                    .Select(p => p.Id)
                    .ToListAsync();

                var alheias = sugeridas.Where(id => !proprias.Contains(id)).ToList();
                if (alheias.Any())
                {
                    return BadRequest(new
                    {
                        message = "Há peças sugeridas que não pertencem a esta exibidora.",
                        pecas = alheias
                    });
                }
            }

            var decisoes = dto.Itens.ToDictionary(i => i.IdItemPedidoReserva);

            var command = new PedidoReservaRespostaCommand
            {
                CodigoPedidoReserva = pedido.Codigo,
                // IdPeca e IdPeriodo saem do pedido carregado, nunca do corpo da
                // requisicao: o cliente decide o QUE responder, o servidor decide
                // SOBRE O QUE. Aceitar esses ids do cliente permitiria mover a
                // resposta para outra peca.
                Itens = pendentes.Select(i => new PedidoReservaRespostaCommand.PedidoReservaItemResposta
                {
                    IdItemPedidoReserva = i.IdPedidoItem,
                    IdPeca = i.PedidoItem.IdPeca,
                    IdPeriodo = i.PedidoItem.IdPeriodo,
                    Disponibilidade = decisoes[i.IdPedidoItem].Aceitar
                        ? StatusPedidoReservaItemEnum.Reservado
                        : StatusPedidoReservaItemEnum.Indisponivel,
                    // Array vazio, não null: o handler do core acessa
                    // `IdsPecaSugerida.Length` sem checar nulidade. Sugestão só
                    // faz sentido na rejeição — numa aceitação ela é ignorada.
                    IdsPecaSugerida = !decisoes[i.IdPedidoItem].Aceitar
                        ? (decisoes[i.IdPedidoItem].IdsPecaSugerida ?? Array.Empty<int>())
                            .Where(id => id > 0).ToArray()
                        : Array.Empty<int>()
                }).ToList()
            };

            var resposta = await _coreCadastro.ResponderReservaAsync(command);

            // Só o caminho de erro repassa o corpo do core — ali as notificações do
            // domínio são o que a tela precisa mostrar. No sucesso o core devolve um
            // `PedidoReservaResult`, que não tem `message` e não serve para nada
            // nesta UI: a tela lê `resposta.message` para o banner de confirmação e
            // ficaria em branco se recebesse o objeto do core.
            if (!resposta.Sucesso)
                return RepassarResposta(resposta);

            var aceitos = dto.Itens.Count(i => i.Aceitar);
            var rejeitados = dto.Itens.Count - aceitos;

            return Ok(new
            {
                message = rejeitados == 0
                    ? "Reserva confirmada com sucesso."
                    : aceitos == 0
                        ? "Reserva rejeitada com sucesso."
                        : $"Resposta registrada: {aceitos} item(ns) aceito(s) e {rejeitados} recusado(s).",
                aceitos,
                rejeitados
            });
        }
    }

    /// <summary>
    /// Resposta a um pedido de reserva, com uma decisão por item.
    /// </summary>
    /// <remarks>
    /// O campo <c>Aceitar</c> de nível superior saiu de propósito. Ele aplicava a
    /// mesma decisão a todos os itens, e mantê-lo ao lado de <c>Itens</c> criaria
    /// duas fontes de verdade para a mesma pergunta — com a dúvida de qual vence
    /// quando as duas vêm preenchidas e discordam.
    /// </remarks>
    public class PedidoReservaRespostaDto
    {
        public int PedidoReservaId { get; set; }

        /// <summary>Uma entrada por item pendente do pedido — nem a mais, nem a menos.</summary>
        public List<PedidoReservaItemRespostaDto> Itens { get; set; }
    }

    public class PedidoReservaItemRespostaDto
    {
        /// <summary>Id do item do pedido (<c>IdPedidoItem</c>).</summary>
        public int IdItemPedidoReserva { get; set; }

        public bool Aceitar { get; set; }

        /// <summary>
        /// Peças oferecidas como alternativa na rejeição. Precisam ser da própria
        /// exibidora; o core grava a primeira em <c>IdPecaRecomendada</c>.
        /// </summary>
        public int[] IdsPecaSugerida { get; set; }
    }
}
