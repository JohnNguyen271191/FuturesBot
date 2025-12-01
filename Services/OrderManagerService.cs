using FuturesBot.Config;
using FuturesBot.IServices;
using FuturesBot.Models;
using static FuturesBot.Utils.EnumTypesHelper;

namespace FuturesBot.Services
{
    /// <summary>
    /// Quản lý lệnh sau khi TradingStrategy bắn signal:
    /// - Monitor lệnh LIMIT: nếu setup hỏng thì hủy limit.
    /// - Monitor vị thế đang mở: early exit, hard reverse, trailing SL theo RR.
    /// - Chạy realtime theo interval 3s, không phụ thuộc tick 15m.
    /// 
    /// 1) Sau khi đặt LIMIT thành công:
    ///    _ = _orderManager.MonitorLimitOrderAsync(signal);
    /// 
    /// 2) Nếu vào lệnh MARKET ngay:
    ///    _ = _orderManager.MonitorPositionAsync(signal);
    /// 
    /// 3) Hoặc trong MonitorLimitOrderAsync, khi phát hiện vị thế đã mở
    ///    thì tự gọi MonitorPositionAsync.
    /// </summary>
    public class OrderManagerService(IExchangeClientService exchange, SlackNotifierService notify, BotConfig config)
    {
        private readonly IExchangeClientService _exchange = exchange;
        private readonly SlackNotifierService _notify = notify;
        private readonly BotConfig _botConfig = config;

        private const int MonitorIntervalMs = 3000;   // 3 giây
        private const decimal EarlyExitRR = 0.5m;     // chốt non khi đạt >= 0.5R và momentum đảo
        private const decimal HardReverseRR = 0.2m;   // nếu đảo trend mạnh và RR >= 0.2 thì đóng ngay
        private const decimal EmaBreakTolerance = 0.003m; // ~0.3%

        // =====================================================================
        //                  MONITOR LIMIT ORDER (CHỜ KHỚP)
        // =====================================================================

        /// <summary>
        /// Gọi sau khi đã gửi LIMIT order.
        /// - Nếu setup bị phá (giá phá SL hoặc phá EMA “ranh giới” theo entry) → CancelAllOpenOrders.
        /// - Nếu LIMIT khớp (có positionAmt != 0) → tự động chuyển sang MonitorPositionAsync.
        /// </summary>
        public async Task MonitorLimitOrderAsync(TradeSignal signal)
        {
            string symbol = signal.Coin;
            bool isLong = signal.Type == SignalType.Long;

            await _notify.SendAsync($"[{symbol}] OrderManager: bắt đầu theo dõi LIMIT...");

            while (true)
            {
                await Task.Delay(MonitorIntervalMs);

                // 1) Kiểm tra position + open order
                var openOrders = await _exchange.GetOpenOrdersAsync(symbol);
                var position   = await _exchange.GetPositionAsync(symbol);

                bool hasPosition  = position.PositionAmt != 0;
                bool hasOpenOrder = openOrders.Any();

                // ===== ƯU TIÊN POSITION: nếu đã có vị thế thì chuyển sang monitor POSITION =====
                if (hasPosition)
                {
                    await _notify.SendAsync(
                        $"[{symbol}] Đã có position (qty={position.PositionAmt}) → chuyển sang monitor POSITION.");

                    // fire & forget
                    _ = MonitorPositionAsync(signal);
                    return; // dừng monitor LIMIT
                }

                // Không còn position + không còn LIMIT nào nữa → dừng monitor LIMIT
                if (!hasOpenOrder)
                {
                    await _notify.SendAsync(
                        $"[{symbol}] Không còn LIMIT & không có vị thế → dừng monitor LIMIT.");
                    return;
                }

                // 2) Còn LIMIT đang chờ → kiểm tra setup còn hợp lệ không
                var candles = await _exchange.GetRecentCandlesAsync(symbol, _botConfig.Intervals[0].FrameTime, 80);
                if (candles.Count == 0)
                    continue;

                var last  = candles[^1];

                // EMA động
                decimal ema34 = ComputeEmaLast(candles, 34);
                decimal ema89 = ComputeEmaLast(candles, 89);
                decimal ema200 = ComputeEmaLast(candles, 200);

                decimal entry = signal.EntryPrice ?? last.Close;

                // Ranh giới “đảo trend” tùy theo vị trí entry so với EMA
                decimal boundary = isLong
                    ? GetDynamicBoundaryForLong(entry, ema34, ema89, ema200)
                    : GetDynamicBoundaryForShort(entry, ema34, ema89, ema200);

                bool setupBroken = false;

                if (isLong)
                {
                    // phá xuống SL
                    if (last.Close < signal.StopLoss)
                        setupBroken = true;

                    // Long: chỉ coi là hỏng setup khi giá đóng dưới EMA boundary khá sâu
                    if (boundary > 0 &&
                        last.Close < boundary * (1 - EmaBreakTolerance))
                    {
                        setupBroken = true;
                    }
                }
                else
                {
                    // Short: phá lên SL
                    if (last.Close > signal.StopLoss)
                        setupBroken = true;

                    // Short: chỉ coi là hỏng setup khi giá đóng trên EMA boundary khá sâu
                    if (boundary > 0 &&
                        last.Close > boundary * (1 + EmaBreakTolerance))
                    {
                        setupBroken = true;
                    }
                }

                if (setupBroken)
                {
                    await _notify.SendAsync(
                        $"[{symbol}] Setup bị phá (EMA boundary={boundary:F6}) → Hủy LIMIT.");
                    await _exchange.CancelAllOpenOrdersAsync(symbol);
                    return;
                }
            }
        }

