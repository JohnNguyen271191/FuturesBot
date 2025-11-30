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
    public class OrderManagerService(IExchangeClientService exchange, SlackNotifierService notify)
    {
        private readonly IExchangeClientService _exchange = exchange;
        private readonly SlackNotifierService _notify = notify;

        private const int MonitorIntervalMs = 3000;   // 3 giây
        private const decimal EarlyExitRR = 0.5m;     // chốt non khi đạt >= 0.5R và momentum đảo
        private const decimal HardReverseRR = 0.2m;   // nếu đảo trend mạnh và RR >= 0.2 thì đóng ngay
        private const decimal EmaBreakTolerance = 0.0015m; // ~0.15%

        // =====================================================================
        //                  MONITOR LIMIT ORDER (CHỜ KHỚP)
        // =====================================================================

        /// <summary>
        /// Gọi sau khi đã gửi LIMIT order.
        /// - Nếu setup bị phá (giá phá SL hoặc phá EMA34 mạnh) → CancelAllOpenOrders.
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

                // 1) Nếu không còn order mở nào nữa
                var openOrders = await _exchange.GetOpenOrdersAsync(symbol);
                var position = await _exchange.GetPositionAsync(symbol);

                bool hasPosition = position.PositionAmt != 0;
                bool hasOpenOrder = openOrders.Any();

                if (!hasOpenOrder)
                {
                    if (hasPosition)
                    {
                        await _notify.SendAsync($"[{symbol}] LIMIT đã khớp → chuyển sang monitor POSITION.");
                        _ = MonitorPositionAsync(signal);
                    }
                    else
                    {
                        await _notify.SendAsync($"[{symbol}] Không còn LIMIT & không có vị thế → dừng monitor LIMIT.");
                    }

                    return;
                }

                // 2) Còn LIMIT đang chờ → kiểm tra setup còn hợp lệ không
                var candles = await _exchange.GetRecentCandlesAsync(symbol, "15m", 80);
                if (candles.Count == 0)
                    continue;

                var last = candles[^1];
                decimal ema34 = ComputeEmaLast(candles, 34);

                bool setupBroken = false;

                if (isLong)
                {
                    // giá phá xuống SL hoặc phá EMA34 quá mạnh
                    if (last.Close < signal.StopLoss)
                        setupBroken = true;

                    if (last.Close < ema34 * (1 - EmaBreakTolerance))
                        setupBroken = true;
                }
                else
                {
                    if (last.Close > signal.StopLoss)
                        setupBroken = true;

                    if (last.Close > ema34 * (1 + EmaBreakTolerance))
                        setupBroken = true;
                }

                if (setupBroken)
                {
                    await _notify.SendAsync($"[{symbol}] Setup bị phá → Hủy LIMIT.");
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
        /// - Tự động early-exit khi đạt 0.5R nhưng momentum đảo.
        /// - đóng ngay nếu hard reverse (engulfing phá cấu trúc).
        /// - Trailing SL tại 1R và 1.5R.
        /// </summary>
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
                var candles = await _exchange.GetRecentCandlesAsync(symbol, "15m", 40);
                if (candles.Count < 3)
                    continue;

                var (reverse, hardReverse) = CheckMomentumReversal(candles, isLong);

                // EARLY EXIT: đã lãi >= 0.5R nhưng momentum đảo
                if (rr >= EarlyExitRR && reverse)
                {
                    await _notify.SendAsync(
                        $"[{symbol}] Early EXIT: RR={rr:F2} >= {EarlyExitRR}, momentum đảo chiều → đóng lệnh.");

                    await _exchange.ClosePositionAsync(symbol, Math.Abs(qty));
                    return;
                }

                // HARD REVERSE: nến engulfing phá cấu trúc, cắt sớm để tránh đảo trend mạnh
                if (hardReverse && rr >= -HardReverseRR) // cho phép cắt sớm, lỗ nhỏ hoặc hòa
                {
                    await _notify.SendAsync(
                        $"[{symbol}] HARD REVERSE: giá đảo mạnh, RR={rr:F2} → đóng ngay để bảo vệ vốn.");

                    await _exchange.ClosePositionAsync(symbol, Math.Abs(qty));
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
        /// hardReverse = nến engulfing phá high/low của nến trước
        /// </summary>
        private (bool reverse, bool hardReverse) CheckMomentumReversal(IReadOnlyList<Candle> candles15m, bool isLong)
        {
            int i = candles15m.Count - 1;
            var c0 = candles15m[i];     // nến hiện tại
            var c1 = candles15m[i - 1]; // nến trước

            bool reverse = false;
            bool hard = false;

            if (isLong)
            {
                // nến đỏ, vol không nhỏ hơn quá nhiều so với trước
                reverse = c0.Close < c0.Open && c0.Volume >= c1.Volume * 0.8m;
                // giá đóng dưới đáy nến trước → engulfing giảm
                hard = c0.Close < c1.Low;
            }
            else
            {
                reverse = c0.Close > c0.Open && c0.Volume >= c1.Volume * 0.8m;
                hard = c0.Close > c1.High;
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

            // Lấy khoảng 3 * period nến gần nhất để tính EMA cho đỡ lệch
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
    }
}
