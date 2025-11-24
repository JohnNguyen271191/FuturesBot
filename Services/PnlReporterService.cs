using FuturesBot.Models;
using System.Text;
using static FuturesBot.Utils.EnumTypesHelper;

namespace FuturesBot.Services
{
    public class PnlReporterService(SlackNotifierService notifier)
    {
        private readonly SlackNotifierService _notifier = notifier;

        private readonly List<ClosedTrade> _closedTrades = new();
        private readonly List<PlannedTrade> _plannedTrades = new();

        private DateTime _currentDay = DateTime.UtcNow.Date;
        private bool _summarySentForToday = false;

        public void RegisterPlannedTrade(
            string symbol,
            SignalType side,
            decimal entry,
            decimal sl,
            decimal tp,
            decimal qty,
            decimal riskAmount,
            decimal rewardAmount)
        {
            _plannedTrades.Add(new PlannedTrade
            {
                Symbol = symbol,
                Side = side,
                Entry = entry,
                StopLoss = sl,
                TakeProfit = tp,
                Quantity = qty,
                RiskAmount = riskAmount,
                RewardAmount = rewardAmount,
                Time = DateTime.UtcNow
            });
        }

        public async Task RegisterClosedTradeAsync(ClosedTrade trade)
        {
            // cố gắng match với plan gần nhất
            var plan = _plannedTrades
                .Where(p => p.Symbol == trade.Symbol && p.Side == trade.Side)
                .OrderByDescending(p => p.Time)
                .FirstOrDefault();

            if (plan != null && plan.RiskAmount != 0)
            {
                trade.RMultiple = trade.PnlUSDT / Math.Abs(plan.RiskAmount);
            }
            else
            {
                trade.RMultiple = 0;
            }

            _closedTrades.Add(trade);

            var msg =
    $@"[TRADE CLOSED] {trade.Symbol} {trade.Side}
Entry : {trade.Entry}
Exit  : {trade.Exit}
Qty   : {trade.Quantity:F6}
PnL   : {trade.PnlUSDT:F2} USDT
R     : {trade.RMultiple:F2}";

            await _notifier.SendAsync(msg);

            _summarySentForToday = false;
        }

        public async Task SendQuickDailySummary()
        {
            var nowDay = DateTime.UtcNow.Date;
            if (nowDay != _currentDay)
            {
                _currentDay = nowDay;
                _summarySentForToday = false;
            }

            if (_summarySentForToday) return;

            var tradesToday = _closedTrades
                .Where(t => t.CloseTime.Date == _currentDay)
                .ToList();

            if (!tradesToday.Any()) return;

            var totalPnl = tradesToday.Sum(t => t.PnlUSDT);
            var totalR = tradesToday.Sum(t => t.RMultiple);
            var wins = tradesToday.Count(t => t.PnlUSDT > 0);
            var losses = tradesToday.Count(t => t.PnlUSDT < 0);

            var sb = new StringBuilder();
            sb.AppendLine("===== DAILY PnL SUMMARY =====");
            sb.AppendLine($"Date   : {_currentDay:yyyy-MM-dd} (UTC)");
            sb.AppendLine($"Trades : {tradesToday.Count} (W: {wins} / L: {losses})");
            sb.AppendLine($"Total  : {totalPnl:F2} USDT");
            sb.AppendLine($"TotalR : {totalR:F2} R");
            sb.AppendLine("==============================");

            await _notifier.SendAsync(sb.ToString());

            _summarySentForToday = true;
        }
    }
}
