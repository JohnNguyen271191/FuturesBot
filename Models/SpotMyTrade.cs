namespace FuturesBot.Models
{
    /// <summary>
    /// Spot trade (Binance /api/v3/myTrades).
    /// Only the fields we need for daily report.
    /// </summary>
    public sealed class SpotMyTrade
    {
        public string Symbol { get; set; } = string.Empty;
        public long Id { get; set; }
        public long OrderId { get; set; }

        public decimal Price { get; set; }
        public decimal Qty { get; set; }
        public decimal QuoteQty { get; set; }

        public decimal Commission { get; set; }
        public string CommissionAsset { get; set; } = string.Empty;

        public bool IsBuyer { get; set; }
        public bool IsMaker { get; set; }

        public DateTime TimeUtc { get; set; }
    }
}
