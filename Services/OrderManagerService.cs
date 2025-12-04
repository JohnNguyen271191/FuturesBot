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
        private const decimal EmaBreakTolerance = 0.001m; // ~0.1%

        // =====================================================================
        //                  MONITOR LIMIT ORDER (CHỜ KHỚP)
        // =====================================================================

        public async Task MonitorLimitOrderAsync(TradeSignal signal)
        {
            string symbol = signal.Coin;
            bool isLong = signal.Type == SignalType.Long;

            await _notify.SendAsync($"[{symbol}] OrderManager: bắt đầu theo dõi LIMIT...");

            while (true)
            {
                await Task.Delay(MonitorIntervalMs);

                var openOrders = await _exchange.GetOpenOrdersAsync(symbol);
                var position = await _exchange.GetPositionAsync(symbol);

                bool hasPosition = position.PositionAmt != 0;
                bool hasOpenOrder = openOrders.Any();

                if (hasPosition)
                {
                    await _notify.SendAsync(
                        $"[{symbol}] Đã có position (qty={position.PositionAmt}) → chuyển sang monitor POSITION.");

                    _ = MonitorPositionAsync(signal);
                    return;
                }

                if (!hasOpenOrder)
                {
                    await _notify.SendAsync(
                        $"[{symbol}] Không còn LIMIT & không có vị thế → dừng monitor LIMIT.");
                    return;
                }

                var candles = await _exchange.GetRecentCandlesAsync(symbol, _botConfig.Intervals[0].FrameTime, 80);
                if (candles.Count == 0)
                    continue;

                var last = candles[^1];

                decimal ema34 = ComputeEmaLast(candles, 34);
                decimal ema89 = ComputeEmaLast(candles, 89);
                decimal ema200 = ComputeEmaLast(candles, 200);

                decimal entry = signal.EntryPrice ?? last.Close;

                decimal boundary = isLong
                    ? GetDynamicBoundaryForLong(entry, ema34, ema89, ema200)
                    : GetDynamicBoundaryForShort(entry, ema34, ema89, ema200);

                bool setupBroken = false;

                if (isLong)
                {
                    if (last.Close < signal.StopLoss)
                        setupBroken = true;

                    if (boundary > 0 &&
                        last.Close < boundary * (1 - EmaBreakTolerance))
                    {
                        setupBroken = true;
                    }
                }
                else
                {
                    if (last.Close > signal.StopLoss)
                        setupBroken = true;

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

        public async Task MonitorPositionAsync(TradeSignal signal)
        {
            string symbol = signal.Coin;
            bool isLong = signal.Type == SignalType.Long;

            decimal entry = signal.EntryPrice.GetValueOrDefault();
            decimal sl = signal.StopLoss.GetValueOrDefault();
            decimal tp = signal.TakeProfit.GetValueOrDefault();

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

                // *** CHANGED: lấy cả position và openOrders để quyết định có thực sự hết vị thế hay chưa
                var pos = await _exchange.GetPositionAsync(symbol);
                var openOrders = await _exchange.GetOpenOrdersAsync(symbol);

                if (pos == null)
                {
                    await _notify.SendAsync($"[{symbol}] WARNING: GetPositionAsync trả null, tiếp tục monitor.");
                    continue;
                }

                decimal qty = pos.PositionAmt;

                // Không còn vị thế: chỉ dừng khi qty == 0 VÀ không còn open order nào
                if (qty == 0)
                {
                    bool hasOpenOrder = openOrders != null && openOrders.Any();

                    if (!hasOpenOrder)
                    {
                        await _notify.SendAsync($"[{symbol}] Không còn vị thế & không còn open order → dừng monitor POSITION.");
                        return;
                    }
                    else
                    {
                        // thường là do API trả tạm thời sai hoặc đang đóng/mở lại,
                        // mình không dừng monitor để khỏi bị lệch như case DOGE.
                        await _notify.SendAsync(
                            $"[{symbol}] WARNING: qty=0 nhưng còn {openOrders.Count} open orders → tiếp tục monitor.");
                        continue;
                    }
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

                // EARLY EXIT
                if (rr >= EarlyExitRR && reverse)
                {
                    await _notify.SendAsync(
                        $"[{symbol}] Early EXIT: RR={rr:F2} >= {EarlyExitRR}, momentum đảo chiều → đóng lệnh.");

                    await _exchange.ClosePositionAsync(symbol, qty);
                    return;
                }

                // HARD REVERSE
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
                    newSL = isLong ? entry + risk * 0.5m : entry - risk * 0.5m;
                }

                if (rr >= 1.5m)
                {
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

        private (bool reverse, bool hardReverse) CheckMomentumReversal(
            IReadOnlyList<Candle> candles15m,
            bool isLong,
            decimal entryPrice)
        {
            int i = candles15m.Count - 1;
            var c0 = candles15m[i];
            var c1 = candles15m[i - 1];

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
                reverse = c0.Close < c0.Open && c0.Volume >= c1.Volume * 0.8m;

                if (boundary > 0)
                {
                    bool breakDown = c0.Close < boundary * (1 - EmaBreakTolerance);
                    hard = breakDown;
                }
            }
            else
            {
                reverse = c0.Close > c0.Open && c0.Volume >= c1.Volume * 0.8m;

                if (boundary > 0)
                {
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

            await _exchange.CancelAllOpenOrdersAsync(symbol);

            var pos = await _exchange.GetPositionAsync(symbol);
            decimal qty = Math.Abs(pos.PositionAmt);
            if (qty <= 0)
            {
                await _notify.SendAsync($"[{symbol}] Không tìm thấy position khi update SL.");
                return;
            }

            string side = isLong ? "SELL" : "BUY";
            string posSide = isLong ? "LONG" : "SHORT";

            await _exchange.PlaceStopOnlyAsync(symbol, side, posSide, qty, newSL);
        }

        // =====================================================================
        //                    HELPER: TÍNH EMA CUỐI (QUICK)
        // =====================================================================

        private static decimal ComputeEmaLast(IReadOnlyList<Candle> candles, int period)
        {
            if (candles == null || candles.Count == 0)
                return 0m;

            var closes = candles
                .Skip(Math.Max(0, candles.Count - period * 3))
                .Select(c => c.Close)
                .ToArray();

            decimal k = 2m / (period + 1);
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

        private static decimal GetDynamicBoundaryForShort(decimal entry, decimal ema34, decimal ema89, decimal ema200)
        {
            var emas = new List<decimal>();
            if (ema34 > 0) emas.Add(ema34);
            if (ema89 > 0) emas.Add(ema89);
            if (ema200 > 0) emas.Add(ema200);

            if (emas.Count == 0)
                return 0m;

            var candidate = emas
                .Where(e => e >= entry)
                .OrderBy(e => e)
                .FirstOrDefault();

            return candidate == 0m ? 0m : candidate;
        }

        private static decimal GetDynamicBoundaryForLong(decimal entry, decimal ema34, decimal ema89, decimal ema200)
        {
            var emas = new List<decimal>();
            if (ema34 > 0) emas.Add(ema34);
            if (ema89 > 0) emas.Add(ema89);
            if (ema200 > 0) emas.Add(ema200);

            if (emas.Count == 0)
                return 0m;

            var candidate = emas
                .Where(e => e <= entry)
                .OrderByDescending(e => e)
                .FirstOrDefault();

            return candidate == 0m ? 0m : candidate;
        }
    }
}
