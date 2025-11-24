using FuturesBot.Config;

namespace FuturesBot.Services
{
    public class RiskManager(BotConfig config)
    {
        private readonly BotConfig _config = config;

        public int TradesToday { get; private set; }
        public int LosingStreak { get; private set; }
        public decimal DailyPnl { get; private set; }
        public DateTime? LastTradeTime { get; private set; }

        public bool CanOpenNewTrade()
        {
            if (TradesToday >= _config.MaxTradesPerDay) return false;
            if (LosingStreak >= _config.MaxLosingStreak) return false;

            var maxDailyLoss = _config.AccountBalance * _config.MaxDailyLossPercent / 100m;
            if (DailyPnl <= -maxDailyLoss) return false;

            if (LastTradeTime.HasValue &&  DateTime.UtcNow - LastTradeTime.Value < _config.CooldownAfterTrade)
                return false;

            return true;
        }

        public decimal CalculatePositionSize(decimal entry, decimal stopLoss)
        {
            var riskAmount = _config.AccountBalance * _config.RiskPerTradePercent / 100m;
            var slDistance = Math.Abs(entry - stopLoss);
            if (slDistance <= 0) return 0;

            // Futures: loss ≈ slDistance * qty
            return riskAmount / slDistance;
        }

        public void RegisterResult(decimal pnl)
        {
            TradesToday++;
            LastTradeTime = DateTime.UtcNow;

            DailyPnl += pnl;
            if (pnl < 0) LosingStreak++;
            else LosingStreak = 0;

            _config.AccountBalance += pnl;
        }
    }
}
