namespace FuturesBot.Models
{
    /// <summary>
    /// Futures-only position snapshot.
    /// </summary>
    public sealed class FuturesPosition
    {
        public string Symbol { get; init; } = string.Empty;

        /// <summary>
        /// Positive = long, Negative = short.
        /// </summary>
        public decimal PositionAmt { get; init; }

        public decimal EntryPrice { get; init; }
        public decimal MarkPrice { get; init; }
        public decimal UnrealizedPnl { get; init; }

        public DateTime UpdateTimeUtc { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// LONG/SHORT (when hedge mode) or BOTH.
        /// </summary>
        public string PositionSide { get; init; } = "BOTH";

        public bool IsFlat => PositionAmt == 0m;
        public bool IsLong => PositionAmt > 0m;
        public bool IsShort => PositionAmt < 0m;
    }
}
