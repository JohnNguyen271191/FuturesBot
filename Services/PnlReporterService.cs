using FuturesBot.Models;
using System.Text;

namespace FuturesBot.Services
{
    public class PnlReporterService(SlackNotifierService notifier)
    {
        private readonly SlackNotifierService _notifier = notifier;

        private readonly List<ClosedTrade> _closedTrades = new();

        private DateTime _currentDay = DateTime.UtcNow.Date;
        private bool _summarySentForToday = false;

        public async Task RegisterClosedTradeAsync(ClosedTrade trade)
        {
            _closedTrades.Add(trade);

            var msg =
    $@"[TRADE CLOSED] {trade.Symbol} {trade.Side}
Entry : {trade.Entry}
Exit  : {trade.Exit}
Qty   : {trade.Quantity:F6}
PnL   : {trade.PnlUSDT:F2} USDT";

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

            if (tradesToday.Count == 0) return;

            var totalPnl = tradesToday.Sum(t => t.PnlUSDT);
            var wins = tradesToday.Count(t => t.PnlUSDT > 0);
            var losses = tradesToday.Count(t => t.PnlUSDT < 0);

            var sb = new StringBuilder();
            sb.AppendLine("===== DAILY PnL SUMMARY =====");
            sb.AppendLine($"Date   : {_currentDay:yyyy-MM-dd} (UTC)");
            sb.AppendLine($"Trades : {tradesToday.Count} (W: {wins} / L: {losses})");
            sb.AppendLine($"Total  : {totalPnl:F2} USDT");
            sb.AppendLine("==============================");

            await _notifier.SendAsync(sb.ToString());

            _summarySentForToday = true;
        }
    }
}
