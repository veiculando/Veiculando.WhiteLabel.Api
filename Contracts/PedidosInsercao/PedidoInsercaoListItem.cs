namespace Veiculando.WhiteLabel.Api.Contracts.PedidosInsercao
{
    /// <summary>Item de GET /api/wl/pedidos-insercao (TP-C §3, PRD §6.11).</summary>
    public class PedidoInsercaoListItem
    {
        public int Id { get; set; }
        public string Codigo { get; set; }
        public int Status { get; set; }
        public int QuantidadePecas { get; set; }
        public decimal ValorLiquido { get; set; }
    }
}
