using System;

namespace Veiculando.WhiteLabel.Api.Contracts.PedidosReserva
{
    /// <summary>Item de GET /api/wl/pedidos-reserva (TP-C §3, PRD §6.10).</summary>
    public class PedidoReservaListItem
    {
        public int Id { get; set; }
        public string Codigo { get; set; }
        public int Status { get; set; }
        public int QuantidadePecas { get; set; }
        public decimal ValorLiquido { get; set; }
        public DateTime DataCadastro { get; set; }
    }
}
