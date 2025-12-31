using FuturesBot.Config;

namespace FuturesBot.Services
{
    public class RiskManager(BotConfig config)
    {
        private readonly BotConfig _config = config;

        /// <summary>
        /// Futures sizing by risk, using an explicit balance cap.
        /// </summary>
        public decimal CalculatePositionSize(decimal entry, decimal stopLoss, decimal accountBalanceCap, decimal riskPerTradePercent)
        {
            var minQuantity = 0.002m;
            var baseBalance = accountBalanceCap > 0 ? accountBalanceCap : _config.AccountBalance;
            var riskAmount = baseBalance * riskPerTradePercent / 100m;
            var slDistance = Math.Abs(entry - stopLoss);
            if (slDistance <= 0) return 0;

            // Futures: loss ≈ slDistance * qty
            var quantity = Math.Round(riskAmount / slDistance, 3);
            return quantity < minQuantity ? minQuantity : quantity;
        }

        // Back-compat overload
        public decimal CalculatePositionSize(decimal entry, decimal stopLoss, decimal riskPerTradePercent)
            => CalculatePositionSize(entry, stopLoss, _config.AccountBalance, riskPerTradePercent);
    }
}
