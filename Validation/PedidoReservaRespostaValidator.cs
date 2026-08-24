using System.Collections.Generic;
using System.Linq;
using Veiculando.Domain.Commands.Inputs.Pedidos;
using Veiculando.Domain.Entities.Pedidos;
using Veiculando.Domain.Enums;
using Veiculando.Domain.Repositories;
using Veiculando.WhiteLabel.Api.Contracts.PedidosReserva;

namespace Veiculando.WhiteLabel.Api.Validation
{
    public enum PedidoReservaRespostaErrorCode
    {
        None,
        NotFound,
        Conflict,
        BadRequest,
    }

    public class PedidoReservaRespostaValidationOutcome
    {
        public PedidoReservaRespostaErrorCode ErrorCode { get; }
        public string ErrorMessage { get; }
        public PedidoReservaRespostaCommand Command { get; }
        public bool IsValid => ErrorCode == PedidoReservaRespostaErrorCode.None;

        private PedidoReservaRespostaValidationOutcome(PedidoReservaRespostaErrorCode errorCode, string errorMessage, PedidoReservaRespostaCommand command)
        {
            ErrorCode = errorCode;
            ErrorMessage = errorMessage;
            Command = command;
        }

        public static PedidoReservaRespostaValidationOutcome Ok(PedidoReservaRespostaCommand command) =>
            new PedidoReservaRespostaValidationOutcome(PedidoReservaRespostaErrorCode.None, null, command);

        public static PedidoReservaRespostaValidationOutcome Fail(PedidoReservaRespostaErrorCode code, string message) =>
            new PedidoReservaRespostaValidationOutcome(code, message, null);
    }

    public interface IPedidoReservaRespostaValidator
    {
        PedidoReservaRespostaValidationOutcome Validate(PedidoReserva pedidoReserva, PedidoReservaRespostaRequest request, int tenantAfiliadaId);
    }

    /// <summary>
    /// Aplica as validações do TP-C §1 ANTES de qualquer chamada ao Core.
    /// Recebe o <see cref="PedidoReserva"/> já carregado do repositório — não
    /// consulta banco para o pedido em si, só para peças sugeridas. Nenhuma
    /// falha aqui chega a montar um <see cref="PedidoReservaRespostaCommand"/>
    /// utilizável; o controller nunca despacha para o Core em caso de erro.
    /// </summary>
    public class PedidoReservaRespostaValidator : IPedidoReservaRespostaValidator
    {
        private readonly IPecaRepository _pecaRepository;

        public PedidoReservaRespostaValidator(IPecaRepository pecaRepository)
        {
            _pecaRepository = pecaRepository;
        }

