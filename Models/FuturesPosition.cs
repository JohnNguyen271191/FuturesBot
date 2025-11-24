using static FuturesBot.Utils.EnumTypesHelper;

namespace FuturesBot.Models
{
    public class FuturesPosition
    {
        public bool IsOpen => Direction != SignalType.None;

        public SignalType Direction { get; set; } = SignalType.None;
        public decimal EntryPrice { get; set; }
        public decimal Quantity { get; set; }
        public decimal Leverage { get; set; }
        public decimal StopLoss { get; set; }
        public decimal TakeProfit { get; set; }
        public DateTime OpenTime { get; set; }
    }
}
