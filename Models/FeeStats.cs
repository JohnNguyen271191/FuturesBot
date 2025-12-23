namespace FuturesBot.Models
{
    public sealed class FeeStats
    {
        public decimal EwmaRate { get; set; } = 0.0004m;
        public int Samples { get; set; } = 0;
        public DateTime LastUpdateUtc { get; set; } = DateTime.MinValue;
    }
}