        // =====================================================================
        //                     MONITOR VỊ THẾ ĐANG MỞ
        // =====================================================================

        /// <summary>
        /// Gọi sau khi vào lệnh (MARKET hoặc LIMIT đã khớp).
        /// - Tự động early-exit khi đạt 0.5R nhưng momentum đảo (nến ngược màu + vol).
        /// - Đóng ngay nếu hard reverse (giá phá EMA boundary ngược chiều).
        /// - Trailing SL tại 1R và 1.5R.
        /// </summary>
        public async Task MonitorPositionAsync(TradeSignal signal)
        {
            string symbol = signal.Coin;
            bool isLong = signal.Type == SignalType.Long;

            decimal entry = signal.EntryPrice.GetValueOrDefault();
            decimal sl    = signal.StopLoss.GetValueOrDefault();
            decimal tp    = signal.TakeProfit.GetValueOrDefault();

            decimal risk = isLong ? entry - sl : sl - entry;
            if (risk <= 0)
            {
                await _notify.SendAsync($"[{symbol}] OrderManager: risk <= 0, bỏ qua monitor position.");
                return;
            }

            await _notify.SendAsync($"[{symbol}] OrderManager: bắt đầu theo dõi POSITION...");

            while (true)
            {
                await Task.Delay(MonitorIntervalMs);

                var pos = await _exchange.GetPositionAsync(symbol);
                decimal qty = pos.PositionAmt;

                // Không còn vị thế
                if (qty == 0)
                {
                    await _notify.SendAsync($"[{symbol}] Không còn vị thế → dừng monitor POSITION.");
                    return;
                }

                decimal price = pos.MarkPrice;

                // 1) Hit SL hoặc TP (sàn tự đóng) → thoát
                if ((isLong && price <= sl) || (!isLong && price >= sl))
                {
                    await _notify.SendAsync($"[{symbol}] SL hit (price={price}) → stop monitor.");
                    return;
                }

                if ((isLong && price >= tp) || (!isLong && price <= tp))
                {
                    await _notify.SendAsync($"[{symbol}] TP hit (price={price}) → stop monitor.");
                    return;
                }

                // 2) Tính RR hiện tại
                decimal rr = isLong
                    ? (price - entry) / risk
                    : (entry - price) / risk;

                // 3) Xem momentum 15m để quyết định early-exit / hard reverse
                var candles = await _exchange.GetRecentCandlesAsync(symbol, _botConfig.Intervals[0].FrameTime, 40);
                if (candles.Count < 3)
                    continue;

                var (reverse, hardReverse) = CheckMomentumReversal(candles, isLong, entry);

                // EARLY EXIT: đã lãi >= 0.5R nhưng momentum đảo
                if (rr >= EarlyExitRR && reverse)
                {
                    await _notify.SendAsync(
                        $"[{symbol}] Early EXIT: RR={rr:F2} >= {EarlyExitRR}, momentum đảo chiều → đóng lệnh.");

                    await _exchange.ClosePositionAsync(symbol, qty);
                    return;
                }

                // HARD REVERSE: giá phá EMA boundary ngược chiều, RR chưa lỗ quá -0.2R
                if (hardReverse && rr >= -HardReverseRR)
                {
                    await _notify.SendAsync(
                        $"[{symbol}] HARD REVERSE (phá EMA boundary ngược trend), RR={rr:F2} → đóng ngay để bảo vệ vốn.");

                    await _exchange.ClosePositionAsync(symbol, qty);
                    return;
                }

                // 4) Trailing SL theo RR
                decimal newSL = sl;

                if (rr >= 1m)
                {
                    // bảo vệ 0.5R
                    newSL = isLong ? entry + risk * 0.5m : entry - risk * 0.5m;
                }

                if (rr >= 1.5m)
                {
                    // bảo vệ 1R
                    newSL = isLong ? entry + risk * 1m : entry - risk * 1m;
                }

                if (newSL != sl)
                {
                    sl = newSL;
                    await UpdateStopLossAsync(symbol, sl, isLong);
                }
            }
        }

        // =====================================================================
        //                         HELPER: MOMENTUM
        // =====================================================================

