namespace FuturesBot.Models
{
    public class PositionInfo
    {
        public string Symbol { get; set; } = "";
        public decimal PositionAmt { get; set; } // >0 long, <0 short, 0 = flat
        public decimal EntryPrice { get; set; }
        public decimal MarkPrice { get; set; }
        public DateTime UpdateTime { get; set; }

        public bool IsFlat => PositionAmt == 0;
        public bool IsLong => PositionAmt > 0;
        public bool IsShort => PositionAmt < 0;
    }
}
