using FuturesBot.Models;
using System.Text;
using FuturesBot.Config;

namespace FuturesBot.Services
{
    public class PnlReporterService(SlackNotifierService notifier, BotConfig botConfig)
    {
        private readonly SlackNotifierService _notifier = notifier;

        private readonly List<ClosedTrade> _closedTrades = [];

        private DateTime _currentDay = DateTime.UtcNow.Date;
        private bool _summarySentForToday = false;

        // ==============================
        //    DAILY PnL COOLDOWN LOGIC
        // ==============================

        // Vốn gốc dùng để tính %
        private decimal _dailyBaseCapital = 0m;

        private DateTime? _cooldownUntil = null;

        public void SetDailyBaseCapital()
        {
            _dailyBaseCapital = botConfig.AccountBalance;
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
PnL   : {trade.PnlUSDT:F2} USDT";

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
            var cooldownDuration = TimeSpan.FromHours(botConfig.CooldownDuration);
            var vnTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
            var cooldownVnTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow.Add(cooldownDuration), vnTimeZone);
            // Thua >= 5% → cooldown dựa vào cooldownDuration
            if (pnlPercent <= -botConfig.MaxDailyLossPercent)
            {
                _cooldownUntil = DateTime.UtcNow.Add(cooldownDuration);
                
                await _notifier.SendAsync($":warning: DAILY COOLDOWN TRIGGERED — Lỗ {pnlPercent:P2} (~{totalPnl:F2} USDT trên vốn {_dailyBaseCapital:F2}) → nghỉ {cooldownDuration.TotalHours} giờ, tới {cooldownVnTime:HH:mm}.");

                return;
            }

            // Lãi >= 5% → cooldown dựa vào cooldownDuration
            if (pnlPercent >= botConfig.MaxDailyLossPercent)
            {
                _cooldownUntil = DateTime.UtcNow.Add(cooldownDuration);

                await _notifier.SendAsync($":tada: DAILY COOLDOWN TRIGGERED — Lãi {pnlPercent:P2} (~{totalPnl:F2} USDT trên vốn {_dailyBaseCapital:F2}) → nghỉ {cooldownDuration.TotalHours} giờ, tới {cooldownVnTime:HH:mm}.");
            }
        }
    }
}
