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
    /// FIX:
    /// - RR theo USD PnL / USD Risk
    /// - MinProfitUsd gate để tránh lock quá sớm (case lãi 0.03 mà lock)
    /// - NEW: ATR-based trailing + anti kéo SL sát entry (chống quét ngu)
    ///
    /// PATCH (HƯỚNG 2):
    /// - Anti "trail ngu" bị quét pullback:
    ///   + Chỉ cho phép trailing sớm khi có "pullback -> resume" (cấu trúc continuation)
    ///   + Throttle trailing: tối thiểu X giây / tối thiểu bước SL theo ATR để tránh update liên tục
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

        // =============== ATR trailing (NEW) ========================
        private const int AtrPeriod = 14;
        private const decimal ScalpAtrMult = 0.8m;   // scalp: trailing chặt hơn nhưng vẫn ngoài noise
        private const decimal TrendAtrMult = 1.2m;   // trend: trailing rộng hơn
        private const decimal AtrToAllowProfitLock = 1.0m; // phải đi >= 1 ATR mới cho phép SL vượt entry (lock lời)

        // =============== PATCH: anti pullback trailing (HƯỚNG 2) ===
        // 1) Chỉ trailing sớm nếu giá đã đi đủ một đoạn + có pullback->resume
        private const decimal RequireMoveBeforeTrailAtrFrac = 0.60m;   // giá phải đi >= 0.6 ATR từ entry
        private const decimal PullbackMustNotBreakEntryAtrFrac = 0.20m; // pullback không được xuyên entry quá sâu (theo ATR)
        private const int TrailingMinUpdateIntervalSec = 45;           // tối thiểu 45s mới update SL 1 lần
        private const decimal TrailingMinStepAtrFrac = 0.15m;          // SL phải "tốt hơn" tối thiểu 0.15 ATR mới update

        // =============== Debug throttle ===========================
        private readonly ConcurrentDictionary<string, DateTime> _lastMissingLogUtc = new();

        // PATCH: trailing throttle per symbol
        private readonly ConcurrentDictionary<string, DateTime> _lastTrailingUpdateUtc = new();
        private readonly ConcurrentDictionary<string, decimal> _lastTrailingSl = new();

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

            // --- Anti “0.03$ lock” gate ---
            // Chỉ trigger PROTECT / QUICK TAKE / DANGER CUT nếu PnL đạt tối thiểu theo USD
            public decimal MinProtectProfitUsd { get; init; }
            public decimal MinQuickTakeProfitUsd { get; init; }
            public decimal MinDangerCutAbsLossUsd { get; init; }

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

                        // ✅ FIX: scalp nhỏ, fee ảnh hưởng nhiều → đừng lock/close khi PnL còn vài cent
                        MinProtectProfitUsd = 0.10m,
                        MinQuickTakeProfitUsd = 0.15m,
                        MinDangerCutAbsLossUsd = 0.12m,
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

                        MinProtectProfitUsd = 0.15m,
                        MinQuickTakeProfitUsd = 0.25m,
                        MinDangerCutAbsLossUsd = 0.18m,
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

                        MinProtectProfitUsd = 0.25m,
                        MinQuickTakeProfitUsd = 0.40m,
                        MinDangerCutAbsLossUsd = 0.30m,
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
                    if (elapsed > profile.LimitTimeout)
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

                    // =================== Compute RR / risk (FIX: USD-based) ===================
                    decimal riskPrice = 0m;
                    decimal riskUsd = 0m;
                    decimal pnlUsd = 0m;
                    bool canUseRR = false;

                    if (hasEntry)
                    {
                        // Unrealized PnL (approx, USDT-m)
                        pnlUsd = isLongPosition
                            ? (price - entry) * absQty
                            : (entry - price) * absQty;
                    }

                    if (hasEntry && hasSL && IsValidStopLoss(sl, isLongPosition, entry))
                    {
                        riskPrice = isLongPosition ? (entry - sl) : (sl - entry);
                        if (riskPrice > 0m)
                        {
                            riskUsd = riskPrice * absQty;
                            if (riskUsd > 0m) canUseRR = true;
                        }
                    }

                    decimal rr = 0m;
                    if (canUseRR && riskUsd > 0m)
                        rr = pnlUsd / riskUsd;

                    // =================== SAFETY TP (nếu thiếu TP) - MODE AWARE ===================
                    if (hasEntry && hasSL && !hasTP && !autoTpPlaced)
                    {
                        if (riskPrice > 0m)
                        {
                            decimal safetyTp = isLongPosition
                                ? entry + riskPrice * profile.SafetyTpRR
                                : entry - riskPrice * profile.SafetyTpRR;

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
                                    $"[{symbol}] TIME-STOP: {barsPassed} nến M15 mà rr={rr:F2} < {profile.TimeStopMinRR:F2}R → close | pnl={pnlUsd:F4} risk={riskUsd:F4} | mode={profile.Tag}");
                                await _exchange.ClosePositionAsync(symbol, qty);
                                await SafeCancelLeftoverProtectiveAsync(symbol);
                                return;
                            }
                        }

                        // ===== Profit protect (ATR-based, anti kéo SL sát entry) =====
                        if (rr >= profile.ProtectAtRR
                            && pnlUsd >= profile.MinProtectProfitUsd
                            && hasSL
                            && IsValidStopLoss(sl, isLongPosition, entry))
                        {
                            decimal atr = ComputeAtr(cachedCandles, AtrPeriod);
                            if (atr > 0m)
                            {
                                decimal atrMult = signal.Mode == TradeMode.Scalp ? ScalpAtrMult : TrendAtrMult;

                                bool movedEnoughForProfitLock =
                                    isLongPosition
                                        ? price >= entry + atr * AtrToAllowProfitLock
                                        : price <= entry - atr * AtrToAllowProfitLock;

                                // ============================
                                // >>> PATCH START: HƯỚNG 2
                                // - Nếu CHƯA đi đủ để lock profit (>= 1 ATR)
                                //   thì KHÔNG trail SL chỉ vì rr/pnl đạt.
                                // - Bắt buộc có "pullback -> resume" để tránh bị quét pullback rồi bay tiếp.
                                // - Đồng thời throttle update (time + step theo ATR).
                                // ============================
                                if (!movedEnoughForProfitLock)
                                {
                                    bool movedMin = isLongPosition
                                        ? price >= entry + atr * RequireMoveBeforeTrailAtrFrac
                                        : price <= entry - atr * RequireMoveBeforeTrailAtrFrac;

                                    bool pullbackResumeOk = movedMin && HasPullbackThenResume(c0, c1, isLongPosition, entry, atr);

                                    if (!pullbackResumeOk)
                                    {
                                        // Skip trailing sớm để tránh "update liên tục rồi bị quét"
                                        goto AFTER_PROTECT_BLOCK;
                                    }
                                }
                                // >>> PATCH END
                                // ============================

                                // Phase 1: lock về gần BE nhưng KHÔNG vượt entry (tránh quét ngu)
                                // Phase 2: chỉ khi đi >= 1 ATR mới cho trailing theo ATR (có thể vượt entry)
                                decimal targetSL;

                                if (!movedEnoughForProfitLock)
                                {
                                    // giữ SL dưới entry (LONG) / trên entry (SHORT) một khoảng nhỏ theo ATR
                                    // để tránh bị quét ngay tại entry vì noise/spread
                                    decimal beGuard = Math.Min(atr * 0.25m, riskPrice * 0.50m);

                                    targetSL = isLongPosition
                                        ? (entry - beGuard)     // LONG: vẫn dưới entry
                                        : (entry + beGuard);    // SHORT: vẫn trên entry
                                }
                                else
                                {
                                    // ATR trailing
                                    targetSL = isLongPosition
                                        ? (price - atr * atrMult)
                                        : (price + atr * atrMult);
                                }

                                // additional safety: nếu chưa movedEnough thì tuyệt đối không cho vượt entry
                                if (!movedEnoughForProfitLock)
                                {
                                    if (isLongPosition) targetSL = Math.Min(targetSL, entry);
                                    else targetSL = Math.Max(targetSL, entry);
                                }

                                // PATCH: throttle update (time + step)
                                if (IsBetterStopLoss(targetSL, sl, isLongPosition) && CanUpdateTrailing(symbol, sl, targetSL, isLongPosition, atr))
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

                                    await _notify.SendAsync(
                                        $"[{symbol}] PROTECT ATR: rr={rr:F2} pnl={pnlUsd:F4} risk={riskUsd:F4} atr={atr:F4} moved={(movedEnoughForProfitLock ? "Y" : "N")} → SL={Math.Round(sl, 6)} | mode={profile.Tag}");
                                }
                            }
                            else
                            {
                                // fallback: giữ logic cũ nếu ATR không tính được
                                decimal targetSL = GetBreakEvenLockSL(entry, sl, riskPrice, isLongPosition, profile.BreakEvenBufferR);

                                // anti: đừng cho vượt entry nếu chưa có ATR
                                if (isLongPosition) targetSL = Math.Min(targetSL, entry);
                                else targetSL = Math.Max(targetSL, entry);

                                if (IsBetterStopLoss(targetSL, sl, isLongPosition) && CanUpdateTrailing(symbol, sl, targetSL, isLongPosition, 0m))
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

                                    await _notify.SendAsync($"[{symbol}] PROTECT (fallback): rr={rr:F2} pnl={pnlUsd:F4} → SL={Math.Round(sl, 6)} | mode={profile.Tag}");
                                }
                            }
                        }

                    AFTER_PROTECT_BLOCK:

                        // ===== Quick take (FIX: thêm min profit USD) =====
                        if (rr >= profile.QuickTakeMinRR && pnlUsd >= profile.MinQuickTakeProfitUsd && weakening)
                        {
                            if (rr >= profile.QuickTakeGoodRR || danger || IsOppositeStrongCandle(c0, isLongPosition))
                            {
                                await _notify.SendAsync($"[{symbol}] QUICK TAKE: rr={rr:F2} pnl={pnlUsd:F4} → close | mode={profile.Tag}");
                                await _exchange.ClosePositionAsync(symbol, qty);
                                await SafeCancelLeftoverProtectiveAsync(symbol);
                                return;
                            }
                        }

                        // ===== Danger cut (FIX: thêm abs loss USD) =====
                        if (rr <= profile.DangerCutIfRRBelow && danger)
                        {
                            var absLossUsd = Math.Max(0m, -pnlUsd);
                            if (absLossUsd >= profile.MinDangerCutAbsLossUsd)
                            {
                                await _notify.SendAsync($"[{symbol}] DANGER CUT: rr={rr:F2} loss={absLossUsd:F4} + danger → close | mode={profile.Tag}");
                                await _exchange.ClosePositionAsync(symbol, qty);
                                await SafeCancelLeftoverProtectiveAsync(symbol);
                                return;
                            }
                        }

                        // ===== Exit on boundary break nếu đã dương chút (FIX: thêm min profit) =====
                        if (danger && rr >= 0.10m && pnlUsd >= profile.MinProtectProfitUsd)
                        {
                            await _notify.SendAsync($"[{symbol}] EXIT ON BOUNDARY BREAK: rr={rr:F2} pnl={pnlUsd:F4} → close | mode={profile.Tag}");
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

        // ============================================================
        //                       ATR HELPER (NEW)
        // ============================================================

        private static decimal ComputeAtr(IReadOnlyList<Candle> candles, int period = 14)
        {
            if (candles == null || candles.Count < period + 1) return 0m;

            decimal sum = 0m;
            for (int i = candles.Count - period; i < candles.Count; i++)
            {
                var c = candles[i];
                var prev = candles[i - 1];

                decimal tr = Math.Max(
                    c.High - c.Low,
                    Math.Max(
                        Math.Abs(c.High - prev.Close),
                        Math.Abs(c.Low - prev.Close)
                    )
                );

                sum += tr;
            }

            return sum / period;
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
        // PATCH HELPERS: pullback->resume + throttle trailing
        // ============================================================

        private static bool HasPullbackThenResume(Candle lastClosed, Candle prevClosed, bool isLong, decimal entry, decimal atr)
        {
            // prevClosed = pullback candle (ngược hướng)
            bool pullback = isLong ? (prevClosed.Close < prevClosed.Open) : (prevClosed.Close > prevClosed.Open);
            // lastClosed = resume candle (thuận hướng)
            bool resume = isLong ? (lastClosed.Close > lastClosed.Open) : (lastClosed.Close < lastClosed.Open);

            if (!pullback || !resume) return false;

            // Pullback không được xuyên entry quá sâu (tránh pullback là đảo trend thật)
            // LONG: low phải > entry - 0.2*ATR ; SHORT: high phải < entry + 0.2*ATR
            if (atr > 0m)
            {
                if (isLong)
                {
                    if (prevClosed.Low <= entry - atr * PullbackMustNotBreakEntryAtrFrac)
                        return false;
                }
                else
                {
                    if (prevClosed.High >= entry + atr * PullbackMustNotBreakEntryAtrFrac)
                        return false;
                }
            }

            // Resume phải "đẩy lại" ít nhất vượt close của pullback
            if (isLong)
                return lastClosed.Close > prevClosed.Close;
            else
                return lastClosed.Close < prevClosed.Close;
        }

        private bool CanUpdateTrailing(string symbol, decimal currentSl, decimal targetSl, bool isLong, decimal atr)
        {
            // 1) Interval gate
            var now = DateTime.UtcNow;
            if (_lastTrailingUpdateUtc.TryGetValue(symbol, out var lastUtc))
            {
                if ((now - lastUtc) < TimeSpan.FromSeconds(TrailingMinUpdateIntervalSec))
                    return false;
            }

            // 2) Step gate (tránh update vi mô)
            if (atr > 0m)
            {
                decimal minStep = atr * TrailingMinStepAtrFrac;
                decimal delta = Math.Abs(targetSl - currentSl);
                if (delta < minStep)
                    return false;
            }

            // 3) Nếu target giống lần update trước (sàn lag / rounding) thì skip
            if (_lastTrailingSl.TryGetValue(symbol, out var lastSl))
            {
                if (Math.Abs(lastSl - targetSl) < (atr > 0m ? atr * 0.05m : 0.000001m))
                    return false;
            }

            _lastTrailingUpdateUtc[symbol] = now;
            _lastTrailingSl[symbol] = targetSl;
            return true;
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

            // guard currentPos
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
