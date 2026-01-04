namespace FuturesBot.Models
{
    public sealed class SlTpDetection
    {
        public decimal? Sl { get; set; }
        public decimal? Tp { get; set; }
        public int TotalOrders { get; set; }
        public int ConsideredOrders { get; set; }
    }
}