        /// <summary>
        /// reverse = momentum yếu đi / đảo màu + vol cao
        /// hardReverse = giá phá EMA boundary ngược chiều (dùng EMA dynamic theo entry)
        /// </summary>
        private (bool reverse, bool hardReverse) CheckMomentumReversal(
            IReadOnlyList<Candle> candles15m,
            bool isLong,
            decimal entryPrice)
        {
            int i = candles15m.Count - 1;
            var c0 = candles15m[i];     // nến hiện tại
            var c1 = candles15m[i - 1]; // nến trước

            // EMA tại nến hiện tại
            decimal ema34 = ComputeEmaLast(candles15m, 34);
            decimal ema89 = ComputeEmaLast(candles15m, 89);
            decimal ema200 = ComputeEmaLast(candles15m, 200);

            decimal boundary = isLong
                ? GetDynamicBoundaryForLong(entryPrice, ema34, ema89, ema200)
                : GetDynamicBoundaryForShort(entryPrice, ema34, ema89, ema200);

            bool reverse;
            bool hard = false;

            if (isLong)
            {
                // Long: nến đỏ + vol không quá nhỏ → dấu hiệu chốt lời / đảo nhẹ
                reverse = c0.Close < c0.Open && c0.Volume >= c1.Volume * 0.8m;

                if (boundary > 0)
                {
                    // Hard reverse: giá đóng dưới EMA boundary khá sâu
                    bool breakDown = c0.Close < boundary * (1 - EmaBreakTolerance);
                    hard = breakDown;
                }
            }
            else
            {
                // Short: nến xanh + vol không quá nhỏ
                reverse = c0.Close > c0.Open && c0.Volume >= c1.Volume * 0.8m;

                if (boundary > 0)
                {
                    // Hard reverse: giá đóng trên EMA boundary khá sâu
                    bool breakUp = c0.Close > boundary * (1 + EmaBreakTolerance);
                    hard = breakUp;
                }
            }

            return (reverse, hard);
        }

        // =====================================================================
        //                   HELPER: UPDATE STOPLOSS TRÊN SÀN
        // =====================================================================

        private async Task UpdateStopLossAsync(string symbol, decimal newSL, bool isLong)
        {
            await _notify.SendAsync($"[{symbol}] Update Trailing SL → {newSL}");

            // Huỷ mọi order TP/SL cũ
            await _exchange.CancelAllOpenOrdersAsync(symbol);

            // Lấy lại position để biết quantity & positionSide
            var pos = await _exchange.GetPositionAsync(symbol);
            decimal qty = Math.Abs(pos.PositionAmt);
            if (qty <= 0)
            {
                await _notify.SendAsync($"[{symbol}] Không tìm thấy position khi update SL.");
                return;
            }

            string side    = isLong ? "SELL"  : "BUY";
            string posSide = isLong ? "LONG"  : "SHORT";

            await _exchange.PlaceStopOnlyAsync(symbol, side, posSide, qty, newSL);
        }

        // =====================================================================
        //                    HELPER: TÍNH EMA CUỐI (QUICK)
        // =====================================================================

        private static decimal ComputeEmaLast(IReadOnlyList<Candle> candles, int period)
        {
            if (candles == null || candles.Count == 0)
                return 0m;

            // Lấy khoảng 3 * period nến gần nhất để tính EMA cho đỡ lệch
            var closes = candles
                .Skip(Math.Max(0, candles.Count - period * 3))
                .Select(c => c.Close)
                .ToArray();

            decimal k   = 2m / (period + 1);
            decimal ema = closes[0];

            for (int i = 1; i < closes.Length; i++)
            {
                ema = closes[i] * k + ema * (1 - k);
            }

            return ema;
        }

        // =====================================================================
        //           HELPER: EMA BOUNDARY ĐỘNG CHO LONG / SHORT
        // =====================================================================

        /// <summary>
        /// Short: lấy EMA gần nhất phía TRÊN entry (nếu có).
        /// Ví dụ downtrend: entry giữa 34–89 → boundary = EMA89.
        /// </summary>
        private static decimal GetDynamicBoundaryForShort(decimal entry, decimal ema34, decimal ema89, decimal ema200)
        {
            var emas = new List<decimal>();
            if (ema34 > 0) emas.Add(ema34);
            if (ema89 > 0) emas.Add(ema89);
            if (ema200 > 0) emas.Add(ema200);

            if (emas.Count == 0)
                return 0m;

            // EMA >= entry, chọn cái nhỏ nhất (gần entry nhất phía trên)
            var candidate = emas
                .Where(e => e >= entry)
                .OrderBy(e => e)
                .FirstOrDefault();

            return candidate == 0m ? 0m : candidate;
        }

        /// <summary>
        /// Long: lấy EMA gần nhất phía DƯỚI entry (nếu có).
        /// Ví dụ uptrend: entry giữa 89–34 → boundary = EMA89.
        /// </summary>
        private static decimal GetDynamicBoundaryForLong(decimal entry, decimal ema34, decimal ema89, decimal ema200)
        {
            var emas = new List<decimal>();
            if (ema34 > 0) emas.Add(ema34);
            if (ema89 > 0) emas.Add(ema89);
            if (ema200 > 0) emas.Add(ema200);

            if (emas.Count == 0)
                return 0m;

            // EMA <= entry, chọn cái lớn nhất (gần entry nhất phía dưới)
            var candidate = emas
                .Where(e => e <= entry)
                .OrderByDescending(e => e)
                .FirstOrDefault();

            return candidate == 0m ? 0m : candidate;
        }
    }
}
