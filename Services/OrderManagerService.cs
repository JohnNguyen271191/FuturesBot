using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using FuturesBot.Config;
using FuturesBot.IServices;
using FuturesBot.Models;
using static FuturesBot.Utils.EnumTypesHelper;

namespace FuturesBot.Services
{
    public class OrderManagerService
    {
        private readonly IExchangeClientService _exchange;
        private readonly SlackNotifierService _notify;
        private readonly BotConfig _botConfig;

        private const int MonitorIntervalMs = 3000;
        private const decimal EarlyExitRR = 0.5m;
        private const decimal HardReverseRR = 0.2m;
        private const decimal EmaBreakTolerance = 0.001m;

        // Timeout cho LIMIT: 20 phút
        private static readonly TimeSpan LimitTimeout = TimeSpan.FromMinutes(20);

        // ============================================================
        // TRACK SYMBOL ĐANG ĐƯỢC GIÁM SÁT
        // ============================================================

        private readonly ConcurrentDictionary<string, bool> _monitoringLimit = new();
        private readonly ConcurrentDictionary<string, bool> _monitoringPosition = new();

        private bool IsMonitoringLimit(string symbol)
            => _monitoringLimit.ContainsKey(symbol);

        private bool IsMonitoringPosition(string symbol)
            => _monitoringPosition.ContainsKey(symbol);

        private bool TryStartMonitoringLimit(string symbol)
            => _monitoringLimit.TryAdd(symbol, true);

        private bool TryStartMonitoringPosition(string symbol)
            => _monitoringPosition.TryAdd(symbol, true);

        private void ClearMonitoringLimit(string symbol)
            => _monitoringLimit.TryRemove(symbol, out _);

        private void ClearMonitoringPosition(string symbol)
            => _monitoringPosition.TryRemove(symbol, out _);

        private void ClearAllMonitoring(string symbol)
        {
            ClearMonitoringLimit(symbol);
            ClearMonitoringPosition(symbol);
        }

        // ============================================================
        // CONSTRUCTOR
        // ============================================================

        public OrderManagerService(
            IExchangeClientService exchange,
            SlackNotifierService notify,
            BotConfig config)
        {
            _exchange = exchange;
            _notify = notify;
            _botConfig = config;
        }

        // ============================================================
        //   MONITOR LIMIT ORDER (CHỜ KHỚP)
        // ============================================================

        public async Task MonitorLimitOrderAsync(TradeSignal signal)
        {
            string symbol = signal.Coin;
            bool isLong = signal.Type == SignalType.Long;

            // Nếu đang monitor POSITION hoặc không thể start LIMIT (đã có thread khác)
            if (IsMonitoringPosition(symbol) || !TryStartMonitoringLimit(symbol))
            {
                await _notify.SendAsync($"[{symbol}] LIMIT: đã monitor → bỏ qua.");
                return;
            }

            await _notify.SendAsync($"[{symbol}] Monitor LIMIT started...");

            // Bắt đầu đếm thời gian cho limit
            var startTime = DateTime.UtcNow;

            try
            {
                while (true)
                {
                    await Task.Delay(MonitorIntervalMs);

                    // === TIMEOUT CHECK: nếu quá 20 phút chưa khớp thì cancel LIMIT ===
                    var elapsed = DateTime.UtcNow - startTime;
                    if (elapsed > LimitTimeout)
                    {
                        await _notify.SendAsync(
                            $"[{symbol}] LIMIT quá {LimitTimeout.TotalMinutes} phút chưa khớp → cancel tất cả orders và stop LIMIT monitor.");

                        await _exchange.CancelAllOpenOrdersAsync(symbol);
                        return;
                    }

                    var openOrders = await _exchange.GetOpenOrdersAsync(symbol);
                    var pos = await _exchange.GetPositionAsync(symbol);

                    bool hasPosition = pos.PositionAmt != 0;
                    bool hasOpenOrder = openOrders.Any();

                    // LIMIT ĐÃ KHỚP → CHUYỂN POSITION
                    if (hasPosition)
                    {
                        await _notify.SendAsync($"[{symbol}] LIMIT filled → chuyển sang monitor POSITION");

                        // Clear LIMIT, start POSITION (có guard race bên trong)
                        ClearMonitoringLimit(symbol);
                        _ = MonitorPositionAsync(signal);
                        return;
                    }

                    // LIMIT không còn order → hủy monitor
                    if (!hasOpenOrder)
                    {
                        await _notify.SendAsync($"[{symbol}] LIMIT không còn order → stop LIMIT monitor.");
                        return;
                    }

                    // CHECK SETUP INVALID
                    var candles = await _exchange.GetRecentCandlesAsync(symbol, _botConfig.Intervals[0].FrameTime, 80);
                    if (candles.Count == 0) continue;

                    var last = candles[^1];
                    decimal ema34 = ComputeEmaLast(candles, 34);
                    decimal ema89 = ComputeEmaLast(candles, 89);
                    decimal ema200 = ComputeEmaLast(candles, 200);

                    decimal entry = signal.EntryPrice ?? last.Close;
                    decimal boundary = isLong
                        ? GetDynamicBoundaryForLong(entry, ema34, ema89, ema200)
                        : GetDynamicBoundaryForShort(entry, ema34, ema89, ema200);

                    bool broken = false;

                    if (isLong)
                    {
                        if (last.Close < signal.StopLoss) broken = true;
                        if (boundary > 0 && last.Close < boundary * (1 - EmaBreakTolerance))
                            broken = true;
                    }
                    else
                    {
                        if (last.Close > signal.StopLoss) broken = true;
                        if (boundary > 0 && last.Close > boundary * (1 + EmaBreakTolerance))
                            broken = true;
                    }

                    if (broken)
                    {
                        await _notify.SendAsync($"[{symbol}] LIMIT setup broke → cancel orders...");
                        await _exchange.CancelAllOpenOrdersAsync(symbol);
                        return;
                    }
                }
            }
            finally
            {
                // đảm bảo luôn clear flag LIMIT
                ClearMonitoringLimit(symbol);
            }
        }