        public PedidoReservaRespostaValidationOutcome Validate(PedidoReserva pedidoReserva, PedidoReservaRespostaRequest request, int tenantAfiliadaId)
        {
            // Regra 7 do §3 de segurança comum a todo o TP-A/TP-C: nunca revelar
            // se o pedido existe — o controller resolve "não encontrado" e
            // "de outro tenant" para a mesma resposta 404 antes mesmo daqui,
            // mas revalidamos porque é barato e defende contra reordenação futura.
            if (pedidoReserva.IdAfiliada != tenantAfiliadaId)
                return PedidoReservaRespostaValidationOutcome.Fail(PedidoReservaRespostaErrorCode.NotFound, "Pedido não encontrado.");

            if (pedidoReserva.Status != StatusPedidoReservaEnum.Solicitado)
                return PedidoReservaRespostaValidationOutcome.Fail(PedidoReservaRespostaErrorCode.Conflict, "Este pedido já foi respondido.");

            if (request?.Itens == null || request.Itens.Count == 0)
                return PedidoReservaRespostaValidationOutcome.Fail(PedidoReservaRespostaErrorCode.BadRequest, "Informe a resposta de ao menos um item.");

            var pendentes = pedidoReserva.Itens
                .Where(i => i.Status == StatusPedidoReservaItemEnum.Solicitado)
                .ToDictionary(i => i.IdPedidoItem);

            var idsRecebidos = request.Itens.Select(i => i.IdItemPedidoReserva).ToList();

            if (idsRecebidos.Count != idsRecebidos.Distinct().Count())
                return PedidoReservaRespostaValidationOutcome.Fail(PedidoReservaRespostaErrorCode.BadRequest, "Item de pedido duplicado no payload.");

            if (idsRecebidos.Any(id => !pendentes.ContainsKey(id)))
                return PedidoReservaRespostaValidationOutcome.Fail(PedidoReservaRespostaErrorCode.BadRequest, "Item desconhecido ou de outro pedido.");

            if (pendentes.Keys.Any(id => !idsRecebidos.Contains(id)))
                return PedidoReservaRespostaValidationOutcome.Fail(PedidoReservaRespostaErrorCode.BadRequest, "Todos os itens pendentes devem ser respondidos.");

            var itensCommand = new List<PedidoReservaRespostaCommand.PedidoReservaItemResposta>();

            foreach (var itemRequest in request.Itens)
            {
                if (itemRequest.Disponibilidade != StatusPedidoReservaItemEnum.Reservado &&
                    itemRequest.Disponibilidade != StatusPedidoReservaItemEnum.Indisponivel)
                {
                    return PedidoReservaRespostaValidationOutcome.Fail(PedidoReservaRespostaErrorCode.BadRequest, "Disponibilidade deve ser Reservado ou Indisponivel.");
                }

                var sugeridas = itemRequest.IdsPecaSugerida ?? System.Array.Empty<int>();

                if (itemRequest.Disponibilidade == StatusPedidoReservaItemEnum.Reservado && sugeridas.Length > 0)
                    return PedidoReservaRespostaValidationOutcome.Fail(PedidoReservaRespostaErrorCode.BadRequest, "Item Reservado não aceita peça sugerida.");

                int[] sugeridaValidada = System.Array.Empty<int>();

                if (itemRequest.Disponibilidade == StatusPedidoReservaItemEnum.Indisponivel && sugeridas.Length > 0)
                {
                    // O Core só usa a primeira sugestão (PedidoReservaItem.Indisponivel);
                    // limitamos explicitamente a uma para não criar uma falsa
                    // expectativa de que as demais seriam consideradas.
                    var idPecaSugerida = sugeridas[0];
                    var peca = _pecaRepository.RetornaPorId(idPecaSugerida);

                    if (peca == null)
                        return PedidoReservaRespostaValidationOutcome.Fail(PedidoReservaRespostaErrorCode.BadRequest, $"Peça sugerida {idPecaSugerida} não existe.");

                    if (peca.Local == null || peca.Local.IdAfiliada != tenantAfiliadaId)
                        return PedidoReservaRespostaValidationOutcome.Fail(PedidoReservaRespostaErrorCode.BadRequest, "Peça sugerida não pertence a esta afiliada.");

                    sugeridaValidada = new[] { idPecaSugerida };
                }

                var pedidoItem = pendentes[itemRequest.IdItemPedidoReserva].PedidoItem;

                itensCommand.Add(new PedidoReservaRespostaCommand.PedidoReservaItemResposta
                {
                    IdItemPedidoReserva = itemRequest.IdItemPedidoReserva,
                    // Servidor deriva peça e período do pedido validado — nunca do cliente (TP-C §1 regra 8).
                    IdPeca = pedidoItem.IdPeca,
                    IdPeriodo = pedidoItem.IdPeriodo,
                    Disponibilidade = itemRequest.Disponibilidade,
                    IdsPecaSugerida = sugeridaValidada,
                });
            }

            var command = new PedidoReservaRespostaCommand
            {
                CodigoPedidoReserva = null, // preenchido pelo controller (vem da rota, não do body)
                Itens = itensCommand,
            };

            return PedidoReservaRespostaValidationOutcome.Ok(command);
        }
    }
}
