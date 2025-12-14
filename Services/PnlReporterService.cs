using FuturesBot.Models;
using System.Text;
using System.Linq;
using System.Collections.Generic;

namespace FuturesBot.Services
{
    public class PnlReporterService(SlackNotifierService notifier)
    {
        private readonly SlackNotifierService _notifier = notifier;

        private readonly List<ClosedTrade> _closedTrades = new();

        private DateTime _currentDay = DateTime.UtcNow.Date;
        private bool _summarySentForToday = false;

        // ==============================
        //    DAILY PnL COOLDOWN LOGIC
        // ==============================

        // Vốn gốc dùng để tính %
        private decimal _dailyBaseCapital = 0m;

        // Ngưỡng ăn/thua
        private const decimal LossThresholdPercent = 0.03m;   // -3%
        private const decimal ProfitThresholdPercent = 0.03m; // +3%

        // Thời gian nghỉ
        private static readonly TimeSpan LossCooldownDuration = TimeSpan.FromHours(1); // thua 3% nghỉ 1h
        private static readonly TimeSpan ProfitCooldownDuration = TimeSpan.FromHours(2); // ăn 3% nghỉ 2h

        private DateTime? _cooldownUntil = null;

        /// <summary>
        /// Set vốn gốc trong ngày để tính % PnL.
        /// Ví dụ: gọi lúc khởi động bot hoặc đầu ngày.
        /// </summary>
        public void SetDailyBaseCapital(decimal capital)
        {
            _dailyBaseCapital = capital;
        }

        /// <summary>
        /// Có đang trong thời gian cooldown không.
        /// </summary>
        public bool IsInCooldown()
        {
            if (_cooldownUntil == null) return false;

            if (DateTime.UtcNow >= _cooldownUntil.Value)
            {
                // Hết cooldown thì reset
                _cooldownUntil = null;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Thời gian cooldown còn lại (nếu có).
        /// </summary>
        public TimeSpan? GetCooldownRemaining()
        {
            if (!IsInCooldown()) return null;
            return _cooldownUntil!.Value - DateTime.UtcNow;
        }

        // ==============================
        //      REGISTER CLOSED TRADE
        // ==============================

        public async Task RegisterClosedTradeAsync(ClosedTrade trade)
        {
            _closedTrades.Add(trade);

            var msg =
$@"[TRADE CLOSED] {trade.Symbol} {trade.Side}
Entry : {trade.Entry}
Exit  : {trade.Exit}
Qty   : {trade.Quantity:F6}
PnL   : {trade.PnlUSDT:F2} USDT
Realized   : {trade.Realized:F2} USDT
Commission : {trade.Commission:F2} USDT
Funding    : {trade.Funding:F2} USDT";

            await _notifier.SendAsync(msg);

            _summarySentForToday = false;

            // Sau mỗi trade đóng xong thì check xem có cần bật cooldown hay không
            await CheckAndMaybeStartCooldownAsync();
        }

        // ==============================
        //    QUICK DAILY SUMMARY
        // ==============================

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

            if (_dailyBaseCapital > 0)
            {
                var pnlPercent = totalPnl / _dailyBaseCapital;
                sb.AppendLine($"Return : {pnlPercent:P2} trên vốn {_dailyBaseCapital:F2} USDT");
            }

            if (IsInCooldown())
            {
                var remain = GetCooldownRemaining();
                sb.AppendLine($"Status : COOLDOWN còn ~{remain?.TotalMinutes:F0} phút");
            }
            else
            {
                sb.AppendLine("Status : ACTIVE");
            }

            sb.AppendLine("==============================");

            await _notifier.SendAsync(sb.ToString());

            _summarySentForToday = true;
        }

        // ==============================
        //   INTERNAL: CHECK COOLDOWN
        // ==============================

        private async Task CheckAndMaybeStartCooldownAsync()
        {
            if (_dailyBaseCapital <= 0)
            {
                // Nếu chưa set vốn thì không tính %, tránh chia 0
                return;
            }

            // Nếu đã đang cooldown thì không check nữa
            if (IsInCooldown()) return;

            var tradesToday = _closedTrades
                .Where(t => t.CloseTime.Date == _currentDay)
                .ToList();

            if (tradesToday.Count == 0) return;

            var totalPnl = tradesToday.Sum(t => t.PnlUSDT);
            var pnlPercent = totalPnl / _dailyBaseCapital;

            // Thua >= 3% → cooldown 1h
            if (pnlPercent <= -LossThresholdPercent)
            {
                _cooldownUntil = DateTime.UtcNow.Add(LossCooldownDuration);

                await _notifier.SendAsync(
                    $":warning: DAILY COOLDOWN TRIGGERED — Lỗ {pnlPercent:P2} (~{totalPnl:F2} USDT trên vốn {_dailyBaseCapital:F2}) → nghỉ {LossCooldownDuration.TotalHours} giờ, tới {_cooldownUntil:HH:mm} UTC."
                );

                return;
            }

            // Lãi >= 3% → cooldown 2h
            if (pnlPercent >= ProfitThresholdPercent)
            {
                _cooldownUntil = DateTime.UtcNow.Add(ProfitCooldownDuration);

                await _notifier.SendAsync(
                    $":tada: DAILY COOLDOWN TRIGGERED — Lãi {pnlPercent:P2} (~{totalPnl:F2} USDT trên vốn {_dailyBaseCapital:F2}) → nghỉ {ProfitCooldownDuration.TotalHours} giờ, tới {_cooldownUntil:HH:mm} UTC."
                );
            }
        }
    }
}