        // ============================================================
        //         MONITOR POSITION (AUTO-TP, TRAILING, EARLY EXIT)
        // ============================================================

        public async Task MonitorPositionAsync(TradeSignal signal)
        {
            string symbol = signal.Coin;
            bool isLongSignal = signal.Type == SignalType.Long;

            // Guard chống race: chỉ 1 thread được monitor POSITION cho mỗi symbol
            if (!TryStartMonitoringPosition(symbol))
            {
                await _notify.SendAsync($"[{symbol}] POSITION: đã monitor → bỏ qua.");
                return;
            }

            // Nếu còn LIMIT monitor cũ thì clear
            ClearMonitoringLimit(symbol);

            decimal entry = signal.EntryPrice ?? 0;
            decimal sl = signal.StopLoss ?? 0;
            decimal tp = signal.TakeProfit ?? 0;

            bool hasEntry = entry > 0;
            bool hasSL = sl > 0;
            bool hasTP = tp > 0;

            if (!hasEntry || !hasSL || !hasTP)
            {
                await _notify.SendAsync(
                    $"[{symbol}] POSITION: thiếu Entry/SL/TP. entry={entry}, sl={sl}, tp={tp}");
            }

            decimal risk = 0;
            bool useRR = false;

            if (hasEntry && hasSL)
            {
                risk = isLongSignal ? entry - sl : sl - entry;
                if (risk > 0) useRR = true;
            }

            // Cờ đánh dấu đã chắc chắn có TP trên sàn (để tránh loop AUTO-TP)
            bool tpInitialized = false;

            await _notify.SendAsync($"[{symbol}] Monitor POSITION started...");

            try
            {
                while (true)
                {
                    await Task.Delay(MonitorIntervalMs);

                    var pos = await _exchange.GetPositionAsync(symbol);
                    decimal qty = pos.PositionAmt;

                    // POSITION CLOSED
                    if (qty == 0)
                    {
                        await _notify.SendAsync($"[{symbol}] Position closed → stop monitor.");
                        return;
                    }

                    bool isLongPosition = qty > 0;
                    decimal price = pos.MarkPrice;

                    // ===================== AUTO-TP (1 lần, dùng DetectManualSlTpAsync) =====================
                    if (hasTP && !tpInitialized)
                    {
                        // Kiểm tra trên sàn hiện tại đã có TP chưa (normal + algo)
                        var (_, tpOnExchange) = await DetectManualSlTpAsync(symbol, isLongPosition, entry);
                        if (tpOnExchange.HasValue)
                        {
                            // Đã thấy TP trên sàn → không cần AUTO-TP nữa
                            tpInitialized = true;
                        }
                        else
                        {
                            decimal absQty = Math.Abs(qty);
                            string posSide = isLongPosition ? "LONG" : "SHORT";
                            decimal tpDisplay = Math.Round(tp, 6);

                            await _notify.SendAsync($"[{symbol}] AUTO-TP → đặt TP mới {tpDisplay}");

                            var ok = await _exchange.PlaceTakeProfitAsync(symbol, posSide, absQty, tp);
                            if (!ok)
                            {
                                await _notify.SendAsync($"[{symbol}] AUTO-TP FAILED → tp={tpDisplay}, qty={absQty}");
                            }
                            else
                            {
                                tpInitialized = true;
                            }
                        }
                    }

                    // ===================== SL HIT (chỉ khi có SL) =====================
                    if (hasSL)
                    {
                        if ((isLongPosition && price <= sl) || (!isLongPosition && price >= sl))
                        {
                            await _notify.SendAsync($"[{symbol}] SL HIT → stop monitor.");
                            return;
                        }
                    }

                    // ===================== TP HIT (theo giá trong signal) ===========
                    if (hasTP)
                    {
                        if ((isLongPosition && price >= tp) || (!isLongPosition && price <= tp))
                        {
                            await _notify.SendAsync($"[{symbol}] TP HIT (theo giá) → stop monitor.");
                            return;
                        }
                    }

                    // ===================== RR, EARLY EXIT, HARD REVERSE ==================
                    if (useRR)
                    {
                        decimal rr = isLongPosition ? (price - entry) / risk : (entry - price) / risk;

                        var candles = await _exchange.GetRecentCandlesAsync(
                            symbol, _botConfig.Intervals[0].FrameTime, 40);
                        if (candles.Count >= 3)
                        {
                            var (reverse, hardReverse) = CheckMomentumReversal(candles, isLongPosition, entry);

                            // EARLY EXIT
                            if (rr >= EarlyExitRR && reverse)
                            {
                                await _notify.SendAsync(
                                    $"[{symbol}] EARLY EXIT rr={rr:F2} → đóng lệnh.");
                                await _exchange.ClosePositionAsync(symbol, qty);
                                return;
                            }

                            // HARD REVERSE
                            if (hardReverse && rr >= -HardReverseRR)
                            {
                                await _notify.SendAsync(
                                    $"[{symbol}] HARD REVERSE rr={rr:F2} → đóng lệnh.");
                                await _exchange.ClosePositionAsync(symbol, qty);
                                return;
                            }
                        }

                        // ===================== TRAILING SL (nếu có SL) ==================
                        if (hasSL)
                        {
                            decimal newSL = sl;

                            if (rr >= 1m)
                                newSL = isLongPosition ? entry + risk * 0.5m : entry - risk * 0.5m;

                            if (rr >= 1.5m)
                                newSL = isLongPosition ? entry + risk * 1m : entry - risk * 1m;

                            if (newSL != sl)
                            {
                                sl = newSL;
                                // Khi trailing SL phải đảm bảo vẫn giữ TP (nếu có)
                                await UpdateStopLossAsync(symbol, newSL, isLongPosition, hasTP, tp);
                            }
                        }
                    }
                }
            }
            finally
            {
                // đảm bảo luôn clear flag POSITION
                ClearMonitoringPosition(symbol);
            }
        }

