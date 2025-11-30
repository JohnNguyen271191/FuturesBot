namespace FuturesBot.Models
{
    public class OpenOrderInfo
    {
        public string Symbol { get; set; } = string.Empty;
        public string ClientOrderId { get; set; } = string.Empty;
        public string OrderId { get; set; } = string.Empty;
        public string Side { get; set; } = string.Empty;  // BUY / SELL
        public string Type { get; set; } = string.Empty;  // LIMIT / STOP / TAKE_PROFIT...
        public decimal Price { get; set; }
        public decimal OrigQty { get; set; }
        public decimal ExecutedQty { get; set; }
        public decimal StopPrice { get; set; }
    }
}
