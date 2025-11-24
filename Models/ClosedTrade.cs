using static FuturesBot.Utils.EnumTypesHelper;

namespace FuturesBot.Models
{
    public class ClosedTrade
    {
        public string Symbol { get; set; } = "";
        public SignalType Side { get; set; }
        public decimal Entry { get; set; }
        public decimal Exit { get; set; }
        public decimal Quantity { get; set; }
        public DateTime OpenTime { get; set; }
        public DateTime CloseTime { get; set; }

        public decimal PnlUSDT =>
            Side == SignalType.Long
                ? (Exit - Entry) * Quantity
                : (Entry - Exit) * Quantity;

        public decimal RMultiple { get; set; }  // +1.5, -1.0, v.v.
    }
}
