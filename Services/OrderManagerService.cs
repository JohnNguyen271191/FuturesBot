using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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

            var startTime = DateTime.UtcNow;

            try
            {
                while (true)
                {
                    await Task.Delay(MonitorIntervalMs);

                    // === TIMEOUT CHECK ===
                    var elapsed = DateTime.UtcNow - startTime;
                    if (elapsed > LimitTimeout)
                    {
                        await _notify.SendAsync(
                            $"[{symbol}] LIMIT quá {LimitTimeout.TotalMinutes} phút chưa khớp → cancel open orders và stop LIMIT monitor.");

                        // NOTE: CancelAll có thể mạnh tay, nhưng ở pha LIMIT thường chưa có POSITION.
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

                        ClearMonitoringLimit(symbol);
                        _ = MonitorPositionAsync(signal);
                        return;
                    }

                    // LIMIT không còn order → dừng monitor
                    if (!hasOpenOrder)
                    {
                        await _notify.SendAsync($"[{symbol}] LIMIT không còn order → stop LIMIT monitor.");
                        return;
                    }

                    // CHECK SETUP INVALID (dùng nến đã đóng, tránh false break)
                    var candles = await _exchange.GetRecentCandlesAsync(symbol, _botConfig.Intervals[0].FrameTime, 80);
                    if (candles == null || candles.Count < 3) continue;

                    var lastClosed = candles[^2]; // FIX: dùng nến đã đóng

                    decimal ema34 = ComputeEmaLast(candles, 34);
                    decimal ema89 = ComputeEmaLast(candles, 89);
                    decimal ema200 = ComputeEmaLast(candles, 200);

                    decimal entry = signal.EntryPrice ?? lastClosed.Close;
                    decimal boundary = isLong
                        ? GetDynamicBoundaryForLong(entry, ema34, ema89, ema200)
                        : GetDynamicBoundaryForShort(entry, ema34, ema89, ema200);

                    bool broken = false;

                    // Guard SL null/0
                    var slVal = signal.StopLoss ?? 0m;
                    bool hasSl = slVal > 0m;

                    if (isLong)
                    {
                        if (hasSl && lastClosed.Close < slVal) broken = true;
                        if (boundary > 0 && lastClosed.Close < boundary * (1 - EmaBreakTolerance))
                            broken = true;
                    }
                    else
                    {
                        if (hasSl && lastClosed.Close > slVal) broken = true;
                        if (boundary > 0 && lastClosed.Close > boundary * (1 + EmaBreakTolerance))
                            broken = true;
                    }

                    if (broken)
                    {
                        await _notify.SendAsync($"[{symbol}] LIMIT setup broke → cancel open orders...");
                        await _exchange.CancelAllOpenOrdersAsync(symbol);
                        return;
                    }
                }
            }
            finally
            {
                ClearMonitoringLimit(symbol);
            }
        }

        // ============================================================
        //         MONITOR POSITION (AUTO-TP, TRAILING, EARLY EXIT)
        // ============================================================

        public async Task MonitorPositionAsync(TradeSignal signal)
        {
            string symbol = signal.Coin;

            if (!TryStartMonitoringPosition(symbol))
            {
                await _notify.SendAsync($"[{symbol}] POSITION: đã monitor → bỏ qua.");
                return;
            }

            ClearMonitoringLimit(symbol);

            // ===== INIT từ signal (manual attach có thể thiếu SL/TP) =====
            decimal entry = signal.EntryPrice ?? 0m;
            decimal sl = signal.StopLoss ?? 0m;
            decimal tp = signal.TakeProfit ?? 0m;

            bool missingNotified = false;
            bool tpInitialized = false;
            bool autoTpPlaced = false;

            // RR mặc định cho case manual thiếu TP (mày muốn đổi thì chỉnh số này)
            const decimal DefaultManualRR = 2m;

            await _notify.SendAsync($"[{symbol}] Monitor POSITION started...");

            try
            {
                while (true)
                {
                    await Task.Delay(MonitorIntervalMs);

                    var pos = await _exchange.GetPositionAsync(symbol);
                    decimal qty = pos.PositionAmt;

                    if (qty == 0)
                    {
                        await _notify.SendAsync($"[{symbol}] Position closed → stop monitor.");
                        return;
                    }

                    bool isLongPosition = qty > 0;
                    decimal price = pos.MarkPrice;
                    decimal absQty = Math.Abs(qty);
                    string posSide = isLongPosition ? "LONG" : "SHORT";

                    // ============================================================
                    // 1) SYNC ENTRY/SL/TP TỪ SÀN (FIX: manual đặt SL/TP sau)
                    // ============================================================

                    if (entry <= 0m && pos.EntryPrice > 0m)
                        entry = pos.EntryPrice;

                    var (slOnEx, tpOnEx) = await DetectManualSlTpAsync(symbol, isLongPosition, entry);

                    // manual đặt SL sau
                    if (sl <= 0m && slOnEx.HasValue && slOnEx.Value > 0m)
                    {
                        sl = slOnEx.Value;
                        await _notify.SendAsync($"[{symbol}] Sync SL từ sàn → SL={Math.Round(sl, 6)}");
                    }

                    // manual đặt TP sau
                    if (tp <= 0m && tpOnEx.HasValue && tpOnEx.Value > 0m)
                    {
                        tp = tpOnEx.Value;
                        tpInitialized = true;
                        await _notify.SendAsync($"[{symbol}] Sync TP từ sàn → TP={Math.Round(tp, 6)}");
                    }

                    bool hasEntry = entry > 0m;
                    bool hasSL = sl > 0m;
                    bool hasTP = tp > 0m;

                    if ((!hasEntry || !hasSL || !hasTP) && !missingNotified)
                    {
                        await _notify.SendAsync(
                            $"[{symbol}] POSITION: thiếu Entry/SL/TP. entry={entry}, sl={sl}, tp={tp} (sẽ auto-sync khi mày đặt tay)");
                        missingNotified = true;
                    }

                    // ============================================================
                    // 2) MANUAL: có SL + entry nhưng thiếu TP → AUTO tính & đặt TP
                    // ============================================================
                    if (hasEntry && hasSL && !hasTP && !autoTpPlaced)
                    {
                        decimal riskManual = isLongPosition ? (entry - sl) : (sl - entry);
                        if (riskManual > 0m)
                        {
                            decimal autoTp = isLongPosition
                                ? entry + riskManual * DefaultManualRR
                                : entry - riskManual * DefaultManualRR;

                            tp = autoTp;
                            hasTP = true;
                            tpInitialized = false;

                            await _notify.SendAsync(
                                $"[{symbol}] Manual có SL nhưng thiếu TP → AUTO-TP={Math.Round(autoTp, 6)} theo RR={DefaultManualRR}");

                            var ok = await _exchange.PlaceTakeProfitAsync(symbol, posSide, absQty, autoTp);
                            if (!ok)
                            {
                                await _notify.SendAsync($"[{symbol}] AUTO-TP FAILED → tp={Math.Round(autoTp, 6)}, qty={absQty}");
                            }
                            else
                            {
                                autoTpPlaced = true;
                                tpInitialized = true;
                            }
                        }
                    }

                    // ============================================================
                    // 3) AUTO-TP (1 lần, check TP trên sàn trước) – giữ logic cũ
                    // ============================================================
                    if (hasTP && !tpInitialized)
                    {
                        var (_, tpCheck) = await DetectManualSlTpAsync(symbol, isLongPosition, entry);

                        if (tpCheck.HasValue)
                        {
                            tpInitialized = true;
                        }
                        else
                        {
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

                    // ============================================================
                    // 4) SL HIT (nếu có SL)
                    // ============================================================
                    if (hasSL)
                    {
                        if ((isLongPosition && price <= sl) || (!isLongPosition && price >= sl))
                        {
                            await _notify.SendAsync($"[{symbol}] SL HIT (theo giá) → đóng lệnh để chắc chắn.");
                            await _exchange.ClosePositionAsync(symbol, qty);
                            return;
                        }
                    }

                    // ============================================================
                    // 5) TP HIT (theo giá trong local tp)
                    // ============================================================
                    if (hasTP)
                    {
                        bool hitTp = (isLongPosition && price >= tp) || (!isLongPosition && price <= tp);
                        if (hitTp)
                        {
                            // nếu chạm TP mà trên sàn không có TP thì đóng position
                            var (_, tpOnExchange2) = await DetectManualSlTpAsync(symbol, isLongPosition, entry);
                            if (!tpOnExchange2.HasValue)
                            {
                                await _notify.SendAsync($"[{symbol}] Giá chạm TP nhưng không thấy TP trên sàn → đóng position.");
                                await _exchange.ClosePositionAsync(symbol, qty);
                            }
                            else
                            {
                                await _notify.SendAsync($"[{symbol}] TP HIT (theo giá) → stop monitor.");
                            }
                            return;
                        }
                    }

                    // ============================================================
                    // 6) RR, EARLY EXIT, HARD REVERSE + TRAILING
                    // ============================================================
                    decimal risk = 0m;
                    bool useRR = false;

                    if (hasEntry && hasSL)
                    {
                        risk = isLongPosition ? entry - sl : sl - entry;
                        if (risk > 0m) useRR = true;
                    }

                    if (useRR)
                    {
                        decimal rr = isLongPosition ? (price - entry) / risk : (entry - price) / risk;

                        var candles = await _exchange.GetRecentCandlesAsync(
                            symbol, _botConfig.Intervals[0].FrameTime, 60);

                        if (candles != null && candles.Count >= 5)
                        {
                            var (reverse, hardReverse) = CheckMomentumReversal(candles, isLongPosition, entry);

                            if (rr >= EarlyExitRR && reverse)
                            {
                                await _notify.SendAsync($"[{symbol}] EARLY EXIT rr={rr:F2} → đóng lệnh.");
                                await _exchange.ClosePositionAsync(symbol, qty);
                                return;
                            }

                            if (hardReverse && rr >= -HardReverseRR)
                            {
                                await _notify.SendAsync($"[{symbol}] HARD REVERSE rr={rr:F2} → đóng lệnh.");
                                await _exchange.ClosePositionAsync(symbol, qty);
                                return;
                            }
                        }

                        // ===================== TRAILING SL =====================
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
                                await UpdateStopLossAsync(symbol, newSL, isLongPosition, hasTP, tp);
                            }
                        }
                    }
                }
            }
            finally
            {
                ClearMonitoringPosition(symbol);
            }
        }

        // ============================================================
        //          MANUAL ATTACH POSITION (AUTO TÍNH TP NẾU MẤT)
        // ============================================================

        public async Task AttachManualPositionAsync(PositionInfo pos)
        {
            if (pos == null || pos.PositionAmt == 0)
                return;

            if (IsMonitoringPosition(pos.Symbol))
                return;

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
                    const decimal defaultRR = 2m;

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
        //          DETECT TP/SL từ openOrders + openAlgoOrders (FIXED)
        // ============================================================

        private async Task<(decimal? sl, decimal? tp)> DetectManualSlTpAsync(
            string symbol, bool isLong, decimal entryPrice)
        {
            // NOTE: vì OpenOrderInfo không có ReduceOnly/ClosePosition,
            // nên để tránh nhầm ENTRY LIMIT thành TP, ta chỉ nhận:
            // - TP: TAKE_PROFIT / TAKE_PROFIT_MARKET (contains "TAKE")
            // - SL: STOP / STOP_MARKET (contains "STOP" và không phải TAKE)
            var normalOrders = await _exchange.GetOpenOrdersAsync(symbol);
            var algoOrders = await _exchange.GetOpenAlgoOrdersAsync(symbol);

            var orders = new List<OpenOrderInfo>();
            if (normalOrders != null) orders.AddRange(normalOrders);
            if (algoOrders != null) orders.AddRange(algoOrders);

            if (orders.Count == 0)
                return (null, null);

            decimal? sl = null;
            decimal? tp = null;

            foreach (var o in orders)
            {
                if (o == null) continue;

                string type = o.Type ?? string.Empty;
                string side = o.Side ?? string.Empty;

                decimal trigger = o.StopPrice > 0 ? o.StopPrice : o.Price;
                if (trigger <= 0) continue;

                bool isStopType = type.Contains("STOP", StringComparison.OrdinalIgnoreCase)
                                  && !type.Contains("TAKE", StringComparison.OrdinalIgnoreCase);

                bool isTakeType = type.Contains("TAKE", StringComparison.OrdinalIgnoreCase);

                if (isLong)
                {
                    // LONG: TP thường là SELL TAKE_PROFIT phía trên entry
                    if (side.Equals("SELL", StringComparison.OrdinalIgnoreCase) && isTakeType &&
                        (entryPrice <= 0 || trigger >= entryPrice))
                    {
                        if (!tp.HasValue) tp = trigger;
                        else tp = Math.Min(tp.Value, trigger); // TP gần nhất (thấp hơn) trên entry
                    }

                    // LONG: SL là SELL STOP phía dưới entry (hoặc dưới giá)
                    if (side.Equals("SELL", StringComparison.OrdinalIgnoreCase) && isStopType)
                    {
                        if (entryPrice > 0 && trigger > entryPrice) continue;

                        if (!sl.HasValue) sl = trigger;
                        else sl = Math.Max(sl.Value, trigger); // SL gần giá nhất (cao hơn) nhưng vẫn dưới entry
                    }
                }
                else
                {
                    // SHORT: TP là BUY TAKE_PROFIT phía dưới entry
                    if (side.Equals("BUY", StringComparison.OrdinalIgnoreCase) && isTakeType &&
                        (entryPrice <= 0 || trigger <= entryPrice))
                    {
                        if (!tp.HasValue) tp = trigger;
                        else tp = Math.Max(tp.Value, trigger); // TP gần nhất cho short là giá cao hơn (nhưng vẫn <= entry)
                    }

                    // SHORT: SL là BUY STOP phía trên entry
                    if (side.Equals("BUY", StringComparison.OrdinalIgnoreCase) && isStopType)
                    {
                        if (entryPrice > 0 && trigger < entryPrice) continue;

                        if (!sl.HasValue) sl = trigger;
                        else sl = Math.Min(sl.Value, trigger); // SL gần giá nhất (thấp hơn) nhưng vẫn trên entry
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
            if (candles == null || candles.Count == 0) return 0;

            var closes = candles
                .Skip(Math.Max(0, candles.Count - period * 3))
                .Select(c => c.Close)
                .ToArray();

            if (closes.Length == 0) return 0;

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
        //                    MOMENTUM REVERSAL (FIX: dùng nến đóng)
        // ============================================================

        private (bool reverse, bool hardReverse) CheckMomentumReversal(
            IReadOnlyList<Candle> candles15m, bool isLong, decimal entryPrice)
        {
            int i0 = candles15m.Count - 2;
            int i1 = candles15m.Count - 3;
            if (i1 < 0) return (false, false);

            var c0 = candles15m[i0];
            var c1 = candles15m[i1];

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

            // Hủy tất cả SL hiện tại (chỉ SL, không TP)
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
