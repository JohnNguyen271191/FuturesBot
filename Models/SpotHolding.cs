namespace FuturesBot.Models
{
    /// <summary>
    /// Spot-only holding snapshot for a single asset.
    /// </summary>
    public sealed class SpotHolding
    {
        public string Asset { get; init; } = string.Empty;

        public decimal Free { get; init; }
        public decimal Locked { get; init; }
    }
}
