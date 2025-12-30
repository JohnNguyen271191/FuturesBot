using static FuturesBot.Utils.EnumTypesHelper;

namespace FuturesBot.Models
{
    public class TradeSignal
    {
        public SignalType Type { get; set; } = SignalType.None;
        public decimal? EntryPrice { get; set; }
        public decimal? StopLoss { get; set; }
        public decimal? TakeProfit { get; set; }
        public string Reason { get; set; } = string.Empty;
        public DateTime Time { get; set; } = DateTime.UtcNow;
        public string Symbol { get; set; } = string.Empty;
        public TradeMode Mode { get; set; } = TradeMode.None;

        // ===== SPOT optional hints (non-breaking) =====
        public decimal? SpotEntryRefPrice { get; set; }
        public bool SpotExitNow { get; set; }
        public decimal? SpotTakeProfitOverride { get; set; }
    }
}
