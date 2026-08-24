using System.Collections.Generic;
using Veiculando.Domain.Enums;

namespace Veiculando.WhiteLabel.Api.Contracts.PedidosReserva
{
    /// <summary>
    /// Payload de POST /api/wl/pedidos-reserva/{codigo}/resposta (TP-C §1).
    /// O cliente NUNCA envia IdPeca, IdPeriodo, IdAfiliada ou IdUsuario — o
    /// servidor deriva tudo do pedido carregado e do tenant do Host.
    /// </summary>
    public class PedidoReservaRespostaRequest
    {
        public ICollection<PedidoReservaRespostaItemRequest> Itens { get; set; }
    }

    public class PedidoReservaRespostaItemRequest
    {
        public int IdItemPedidoReserva { get; set; }
        public StatusPedidoReservaItemEnum Disponibilidade { get; set; }

        /// <summary>Sempre array — nunca null (contrato explícito do TP-C §1).</summary>
        public int[] IdsPecaSugerida { get; set; } = System.Array.Empty<int>();
    }
}
