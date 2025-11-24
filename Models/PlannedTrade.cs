using static FuturesBot.Utils.EnumTypesHelper;

namespace FuturesBot.Models
{
    public class PlannedTrade
    {
        public string Symbol { get; set; } = "";
        public SignalType Side { get; set; }
        public decimal Entry { get; set; }
        public decimal StopLoss { get; set; }
        public decimal TakeProfit { get; set; }
        public decimal Quantity { get; set; }
        public decimal RiskAmount { get; set; }  // âm
        public decimal RewardAmount { get; set; } // dương
        public DateTime Time { get; set; }
    }
}
