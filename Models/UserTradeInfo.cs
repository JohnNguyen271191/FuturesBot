namespace FuturesBot.Models
{
    public class UserTradeInfo
    {
        public string Symbol { get; set; } = "";
        public long Id { get; set; }
        public DateTime Time { get; set; }
        public decimal Price { get; set; }
        public decimal Qty { get; set; }
        public bool IsBuyer { get; set; }
    }
}
