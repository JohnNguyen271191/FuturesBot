namespace FuturesBot.Models
{
    public sealed class SpotOrderResult
    {
        public string OrderId { get; set; } = string.Empty;
        public decimal ExecutedQty { get; set; }
        public decimal CummulativeQuoteQty { get; set; }
        public string RawStatus { get; set; } = string.Empty;
    }
}
