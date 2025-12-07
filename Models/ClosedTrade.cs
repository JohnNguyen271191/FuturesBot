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
        public decimal PnlUSDT { get; set; }
        public decimal Realized { get; set; }
        public decimal Commission { get; set; }
        public decimal Funding { get; set; }
    }
}