        // ============================================================
        //          MANUAL ATTACH POSITION (AUTO TÍNH TP NẾU MẤT)
        // ============================================================

        public async Task AttachManualPositionAsync(PositionInfo pos)
        {
            if (pos == null || pos.PositionAmt == 0)
            {
                return;
            }

            // Nếu đã có POSITION monitor rồi thì không attach nữa
            if (IsMonitoringPosition(pos.Symbol))
            {
                return;
            }

            // Có thể đang monitor LIMIT cũ → clear cho chắc
            ClearMonitoringLimit(pos.Symbol);

            decimal qty = pos.PositionAmt;
            bool isLong = qty > 0;

            decimal entry = pos.EntryPrice;

            // 1) Lấy SL/TP hiện có trên sàn (normal + algo)
            var (sl, tp) = await DetectManualSlTpAsync(pos.Symbol, isLong, entry);

            // 2) Nếu KHÔNG tìm thấy TP nhưng có SL + entry → tự tính TP theo RR mặc định
            if (!tp.HasValue && sl.HasValue && entry > 0)
            {
                decimal risk = isLong ? entry - sl.Value : sl.Value - entry;

                if (risk > 0)
                {
                    const decimal defaultRR = 2m; // RR mặc định

                    var autoTp = isLong
                        ? entry + risk * defaultRR
                        : entry - risk * defaultRR;

                    tp = autoTp;

                    await _notify.SendAsync(
                        $"[{pos.Symbol}] MANUAL ATTACH: không tìm thấy TP trên sàn → auto TP={autoTp} theo RR={defaultRR}");
                }
            }

            await _notify.SendAsync(
                $"[{pos.Symbol}] MANUAL ATTACH → side={(isLong ? "LONG" : "SHORT")} entry={entry}, SL={sl}, TP={tp}"
            );

            var signal = new TradeSignal
            {
                Coin = pos.Symbol,
                Type = isLong ? SignalType.Long : SignalType.Short,
                EntryPrice = entry,
                StopLoss = sl,
                TakeProfit = tp,
                Time = DateTime.UtcNow,
                Reason = "MANUAL ATTACH"
            };

            // MonitorPositionAsync có guard race bên trong
            _ = MonitorPositionAsync(signal);
        }

