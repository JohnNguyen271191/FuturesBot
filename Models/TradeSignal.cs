using static FuturesBot.Utils.EnumTypesHelper;

namespace FuturesBot.Models
{
    public class TradeSignal
    {
        public SignalType Type { get; set; } = SignalType.None;
        public decimal? EntryPrice { get; set; }
        public decimal? StopLoss { get; set; }
        public decimal? TakeProfit { get; set; }
        /// <summary>
        /// Prefer market order execution (use carefully). Default false.
        /// </summary>
        public bool UseMarketOrder { get; set; } = false;
        public string Reason { get; set; } = string.Empty;
        public DateTime Time { get; set; } = DateTime.UtcNow;
        public string Symbol { get; set; } = string.Empty;
        public TradeMode Mode { get; set; } = TradeMode.None;
    }
}
