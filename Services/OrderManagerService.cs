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
    /// <summary>
    /// OrderManagerService - WINRATE + FLEX EXIT (giống trade tay) + MODE AWARE
    /// </summary>
    public class OrderManagerService
    {
        private readonly IExchangeClientService _exchange;
        private readonly SlackNotifierService _notify;
        private readonly BotConfig _botConfig;

        // =============== Polling / Throttle =======================
        private const int MonitorIntervalMs = 10000;  // giảm spam -1003
        private const int SlTpCheckEverySec = 30;
        private const int CandleFetchEverySec = 20;

        private static readonly TimeSpan DefaultLimitTimeout = TimeSpan.FromMinutes(20);

        // Grace sau fill (sàn chưa sync kịp algoOrders)
        private const int SlTpGraceAfterFillSec = 8;

        // =============== DEFAULT (fallback) ========================
        private const decimal DefaultSafetyTpRR = 2.0m;

        // =============== EMA default ===============================
        private const decimal DefaultEmaBreakTolerance = 0.001m; // 0.1%

        // =============== Reversal detection (default) ==============
        private const decimal ImpulseBodyToRangeMin = 0.65m;    // nến mạnh
        private const decimal ImpulseVolVsPrevMin = 1.20m;      // vol spike

        // =============== Debug throttle ===========================
        private readonly ConcurrentDictionary<string, DateTime> _lastMissingLogUtc = new();

        // ============================================================
        // TRACK SYMBOL ĐANG ĐƯỢC GIÁM SÁT
        // ============================================================

        private readonly ConcurrentDictionary<string, bool> _monitoringLimit = new();
        private readonly ConcurrentDictionary<string, bool> _monitoringPosition = new();

        private bool IsMonitoringLimit(string symbol) => _monitoringLimit.ContainsKey(symbol);
        private bool IsMonitoringPosition(string symbol) => _monitoringPosition.ContainsKey(symbol);

        private bool TryStartMonitoringLimit(string symbol) => _monitoringLimit.TryAdd(symbol, true);
        private bool TryStartMonitoringPosition(string symbol) => _monitoringPosition.TryAdd(symbol, true);

        private void ClearMonitoringLimit(string symbol) => _monitoringLimit.TryRemove(symbol, out _);
        private void ClearMonitoringPosition(string symbol) => _monitoringPosition.TryRemove(symbol, out _);

        private void ClearAllMonitoring(string symbol)
        {
            ClearMonitoringLimit(symbol);
            ClearMonitoringPosition(symbol);
        }

        // ============================================================
        // MODE PROFILE
        // ============================================================

        private sealed class ModeProfile
        {
            public TradeMode Mode { get; init; }

            // Protect / Quick Take / Danger / TimeStop
            public decimal ProtectAtRR { get; init; }
            public decimal BreakEvenBufferR { get; init; }
            public decimal QuickTakeMinRR { get; init; }
            public decimal QuickTakeGoodRR { get; init; }
            public decimal DangerCutIfRRBelow { get; init; }
            public int TimeStopBars { get; init; }
            public decimal TimeStopMinRR { get; init; }

            // Safety TP
            public decimal SafetyTpRR { get; init; }

            // EMA / boundary
            public decimal EmaBreakTolerance { get; init; }

            // Limit monitoring timeout
            public TimeSpan LimitTimeout { get; init; }

            public string Tag => Mode.ToString();

            public static ModeProfile For(TradeMode mode)
            {
                return mode switch
                {
                    // ====== SCALP ======
                    TradeMode.Scalp => new ModeProfile
                    {
                        Mode = mode,
                        ProtectAtRR = 0.22m,
                        BreakEvenBufferR = 0.06m,
                        QuickTakeMinRR = 0.35m,
                        QuickTakeGoodRR = 0.60m,
                        DangerCutIfRRBelow = -0.25m,
                        TimeStopBars = 4,
                        TimeStopMinRR = 0.18m,
                        SafetyTpRR = 1.30m,
                        EmaBreakTolerance = 0.0012m,
                        LimitTimeout = TimeSpan.FromMinutes(10),
                    },

                    // ====== MODE2 CONTINUATION ======
                    TradeMode.Mode2_Continuation => new ModeProfile
                    {
                        Mode = mode,
                        ProtectAtRR = 0.25m,
                        BreakEvenBufferR = 0.05m,
                        QuickTakeMinRR = 0.40m,
                        QuickTakeGoodRR = 0.70m,
                        DangerCutIfRRBelow = -0.30m,
                        TimeStopBars = 4,
                        TimeStopMinRR = 0.20m,
                        SafetyTpRR = 1.60m,
                        EmaBreakTolerance = 0.0010m,
                        LimitTimeout = TimeSpan.FromMinutes(8),
                    },

                    // ====== TREND (default) ======
                    _ => new ModeProfile
                    {
                        Mode = TradeMode.Trend,
                        ProtectAtRR = 0.30m,
                        BreakEvenBufferR = 0.05m,
                        QuickTakeMinRR = 0.45m,
                        QuickTakeGoodRR = 0.75m,
                        DangerCutIfRRBelow = -0.35m,
                        TimeStopBars = 6,
                        TimeStopMinRR = 0.20m,
                        SafetyTpRR = DefaultSafetyTpRR,
                        EmaBreakTolerance = DefaultEmaBreakTolerance,
                        LimitTimeout = DefaultLimitTimeout,
                    }
                };
            }
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
        //   MONITOR LIMIT ORDER (CHỜ KHỚP) - MODE AWARE
        // ============================================================

        public async Task MonitorLimitOrderAsync(TradeSignal signal)
        {
            string symbol = signal.Symbol;
            bool isLong = signal.Type == SignalType.Long;

            var profile = ModeProfile.For(signal.Mode);

            if (IsMonitoringPosition(symbol) || !TryStartMonitoringLimit(symbol))
            {
                await _notify.SendAsync($"[{symbol}] LIMIT: đã monitor → bỏ qua.");
                return;
            }

            await _notify.SendAsync($"[{symbol}] Monitor LIMIT started... mode={profile.Tag}, timeout={profile.LimitTimeout.TotalMinutes:F0}m");

            var startTime = DateTime.UtcNow;

            try
            {
                while (true)
                {
                    await Task.Delay(MonitorIntervalMs);

                    var elapsed = DateTime.UtcNow - startTime;
                    if (elapsed > profile.LimitTimeout) // ✅ FIX: dùng profile.LimitTimeout (instance)
                    {
                        await _notify.SendAsync(
                            $"[{symbol}] LIMIT quá {profile.LimitTimeout.TotalMinutes:F0} phút chưa khớp → cancel open orders và stop LIMIT monitor.");

                        await _exchange.CancelAllOpenOrdersAsync(symbol);
                        return;
                    }

                    var openOrders = await _exchange.GetOpenOrdersAsync(symbol);
                    var pos = await _exchange.GetPositionAsync(symbol);

                    bool hasPosition = pos.PositionAmt != 0;
                    bool hasOpenOrder = openOrders.Any();

                    if (hasPosition)
                    {
                        await _notify.SendAsync($"[{symbol}] LIMIT filled → chuyển sang monitor POSITION (mode={profile.Tag})");

                        ClearMonitoringLimit(symbol);
                        _ = MonitorPositionAsync(signal);
                        return;
                    }

                    if (!hasOpenOrder)
                    {
                        await _notify.SendAsync($"[{symbol}] LIMIT không còn order → stop LIMIT monitor.");
                        return;
                    }

                    var candles = await _exchange.GetRecentCandlesAsync(symbol, _botConfig.Intervals[0].FrameTime, 80);
                    if (candles == null || candles.Count < 3) continue;

                    var lastClosed = candles[^2];

                    decimal ema34 = ComputeEmaLast(candles, 34);
                    decimal ema89 = ComputeEmaLast(candles, 89);
                    decimal ema200 = ComputeEmaLast(candles, 200);

                    decimal entry = signal.EntryPrice ?? lastClosed.Close;

                    decimal boundary = isLong
                        ? GetDynamicBoundaryForLong(entry, ema34, ema89, ema200)
                        : GetDynamicBoundaryForShort(entry, ema34, ema89, ema200);

                    bool broken = false;

                    var slVal = signal.StopLoss ?? 0m;
                    bool hasSl = slVal > 0m;

                    if (isLong)
                    {
                        if (hasSl && lastClosed.Close < slVal) broken = true;
                        if (boundary > 0 && lastClosed.Close < boundary * (1m - profile.EmaBreakTolerance)) broken = true;
                    }
                    else
                    {
                        if (hasSl && lastClosed.Close > slVal) broken = true;
                        if (boundary > 0 && lastClosed.Close > boundary * (1m + profile.EmaBreakTolerance)) broken = true;
                    }

                    if (broken)
                    {
                        await _notify.SendAsync($"[{symbol}] LIMIT setup broke (mode={profile.Tag}) → cancel open orders...");
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
        //         MONITOR POSITION (FLEX EXIT) - MODE AWARE
        // ============================================================

        public async Task MonitorPositionAsync(TradeSignal signal)
        {
            string symbol = signal.Symbol;
            var profile = ModeProfile.For(signal.Mode);

            if (!TryStartMonitoringPosition(symbol))
            {
                await _notify.SendAsync($"[{symbol}] POSITION: đã monitor → bỏ qua.");
                return;
            }

            ClearMonitoringLimit(symbol);

            decimal entry = signal.EntryPrice ?? 0m;
            decimal sl = signal.StopLoss ?? 0m;
            decimal tp = signal.TakeProfit ?? 0m;

            bool missingNotified = false;
            bool autoTpPlaced = false;

            DateTime lastSlTpCheckUtc = DateTime.MinValue;
            DateTime lastCandleFetchUtc = DateTime.MinValue;
            IReadOnlyList<Candle>? cachedCandles = null;

            var positionMonitorStartedUtc = DateTime.UtcNow;

            // TIME-STOP state
            bool timeStopTriggered = false;
            DateTime? timeStopAnchorUtc = null;

            await _notify.SendAsync(
                $"[{symbol}] Monitor POSITION started... mode={profile.Tag} | FLEX EXIT + TIME-STOP {profile.TimeStopBars}xM15");

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
                        await SafeCancelLeftoverProtectiveAsync(symbol);
                        return;
                    }

                    bool isLongPosition = qty > 0;
                    decimal price = pos.MarkPrice;
                    decimal absQty = Math.Abs(qty);
                    string posSide = isLongPosition ? "LONG" : "SHORT";

                    // sync entry theo sàn nếu lệch
                    if (pos.EntryPrice > 0m)
                    {
                        if (entry <= 0m || Math.Abs(entry - pos.EntryPrice) / pos.EntryPrice > 0.0005m)
                            entry = pos.EntryPrice;
                    }

                    bool hasEntry = entry > 0m;
                    bool hasSL = sl > 0m;
                    bool hasTP = tp > 0m;

                    bool inGrace = (DateTime.UtcNow - positionMonitorStartedUtc) < TimeSpan.FromSeconds(SlTpGraceAfterFillSec);

                    // =================== Sync SL/TP từ sàn (throttle) ===================
                    if ((DateTime.UtcNow - lastSlTpCheckUtc) >= TimeSpan.FromSeconds(SlTpCheckEverySec) || lastSlTpCheckUtc == DateTime.MinValue)
                    {
                        var det = await DetectManualSlTpAsync(symbol, isLongPosition, entry, pos);
                        lastSlTpCheckUtc = DateTime.UtcNow;

                        if (sl <= 0m && det.Sl.HasValue && det.Sl.Value > 0m)
                        {
                            sl = det.Sl.Value;
                            hasSL = true;
                            await _notify.SendAsync($"[{symbol}] Sync SL từ sàn → SL={Math.Round(sl, 6)}");
                        }

                        if (tp <= 0m && det.Tp.HasValue && det.Tp.Value > 0m)
                        {
                            tp = det.Tp.Value;
                            hasTP = true;
                            await _notify.SendAsync($"[{symbol}] Sync TP từ sàn → TP={Math.Round(tp, 6)}");
                        }
                    }

                    // notify thiếu thông tin (sau grace)
                    if (!inGrace)
                    {
                        if ((!hasEntry || !hasSL || !hasTP) && !missingNotified)
                        {
                            await _notify.SendAsync(
                                $"[{symbol}] POSITION: thiếu Entry/SL/TP. entry={entry}, sl={sl}, tp={tp} (sẽ auto-sync / auto safety TP) | mode={profile.Tag}");
                            missingNotified = true;
                        }
                    }

                    // =================== Fetch candles (throttle) ===================
                    if ((DateTime.UtcNow - lastCandleFetchUtc) >= TimeSpan.FromSeconds(CandleFetchEverySec))
                    {
                        cachedCandles = await _exchange.GetRecentCandlesAsync(symbol, _botConfig.Intervals[0].FrameTime, 80);
                        lastCandleFetchUtc = DateTime.UtcNow;
                    }

                    // =================== Compute RR / risk ===================
                    decimal risk = 0m;
                    bool canUseRR = false;

                    if (hasEntry && hasSL && IsValidStopLoss(sl, isLongPosition, entry))
                    {
                        risk = isLongPosition ? (entry - sl) : (sl - entry);
                        if (risk > 0m) canUseRR = true;
                    }

                    decimal rr = 0m;
                    if (canUseRR && risk > 0m)
                        rr = isLongPosition ? (price - entry) / risk : (entry - price) / risk;

                    // =================== SAFETY TP (nếu thiếu TP) - MODE AWARE ===================
                    if (hasEntry && hasSL && !hasTP && !autoTpPlaced)
                    {
                        if (risk > 0m)
                        {
                            decimal safetyTp = isLongPosition
                                ? entry + risk * profile.SafetyTpRR
                                : entry - risk * profile.SafetyTpRR;

                            tp = safetyTp;
                            hasTP = true;

                            await _notify.SendAsync($"[{symbol}] đặt SAFETY-TP={Math.Round(safetyTp, 6)} (RR~{profile.SafetyTpRR}) | mode={profile.Tag}");

                            var ok = await _exchange.PlaceTakeProfitAsync(symbol, posSide, absQty, safetyTp);
                            if (!ok)
                                await _notify.SendAsync($"[{symbol}] SAFETY-TP FAILED → tp={Math.Round(safetyTp, 6)}, qty={absQty}");
                            else
                                autoTpPlaced = true;
                        }
                    }

                    // =================== SL hit (chỉ khi hợp lệ + không grace) ===================
                    if (!inGrace && hasEntry && hasSL && IsValidStopLoss(sl, isLongPosition, entry))
                    {
                        if ((isLongPosition && price <= sl) || (!isLongPosition && price >= sl))
                        {
                            var det = await DetectManualSlTpAsync(symbol, isLongPosition, entry, pos);

                            if (det.Sl.HasValue)
                                await _notify.SendAsync($"[{symbol}] SL touched (MarkPrice) → waiting exchange SL.");
                            else
                            {
                                await _notify.SendAsync($"[{symbol}] SL touched but missing on exchange → force close.");
                                await _exchange.ClosePositionAsync(symbol, qty);
                            }

                            await SafeCancelLeftoverProtectiveAsync(symbol);
                            return;
                        }
                    }

                    // =================== FLEX EXIT LOGIC + TIME-STOP (MODE AWARE) ===================
                    if (!inGrace && cachedCandles != null && cachedCandles.Count >= 10 && hasEntry && canUseRR)
                    {
                        var c0 = cachedCandles[^2]; // last closed
                        var c1 = cachedCandles[^3];

                        if (!timeStopAnchorUtc.HasValue)
                            timeStopAnchorUtc = c0.OpenTime;

                        decimal ema34 = ComputeEmaLast(cachedCandles, 34);
                        decimal ema89 = ComputeEmaLast(cachedCandles, 89);
                        decimal ema200 = ComputeEmaLast(cachedCandles, 200);

                        decimal boundary = isLongPosition
                            ? GetDynamicBoundaryForLong(entry, ema34, ema89, ema200)
                            : GetDynamicBoundaryForShort(entry, ema34, ema89, ema200);

                        bool danger =
                            IsDangerImpulseReverse(c0, c1, isLongPosition)
                            || (boundary > 0 && IsBoundaryBroken(c0.Close, boundary, isLongPosition, profile.EmaBreakTolerance));

                        bool weakening = IsWeakeningAfterMove(cachedCandles, isLongPosition);

                        // ===== TIME-STOP =====
                        if (!timeStopTriggered && timeStopAnchorUtc.HasValue)
                        {
                            int barsPassed = CountClosedBarsSince(timeStopAnchorUtc.Value, c0.OpenTime, 15);
                            if (barsPassed >= profile.TimeStopBars && rr < profile.TimeStopMinRR)
                            {
                                timeStopTriggered = true;
                                await _notify.SendAsync(
                                    $"[{symbol}] TIME-STOP: {barsPassed} nến M15 mà rr={rr:F2} < {profile.TimeStopMinRR:F2}R → close | mode={profile.Tag}");
                                await _exchange.ClosePositionAsync(symbol, qty);
                                await SafeCancelLeftoverProtectiveAsync(symbol);
                                return;
                            }
                        }

                        // ===== Profit protect =====
                        if (rr >= profile.ProtectAtRR && hasSL && IsValidStopLoss(sl, isLongPosition, entry))
                        {
                            decimal targetSL = GetBreakEvenLockSL(entry, sl, risk, isLongPosition, profile.BreakEvenBufferR);

                            if (IsBetterStopLoss(targetSL, sl, isLongPosition))
                            {
                                sl = targetSL;
                                lastSlTpCheckUtc = await UpdateStopLossAsync(
                                    symbol,
                                    targetSL,
                                    isLongPosition,
                                    hasTP,
                                    tp,
                                    pos,
                                    lastSlTpCheckUtc);

                                await _notify.SendAsync($"[{symbol}] PROTECT: rr={rr:F2} → lock SL={Math.Round(sl, 6)} | mode={profile.Tag}");
                            }
                        }

                        // ===== Quick take =====
                        if (rr >= profile.QuickTakeMinRR && weakening)
                        {
                            if (rr >= profile.QuickTakeGoodRR || danger || IsOppositeStrongCandle(c0, isLongPosition))
                            {
                                await _notify.SendAsync($"[{symbol}] QUICK TAKE: rr={rr:F2} → close | mode={profile.Tag}");
                                await _exchange.ClosePositionAsync(symbol, qty);
                                await SafeCancelLeftoverProtectiveAsync(symbol);
                                return;
                            }
                        }

                        // ===== Danger cut =====
                        if (rr <= profile.DangerCutIfRRBelow && danger)
                        {
                            await _notify.SendAsync($"[{symbol}] DANGER CUT: rr={rr:F2} + danger → close | mode={profile.Tag}");
                            await _exchange.ClosePositionAsync(symbol, qty);
                            await SafeCancelLeftoverProtectiveAsync(symbol);
                            return;
                        }

                        // ===== Exit on boundary break nếu đã dương chút =====
                        if (danger && rr >= 0.10m)
                        {
                            await _notify.SendAsync($"[{symbol}] EXIT ON BOUNDARY BREAK: rr={rr:F2} → close | mode={profile.Tag}");
                            await _exchange.ClosePositionAsync(symbol, qty);
                            await SafeCancelLeftoverProtectiveAsync(symbol);
                            return;
                        }
                    }

                    // =================== TP hit (nếu có) ===================
                    if (hasEntry && hasTP && IsValidTakeProfit(tp, isLongPosition, entry))
                    {
                        bool hitTp = (isLongPosition && price >= tp) || (!isLongPosition && price <= tp);
                        if (hitTp)
                        {
                            decimal? tpOnExchange2 = null;

                            if ((DateTime.UtcNow - lastSlTpCheckUtc) >= TimeSpan.FromSeconds(5))
                            {
                                var det2 = await DetectManualSlTpAsync(symbol, isLongPosition, entry, pos);
                                tpOnExchange2 = det2.Tp;
                                lastSlTpCheckUtc = DateTime.UtcNow;
                            }

                            if (!tpOnExchange2.HasValue)
                            {
                                await _notify.SendAsync($"[{symbol}] Giá chạm TP nhưng không thấy TP trên sàn → đóng position. | mode={profile.Tag}");
                                await _exchange.ClosePositionAsync(symbol, qty);
                            }
                            else
                            {
                                await _notify.SendAsync($"[{symbol}] TP touched → stop monitor. | mode={profile.Tag}");
                            }

                            await SafeCancelLeftoverProtectiveAsync(symbol);
                            return;
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
        //          MANUAL ATTACH POSITION (AUTO SAFETY TP) - TREND DEFAULT
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

            var det = await DetectManualSlTpAsync(pos.Symbol, isLong, entry, pos);

            decimal? sl = det.Sl;
            decimal? tp = det.Tp;

            var profile = ModeProfile.For(TradeMode.Trend);

            if (!tp.HasValue && sl.HasValue && entry > 0)
            {
                decimal risk = isLong ? entry - sl.Value : sl.Value - entry;
                if (risk > 0)
                {
                    var safetyTp = isLong
                        ? entry + risk * profile.SafetyTpRR
                        : entry - risk * profile.SafetyTpRR;

                    tp = safetyTp;

                    await _notify.SendAsync($"[{pos.Symbol}] MANUAL ATTACH: thiếu TP → đặt SAFETY-TP={Math.Round(safetyTp, 6)}");
                    var ok = await _exchange.PlaceTakeProfitAsync(pos.Symbol, isLong ? "LONG" : "SHORT", Math.Abs(qty), safetyTp);
                    if (!ok) await _notify.SendAsync($"[{pos.Symbol}] SAFETY-TP FAILED (manual attach).");
                }
            }

            await _notify.SendAsync(
                $"[{pos.Symbol}] MANUAL ATTACH → side={(isLong ? "LONG" : "SHORT")} entry={entry}, SL={sl}, TP={tp}");

            var signal = new TradeSignal
            {
                Symbol = pos.Symbol,
                Type = isLong ? SignalType.Long : SignalType.Short,
                EntryPrice = entry,
                StopLoss = sl,
                TakeProfit = tp,
                Time = DateTime.UtcNow,
                Reason = "MANUAL ATTACH",
                Mode = TradeMode.Trend
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
        //   DETECT TP/SL từ openOrders + openAlgoOrders
        // ============================================================

        private sealed class SlTpDetection
        {
            public decimal? Sl { get; set; }
            public decimal? Tp { get; set; }
            public int TotalOrders { get; set; }
            public int ConsideredOrders { get; set; }
        }

        private async Task<SlTpDetection> DetectManualSlTpAsync(
            string symbol, bool isLong, decimal entryPriceFromCaller, PositionInfo pos)
        {
            var normalOrders = await _exchange.GetOpenOrdersAsync(symbol);
            var algoOrders = await _exchange.GetOpenAlgoOrdersAsync(symbol);

            var orders = new List<OpenOrderInfo>();
            if (normalOrders != null) orders.AddRange(normalOrders);
            if (algoOrders != null) orders.AddRange(algoOrders);

            var result = new SlTpDetection { TotalOrders = orders.Count };

            if (orders.Count == 0)
                return result;

            static decimal GetTrigger(OpenOrderInfo o)
            {
                if (o == null) return 0m;
                if (o.StopPrice > 0) return o.StopPrice;
                if (o.Price > 0) return o.Price;
                return 0m;
            }

            static bool IsTake(string type)
                => !string.IsNullOrWhiteSpace(type) &&
                   type.Contains("TAKE", StringComparison.OrdinalIgnoreCase);

            static bool IsStop(string type)
                => !string.IsNullOrWhiteSpace(type) &&
                   (type.Contains("STOP", StringComparison.OrdinalIgnoreCase) ||
                    type.Contains("LOSS", StringComparison.OrdinalIgnoreCase))
                   && !type.Contains("TAKE", StringComparison.OrdinalIgnoreCase);

            static bool IsProtectiveSideForPosition(OpenOrderInfo o, bool isLongPos)
            {
                var side = (o?.Side ?? "").Trim().ToUpperInvariant();
                if (string.IsNullOrWhiteSpace(side)) return true;
                return isLongPos ? side == "SELL" : side == "BUY";
            }

            decimal markPrice = pos?.MarkPrice ?? 0m;
            decimal exEntry = pos?.EntryPrice ?? 0m;

            decimal entryPivot =
                exEntry > 0 ? exEntry :
                entryPriceFromCaller > 0 ? entryPriceFromCaller : 0m;

            decimal? sl = null;
            decimal? tp = null;

            foreach (var o in orders)
            {
                if (o == null) continue;

                string type = o.Type ?? string.Empty;
                decimal trigger = GetTrigger(o);
                if (trigger <= 0) continue;

                bool take = IsTake(type);
                bool stop = IsStop(type);
                if (!take && !stop) continue;

                if (!IsProtectiveSideForPosition(o, isLong))
                    continue;

                result.ConsideredOrders++;

                if (entryPivot > 0m)
                {
                    if (isLong)
                    {
                        if (stop && trigger < entryPivot)
                            sl = sl.HasValue ? Math.Max(sl.Value, trigger) : trigger;

                        if (take && trigger > entryPivot)
                            tp = tp.HasValue ? Math.Min(tp.Value, trigger) : trigger;
                    }
                    else
                    {
                        if (stop && trigger > entryPivot)
                            sl = sl.HasValue ? Math.Min(sl.Value, trigger) : trigger;

                        if (take && trigger < entryPivot)
                            tp = tp.HasValue ? Math.Max(tp.Value, trigger) : trigger;
                    }

                    continue;
                }

                if (markPrice > 0m)
                {
                    if (isLong)
                    {
                        if (stop && trigger < markPrice)
                            sl = sl.HasValue ? Math.Max(sl.Value, trigger) : trigger;

                        if (take && trigger > markPrice)
                            tp = tp.HasValue ? Math.Min(tp.Value, trigger) : trigger;
                    }
                    else
                    {
                        if (stop && trigger > markPrice)
                            sl = sl.HasValue ? Math.Min(sl.Value, trigger) : trigger;

                        if (take && trigger < markPrice)
                            tp = tp.HasValue ? Math.Max(tp.Value, trigger) : trigger;
                    }

                    continue;
                }

                if (take && !tp.HasValue) tp = trigger;
                if (stop && !sl.HasValue) sl = trigger;
            }

            result.Sl = sl;
            result.Tp = tp;
            return result;
        }

        // ============================================================
        //                  VALIDATION HELPERS
        // ============================================================

        private static bool IsValidStopLoss(decimal sl, bool isLong, decimal entry)
        {
            if (sl <= 0m || entry <= 0m) return false;
            return isLong ? sl < entry : sl > entry;
        }

        private static bool IsValidTakeProfit(decimal tp, bool isLong, decimal entry)
        {
            if (tp <= 0m || entry <= 0m) return false;
            return isLong ? tp > entry : tp < entry;
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

        private static bool IsBoundaryBroken(decimal close, decimal boundary, bool isLong, decimal emaTol)
        {
            if (boundary <= 0m) return false;
            return isLong
                ? close < boundary * (1m - emaTol)
                : close > boundary * (1m + emaTol);
        }

        // ============================================================
        //              FLEX EXIT HELPERS
        // ============================================================

        private static decimal GetBreakEvenLockSL(decimal entry, decimal currentSL, decimal risk, bool isLong, decimal breakEvenBufferR)
        {
            decimal be = entry;
            decimal buffer = risk * breakEvenBufferR;

            decimal target = isLong ? (be + buffer) : (be - buffer);

            if (isLong)
                return Math.Max(currentSL, Math.Min(target, entry + risk * 0.30m));
            else
                return Math.Min(currentSL, Math.Max(target, entry - risk * 0.30m));
        }

        private static bool IsBetterStopLoss(decimal newSL, decimal oldSL, bool isLong)
        {
            if (newSL <= 0m || oldSL <= 0m) return false;
            return isLong ? newSL > oldSL : newSL < oldSL;
        }

        private static bool IsOppositeStrongCandle(Candle c, bool isLong)
        {
            decimal range = c.High - c.Low;
            if (range <= 0) return false;

            decimal body = Math.Abs(c.Close - c.Open);
            decimal bodyToRange = body / range;

            bool strong = bodyToRange >= 0.55m;
            bool opposite = isLong ? (c.Close < c.Open) : (c.Close > c.Open);

            return strong && opposite;
        }

        private static bool IsDangerImpulseReverse(Candle c0, Candle c1, bool isLong)
        {
            decimal range = c0.High - c0.Low;
            if (range <= 0) return false;

            bool opposite = isLong ? (c0.Close < c0.Open) : (c0.Close > c0.Open);

            decimal body = Math.Abs(c0.Close - c0.Open);
            decimal bodyToRange = body / range;

            bool strongBody = bodyToRange >= ImpulseBodyToRangeMin;
            bool volSpike = (c1.Volume > 0m) && (c0.Volume >= c1.Volume * ImpulseVolVsPrevMin);

            bool closeNearEdge = false;
            if (isLong)
            {
                decimal closePosFromLow = (c0.Close - c0.Low) / range;
                closeNearEdge = closePosFromLow <= 0.25m;
            }
            else
            {
                decimal closePosFromHigh = (c0.High - c0.Close) / range;
                closeNearEdge = closePosFromHigh <= 0.25m;
            }

            return opposite && strongBody && volSpike && closeNearEdge;
        }

        private static bool IsWeakeningAfterMove(IReadOnlyList<Candle> candles, bool isLong)
        {
            int n = candles.Count;
            int i0 = n - 2;
            int i1 = n - 3;
            int i2 = n - 4;
            if (i2 < 0) return false;

            var c0 = candles[i0];
            var c1 = candles[i1];
            var c2 = candles[i2];

            decimal r0 = c0.High - c0.Low;
            decimal r1 = c1.High - c1.Low;
            if (r0 <= 0 || r1 <= 0) return false;

            decimal b0 = Math.Abs(c0.Close - c0.Open);
            decimal b1 = Math.Abs(c1.Close - c1.Open);

            bool bodyShrinking = b0 <= b1 * 0.80m;
            bool volDropping = c0.Volume <= c1.Volume * 0.90m;

            bool oppositeCandle = isLong ? (c0.Close < c0.Open) : (c0.Close > c0.Open);

            bool rejectWick = false;
            if (isLong)
            {
                decimal upperWick = c0.High - Math.Max(c0.Open, c0.Close);
                decimal bodySafe = Math.Max(b0, r0 * 0.10m);
                rejectWick = upperWick / bodySafe >= 1.8m;
            }
            else
            {
                decimal lowerWick = Math.Min(c0.Open, c0.Close) - c0.Low;
                decimal bodySafe = Math.Max(b0, r0 * 0.10m);
                rejectWick = lowerWick / bodySafe >= 1.8m;
            }

            int score = 0;
            if (bodyShrinking) score++;
            if (volDropping) score++;
            if (oppositeCandle || rejectWick) score++;

            bool hadMove = isLong
                ? (c2.Close > c2.Open || c1.Close > c1.Open)
                : (c2.Close < c2.Open || c1.Close < c1.Open);

            return hadMove && score >= 2;
        }

        private static int CountClosedBarsSince(DateTime anchorOpenTime, DateTime lastClosedOpenTime, int timeframeMinutes)
        {
            if (timeframeMinutes <= 0) timeframeMinutes = 15;
            var diff = lastClosedOpenTime - anchorOpenTime;
            var mins = diff.TotalMinutes;
            if (mins < 0) return 0;
            return (int)Math.Floor(mins / timeframeMinutes);
        }

        // ============================================================
        //                     UPDATE STOPLOSS
        // ============================================================

        private async Task<DateTime> UpdateStopLossAsync(
            string symbol,
            decimal newSL,
            bool isLong,
            bool hasTp,
            decimal? expectedTp,
            PositionInfo currentPos,
            DateTime lastSlTpCheckUtc)
        {
            await _notify.SendAsync($"[{symbol}] Trailing SL update → {Math.Round(newSL, 6)}");

            await _exchange.CancelStopLossOrdersAsync(symbol);

            // ✅ FIX CS8602: guard currentPos
            if (currentPos == null)
                currentPos = await _exchange.GetPositionAsync(symbol);

            decimal qty = Math.Abs(currentPos?.PositionAmt ?? 0m);
            string posSide = isLong ? "LONG" : "SHORT";

            if (qty <= 0m)
            {
                var pos = await _exchange.GetPositionAsync(symbol);
                qty = Math.Abs(pos.PositionAmt);
                if (qty <= 0m)
                {
                    await _notify.SendAsync($"[{symbol}] Không tìm thấy position khi update SL.");
                    ClearMonitoringPosition(symbol);
                    return lastSlTpCheckUtc;
                }

                currentPos = pos;
            }

            string side = isLong ? "SELL" : "BUY";
            await _exchange.PlaceStopOnlyAsync(symbol, side, posSide, qty, newSL);

            // Giữ TP nếu bị mất (throttle)
            if (hasTp && expectedTp.HasValue)
            {
                if ((DateTime.UtcNow - lastSlTpCheckUtc) >= TimeSpan.FromSeconds(SlTpCheckEverySec))
                {
                    var det = await DetectManualSlTpAsync(symbol, isLong, currentPos.EntryPrice, currentPos);
                    lastSlTpCheckUtc = DateTime.UtcNow;

                    if (!det.Tp.HasValue)
                    {
                        decimal tpVal = expectedTp.Value;
                        decimal tpDisplay = Math.Round(tpVal, 6);

                        await _notify.SendAsync($"[{symbol}] Giữ TP → đặt lại TP {tpDisplay}");

                        var ok = await _exchange.PlaceTakeProfitAsync(symbol, posSide, qty, tpVal);
                        if (!ok)
                            await _notify.SendAsync($"[{symbol}] Giữ TP FAILED → tp={tpDisplay}");
                    }
                }
            }

            return lastSlTpCheckUtc;
        }

        // ============================================================
        //                      CLEANUP / SAFETY
        // ============================================================

        private async Task SafeCancelLeftoverProtectiveAsync(string symbol)
        {
            try { await _exchange.CancelAllOpenOrdersAsync(symbol); } catch { }
            try { await _exchange.CancelStopLossOrdersAsync(symbol); } catch { }
        }
    }
}