        public async Task ClearMonitoringTrigger(string symbol)
        {
            if (IsMonitoringLimit(symbol) || IsMonitoringPosition(symbol))
            {
                ClearAllMonitoring(symbol);
                await _notify.SendAsync($"[{symbol}] đã clear monitoring.");
            }
        }

        // ============================================================
        //          DETECT TP/SL từ openOrders + openAlgoOrders
        // ============================================================

        private async Task<(decimal? sl, decimal? tp)> DetectManualSlTpAsync(
            string symbol, bool isLong, decimal entryPrice)
        {
            var normalOrders = await _exchange.GetOpenOrdersAsync(symbol);
            var algoOrders = await _exchange.GetOpenAlgoOrdersAsync(symbol);
            var orders = normalOrders.Concat(algoOrders).ToList();

            if (orders == null || orders.Count == 0)
                return (null, null);

            decimal? sl = null;
            decimal? tp = null;

            foreach (var o in orders)
            {
                decimal trigger = o.StopPrice > 0 ? o.StopPrice : o.Price;
                if (trigger <= 0) continue;

                bool isStopType = o.Type.Contains("STOP", StringComparison.OrdinalIgnoreCase);
                bool isTpType =
                    o.Type.Contains("TAKE", StringComparison.OrdinalIgnoreCase) ||
                    (o.Type.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) &&
                     !o.Type.Contains("STOP", StringComparison.OrdinalIgnoreCase));

                if (isLong)
                {
                    // LONG TP: SELL LIMIT/TAKE phía trên entry (nếu có entry)
                    if (o.Side == "SELL" && isTpType &&
                        (entryPrice <= 0 || trigger >= entryPrice))
                    {
                        tp ??= trigger;
                        if (trigger < tp) tp = trigger;   // TP gần nhất
                    }

                    // LONG SL: mọi SELL STOP là SL (kể cả trailing > entry)
                    if (o.Side == "SELL" && isStopType)
                    {
                        sl ??= trigger;
                        if (trigger > sl) sl = trigger;   // SL gần giá nhất: cao hơn
                    }
                }
                else
                {
                    // SHORT TP: BUY LIMIT/TAKE phía dưới entry (nếu có entry)
                    if (o.Side == "BUY" && isTpType &&
                        (entryPrice <= 0 || trigger <= entryPrice))
                    {
                        tp ??= trigger;
                        if (trigger > tp) tp = trigger;   // với short, TP gần hơn là giá cao hơn
                    }

                    // SHORT SL: mọi BUY STOP là SL (kể cả trailing < entry)
                    if (o.Side == "BUY" && isStopType)
                    {
                        sl ??= trigger;
                        if (trigger < sl) sl = trigger;   // SL gần giá: thấp hơn
                    }
                }
            }

