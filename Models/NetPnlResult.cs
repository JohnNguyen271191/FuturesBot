namespace FuturesBot.Models
{
    public class NetPnlResult
    {
        public decimal Realized { get; set; }
        public decimal Commission { get; set; }
        public decimal Funding { get; set; }
        public decimal Net => Realized - Commission + Funding;
    }
}
