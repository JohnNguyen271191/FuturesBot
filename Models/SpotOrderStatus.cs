namespace FuturesBot.Models
{
    /// <summary>
    /// Minimal spot order status snapshot.
    /// </summary>
    public sealed class SpotOrderStatus
    {
        public string OrderId { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty; // NEW / PARTIALLY_FILLED / FILLED / CANCELED / REJECTED
        public decimal OrigQty { get; init; }
        public decimal ExecutedQty { get; init; }
        public decimal Price { get; init; }
        public string Side { get; init; } = string.Empty; // BUY / SELL
        public string Type { get; init; } = string.Empty; // LIMIT / LIMIT_MAKER / MARKET ...
    }
}