            return (sl, tp);
        }

        // ============================================================
        //                       EMA HELPERS
        // ============================================================
        private static decimal ComputeEmaLast(IReadOnlyList<Candle> candles, int period)
        {
            if (candles.Count == 0) return 0;

            var closes = candles
                .Skip(Math.Max(0, candles.Count - period * 3))
                .Select(c => c.Close)
                .ToArray();

            decimal k = 2m / (period + 1);
            decimal ema = closes[0];

            for (int i = 1; i < closes.Length; i++)
                ema = closes[i] * k + ema * (1 - k);

            return ema;
        }

        private static decimal GetDynamicBoundaryForShort(decimal entry, decimal ema34, decimal ema89, decimal ema200)
        {
            var emas = new List<decimal> { ema34, ema89, ema200 }.Where(e => e > 0).ToList();

            var candidate = emas.Where(e => e >= entry).OrderBy(e => e).FirstOrDefault();
            return candidate == 0 ? 0 : candidate;
        }

        private static decimal GetDynamicBoundaryForLong(decimal entry, decimal ema34, decimal ema89, decimal ema200)
        {
            var emas = new List<decimal> { ema34, ema89, ema200 }.Where(e => e > 0).ToList();

            var candidate = emas.Where(e => e <= entry).OrderByDescending(e => e).FirstOrDefault();
            return candidate == 0 ? 0 : candidate;
        }

        // ============================================================
        //                    MOMENTUM REVERSAL
        // ============================================================

        private (bool reverse, bool hardReverse) CheckMomentumReversal(
            IReadOnlyList<Candle> candles15m, bool isLong, decimal entryPrice)
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

            bool reverse = false;
            bool hard = false;

            if (isLong)
            {
                reverse = c0.Close < c0.Open && c0.Volume >= c1.Volume * 0.8m;

                if (boundary > 0 && c0.Close < boundary * (1 - EmaBreakTolerance))
                    hard = true;
            }
            else
            {
                reverse = c0.Close > c0.Open && c0.Volume >= c1.Volume * 0.8m;

                if (boundary > 0 && c0.Close > boundary * (1 + EmaBreakTolerance))
                    hard = true;
            }

            return (reverse, hard);
        }

        // ============================================================
        //                     UPDATE STOPLOSS (TRAILING)
        // ============================================================

        private async Task UpdateStopLossAsync(string symbol, decimal newSL, bool isLong, bool hasTp, decimal? expectedTp)
        {
            await _notify.SendAsync($"[{symbol}] Trailing SL update → {newSL}");

            // Hủy tất cả SL hiện tại (chỉ SL, không TP) – implement chi tiết trong BinanceFuturesClientService
            await _exchange.CancelStopLossOrdersAsync(symbol);

            var pos = await _exchange.GetPositionAsync(symbol);
            decimal qty = Math.Abs(pos.PositionAmt);
            if (qty <= 0)
            {
                await _notify.SendAsync($"[{symbol}] Không tìm thấy position khi update SL.");
                ClearMonitoringPosition(symbol);
                return;
            }

            string side = isLong ? "SELL" : "BUY";
            string posSide = isLong ? "LONG" : "SHORT";

            // Đặt SL mới
            await _exchange.PlaceStopOnlyAsync(symbol, side, posSide, qty, newSL);

            // Đảm bảo không bị mất TP sau khi trailing (nếu ban đầu có TP)
            if (hasTp && expectedTp.HasValue)
            {
                var (_, tpOnExchange) = await DetectManualSlTpAsync(symbol, isLong, pos.EntryPrice);
                if (!tpOnExchange.HasValue)
                {
                    decimal tpVal = expectedTp.Value;
                    decimal tpDisplay = Math.Round(tpVal, 6);

                    await _notify.SendAsync($"[{symbol}] Trailing giữ TP → đặt lại TP {tpDisplay}");

                    var ok = await _exchange.PlaceTakeProfitAsync(symbol, posSide, qty, tpVal);
                    if (!ok)
                    {
                        await _notify.SendAsync($"[{symbol}] Trailing giữ TP FAILED → tp={tpDisplay}");
                    }
                }
            }
        }
    }
}
