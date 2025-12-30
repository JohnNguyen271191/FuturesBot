using FuturesBot.Config;

namespace FuturesBot.Services
{
    public class RiskManager(BotConfig config)
    {
        private readonly BotConfig _config = config;

        public decimal CalculatePositionSize(decimal entry, decimal stopLoss, decimal riskPerTradePercent)
        {
            var minQuantity = 0.002m;
            var riskAmount = _config.Global.AccountBalance * riskPerTradePercent / 100m;
            var slDistance = Math.Abs(entry - stopLoss);
            if (slDistance <= 0) return 0;

            // Futures: loss ≈ slDistance * qty
            var quantity = Math.Round(riskAmount / slDistance, 3);
            return quantity < minQuantity ? minQuantity : quantity;
        }
    }
}
