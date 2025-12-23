// OrderManagerService.cs (PATCHED - ANTI KILL GOOD SETUPS + TIME-STOP smarter + MICRO PROFIT PROTECT by %RISK)
// PATCH IMPROVE (Dec 2025):
// - FIX: DangerConfirmBarsMajor/Alt đúng 2/1 nến đã đóng + count từ candle danger đầu tiên
// - FIX: NO-KILL ZONE không bị MICRO TAKE (0.50R) giết kèo -> trong no-kill: chuyển sang LOCK SL, không close
// - IMPROVE: micro risk USD clamp giữa riskUsd thật và plannedRiskUsd (tránh lệch sizing)
// - IMPROVE: UpdateStopLossAsync chắc hơn: delay dài hơn + verify SL trên sàn + retry 1 lần

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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

        private static readonly TimeSpan DefaultLimitTimeout = TimeSpan.FromMinutes(30);

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
        private const decimal ScalpAtrMult = 0.8m;
        private const decimal TrendAtrMult = 1.2m;
        private const decimal AtrToAllowProfitLock = 1.0m;

        // =============== PATCH: anti pullback trailing (HƯỚNG 2) ===
        private const decimal RequireMoveBeforeTrailAtrFrac = 0.60m;
        private const decimal PullbackMustNotBreakEntryAtrFrac = 0.20m;
        private const int TrailingMinUpdateIntervalSec = 45;
        private const decimal TrailingMinStepAtrFrac = 0.15m;

        // =============== PATCH: EARLY EXIT (fee-safe) ==============
        private const decimal StallRangeAtrFrac = 0.45m;
        private const decimal NearBoundaryAtrFrac = 0.20m;
        private const decimal StallSmallBodyToRangeMax = 0.45m;

        // =============== Fee model (NEW) ===========================
        private const decimal DefaultTakerFeeRate = 0.0004m;
        private const decimal DefaultMakerFeeRate = 0.0002m;

        private const decimal FeeEwmaAlpha = 0.20m;
        private static readonly TimeSpan RealFeeLookback = TimeSpan.FromMinutes(30);

        private const decimal ProtectMinNetProfitVsFeeMult = 2.0m;
        private const decimal QuickMinNetProfitVsFeeMult = 2.0m;
        private const decimal EarlyExitMinNetProfitVsFeeMult = 1.2m;
        private const decimal BoundaryExitMinNetProfitVsFeeMult = 1.8m;

        private const decimal MinSlDistanceAtrFrac = 0.35m;
        private const decimal FeeBreakevenBufferMult = 1.25m;

        // ===== MICRO PROFIT PROTECT by planned risk (R-based) =====
        private const decimal MicroLockAtR = 0.25m;
        private const decimal MicroTakeAtR = 0.50m;
        private const decimal MicroMinNetRrToAct = 0.05m;
        private const decimal MicroTrailAtrMult = 0.45m;

        // ===== ANTI KILL GOOD SETUPS =====
        private const decimal NoKillZonePlannedRR = 1.20m;
        private const decimal NoAtrProtectScalpPlannedRR = 1.10m;

        // ===== ANTI FAKE DUMP -> PUMP (Danger confirm) =====
        // FIX: đúng 2 nến cho MAJOR, 1 nến cho ALT (count từ candle danger đầu tiên)
        private const int DangerConfirmBarsMajor = 2;
        private const int DangerConfirmBarsAlt = 1;

        private const decimal DangerHardCutNetRr = -0.80m;
        private const decimal ReclaimAtrFrac = 0.15m;

        // Pending danger timeout (nếu candle fetch fail / treo state lâu)
        private static readonly TimeSpan DangerPendingMaxAge = TimeSpan.FromMinutes(10);

        private sealed class FeeStats
        {
            public decimal EwmaRate { get; set; } = DefaultTakerFeeRate;
            public int Samples { get; set; } = 0;
            public DateTime LastUpdateUtc { get; set; } = DateTime.MinValue;
        }

        private readonly ConcurrentDictionary<string, FeeStats> _feeStatsBySymbol = new(StringComparer.OrdinalIgnoreCase);

        // =============== Debug throttle ===========================
        private readonly ConcurrentDictionary<string, DateTime> _lastMissingLogUtc = new();

        // PATCH: trailing throttle per symbol
        private readonly ConcurrentDictionary<string, DateTime> _lastTrailingUpdateUtc = new();
        private readonly ConcurrentDictionary<string, decimal> _lastTrailingSl = new();

        // ===== pending danger confirm =====
        private readonly ConcurrentDictionary<string, DateTime> _pendingDangerSinceUtc = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, int> _pendingDangerBars = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTime> _pendingDangerLastClosedOpenTime = new(StringComparer.OrdinalIgnoreCase);

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

            public decimal ProtectAtRR { get; init; }
            public decimal BreakEvenBufferR { get; init; }
            public decimal QuickTakeMinRR { get; init; }
            public decimal QuickTakeGoodRR { get; init; }
            public decimal DangerCutIfRRBelow { get; init; }
            public int TimeStopBars { get; init; }
            public decimal TimeStopMinRR { get; init; }

            public decimal MinProtectProfitUsd { get; init; }
            public decimal MinQuickTakeProfitUsd { get; init; }
            public decimal MinDangerCutAbsLossUsd { get; init; }

            public int EarlyExitBars { get; init; }
            public decimal EarlyExitMinRR { get; init; }
            public decimal EarlyExitMinProfitUsd { get; init; }

            public decimal SafetyTpRR { get; init; }
            public decimal EmaBreakTolerance { get; init; }
            public TimeSpan LimitTimeout { get; init; }

            public string Tag => Mode.ToString();

            public static ModeProfile For(TradeMode mode)
            {
                return mode switch
                {
                    TradeMode.Scalp => new ModeProfile
                    {
                        Mode = mode,
                        ProtectAtRR = 0.22m,
                        BreakEvenBufferR = 0.06m,
                        QuickTakeMinRR = 0.35m,
                        QuickTakeGoodRR = 0.60m,
                        DangerCutIfRRBelow = -0.25m,
                        TimeStopBars = 15,
                        TimeStopMinRR = 0.3m,
                        SafetyTpRR = 1.30m,
                        EmaBreakTolerance = 0.0012m,
                        LimitTimeout = TimeSpan.FromMinutes(20),

                        MinProtectProfitUsd = 0.10m,
                        MinQuickTakeProfitUsd = 0.15m,
                        MinDangerCutAbsLossUsd = 0.12m,

                        EarlyExitBars = 8,
                        EarlyExitMinRR = 0.1m,
                        EarlyExitMinProfitUsd = 0.12m,
                    },

                    TradeMode.Mode2_Continuation => new ModeProfile
                    {
                        Mode = mode,
                        ProtectAtRR = 0.25m,
                        BreakEvenBufferR = 0.05m,
                        QuickTakeMinRR = 0.40m,
                        QuickTakeGoodRR = 0.70m,
                        DangerCutIfRRBelow = -0.30m,
                        TimeStopBars = 15,
                        TimeStopMinRR = 0.3m,
                        SafetyTpRR = 1.60m,
                        EmaBreakTolerance = 0.0010m,
                        LimitTimeout = TimeSpan.FromMinutes(15),

                        MinProtectProfitUsd = 0.15m,
                        MinQuickTakeProfitUsd = 0.25m,
                        MinDangerCutAbsLossUsd = 0.18m,

                        EarlyExitBars = 8,
                        EarlyExitMinRR = 0.13m,
                        EarlyExitMinProfitUsd = 0.18m,
                    },

                    _ => new ModeProfile
                    {
                        Mode = TradeMode.Trend,
                        ProtectAtRR = 0.30m,
                        BreakEvenBufferR = 0.05m,
                        QuickTakeMinRR = 0.45m,
                        QuickTakeGoodRR = 0.75m,
                        DangerCutIfRRBelow = -0.35m,
                        TimeStopBars = 25,
                        TimeStopMinRR = 0.4m,
                        SafetyTpRR = DefaultSafetyTpRR,
                        EmaBreakTolerance = DefaultEmaBreakTolerance,
                        LimitTimeout = DefaultLimitTimeout,

                        MinProtectProfitUsd = 0.25m,
                        MinQuickTakeProfitUsd = 0.40m,
                        MinDangerCutAbsLossUsd = 0.30m,

                        EarlyExitBars = 15,
                        EarlyExitMinRR = 0.2m,
                        EarlyExitMinProfitUsd = 0.25m,
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
                        await _notify.SendAsync($"[{symbol}] LIMIT quá {profile.LimitTimeout.TotalMinutes:F0} phút chưa khớp → cancel open orders và stop LIMIT monitor.");
                        await SafeCancelAllOpenOrdersBothAsync(symbol);
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
                        await SafeCancelAllOpenOrdersBothAsync(symbol);
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

            bool timeStopTriggered = false;
            DateTime? timeStopAnchorUtc = null;

            decimal lastKnownAbsQty = 0m;
            decimal lastKnownMarkPrice = 0m;
            decimal lastKnownEntry = 0m;

            int tfMinutes = ParseIntervalMinutesSafe(_botConfig?.Intervals?.FirstOrDefault()?.FrameTime);

            await _notify.SendAsync($"[{symbol}] Monitor POSITION started... mode={profile.Tag} | FLEX EXIT + TIME-STOP {profile.TimeStopBars}x{tfMinutes}m");

            try
            {
                while (true)
                {
                    await Task.Delay(MonitorIntervalMs);

                    var pos = await _exchange.GetPositionAsync(symbol);
                    decimal qty = pos.PositionAmt;

                    if (qty == 0)
                    {
                        await TryAdaptiveFeeUpdateOnCloseAsync(symbol, lastKnownEntry, lastKnownMarkPrice, lastKnownAbsQty);

                        await _notify.SendAsync($"[{symbol}] Position closed → stop monitor.");
                        await SafeCancelLeftoverProtectiveAsync(symbol);
                        ClearDangerPending(symbol);
                        return;
                    }

                    bool isLongPosition = qty > 0;
                    decimal price = pos.MarkPrice;
                    decimal absQty = Math.Abs(qty);
                    string posSide = isLongPosition ? "LONG" : "SHORT";

                    lastKnownAbsQty = absQty;
                    lastKnownMarkPrice = price;

                    if (pos.EntryPrice > 0m)
                    {
                        if (entry <= 0m || Math.Abs(entry - pos.EntryPrice) / pos.EntryPrice > 0.0005m)
                            entry = pos.EntryPrice;
                    }

                    if (entry > 0m) lastKnownEntry = entry;

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
                        cachedCandles = await _exchange.GetRecentCandlesAsync(symbol, _botConfig.Intervals[0].FrameTime, 120);
                        lastCandleFetchUtc = DateTime.UtcNow;
                    }

                    // =================== Compute RR / risk (USD-based) + NET fee ===================
                    decimal riskPrice = 0m;
                    decimal riskUsd = 0m;

                    decimal pnlUsd = 0m;
                    decimal estFeeUsd = 0m;
                    decimal netPnlUsd = 0m;

                    bool canUseRR = false;

                    if (hasEntry)
                    {
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

                    if (hasEntry && absQty > 0m && entry > 0m && price > 0m)
                    {
                        decimal feeRate = GetEffectiveFeeRate(symbol);
                        decimal notionalEntry = entry * absQty;
                        decimal notionalExit = price * absQty;
                        estFeeUsd = (notionalEntry + notionalExit) * feeRate;

                        netPnlUsd = pnlUsd - estFeeUsd;
                    }
                    else
                    {
                        netPnlUsd = pnlUsd;
                    }

                    decimal rr = 0m;
                    decimal netRr = 0m;

                    if (canUseRR && riskUsd > 0m)
                    {
                        rr = pnlUsd / riskUsd;
                        netRr = netPnlUsd / riskUsd;
                    }

                    // ===== plannedRR =====
                    decimal plannedRR = 0m;
                    if (hasEntry && hasSL && hasTP && riskPrice > 0m && IsValidTakeProfit(tp, isLongPosition, entry))
                    {
                        decimal rewardPrice = isLongPosition ? (tp - entry) : (entry - tp);
                        if (rewardPrice > 0m)
                            plannedRR = rewardPrice / riskPrice;
                    }

                    bool isScalp = signal.Mode == TradeMode.Scalp;
                    bool inNoKillZone = plannedRR >= NoKillZonePlannedRR;
                    bool noAtrProtectForScalp = isScalp && plannedRR >= NoAtrProtectScalpPlannedRR;

                    // ===== thresholds dynamic theo plannedRR =====
                    decimal effProtectAtRR = profile.ProtectAtRR;
                    decimal effQuickMinRR = profile.QuickTakeMinRR;
                    decimal effQuickGoodRR = profile.QuickTakeGoodRR;
                    decimal effTimeStopMinRR = profile.TimeStopMinRR;

                    if (plannedRR > 0.25m)
                    {
                        effProtectAtRR = Clamp(Math.Min(profile.ProtectAtRR, plannedRR * 0.30m), 0.15m, profile.ProtectAtRR);

                        effQuickMinRR = Clamp(Math.Min(profile.QuickTakeMinRR, plannedRR * 0.35m), 0.20m, profile.QuickTakeMinRR);
                        effQuickGoodRR = Clamp(Math.Min(profile.QuickTakeGoodRR, plannedRR * 0.55m), effQuickMinRR + 0.05m, profile.QuickTakeGoodRR);

                        effTimeStopMinRR = Clamp(Math.Min(profile.TimeStopMinRR, plannedRR * 0.30m), 0.20m, profile.TimeStopMinRR);
                    }

                    // ===== fee-safe min net profit gates =====
                    decimal minProtectNetProfitUsd = Math.Max(profile.MinProtectProfitUsd, estFeeUsd * ProtectMinNetProfitVsFeeMult);
                    decimal minQuickNetProfitUsd = Math.Max(profile.MinQuickTakeProfitUsd, estFeeUsd * QuickMinNetProfitVsFeeMult);
                    decimal minEarlyNetProfitUsd = Math.Max(profile.EarlyExitMinProfitUsd, estFeeUsd * EarlyExitMinNetProfitVsFeeMult);
                    decimal minBoundaryNetProfitUsd = Math.Max(profile.MinProtectProfitUsd, estFeeUsd * BoundaryExitMinNetProfitVsFeeMult);

                    if (isScalp)
                    {
                        minProtectNetProfitUsd = Math.Max(minProtectNetProfitUsd, estFeeUsd * 2.5m);
                        minQuickNetProfitUsd = Math.Max(minQuickNetProfitUsd, estFeeUsd * 2.5m);
                        minBoundaryNetProfitUsd = Math.Max(minBoundaryNetProfitUsd, estFeeUsd * 2.2m);
                    }

                    // =================== SAFETY TP (nếu thiếu TP) ===================
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

                    // =================== SL hit ===================
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
                            ClearDangerPending(symbol);
                            return;
                        }
                    }

                    // =================== FLEX EXIT + TIME-STOP ===================
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

                        bool weakening = IsWeakeningAfterMove(cachedCandles, isLongPosition);
                        decimal atr = ComputeAtr(cachedCandles, AtrPeriod);

                        bool stall = atr > 0m && IsStallByAtrRange(cachedCandles, atr, profile.EarlyExitBars, StallRangeAtrFrac);
                        bool nearBoundary = atr > 0m && boundary > 0m && Math.Abs(c0.Close - boundary) <= atr * NearBoundaryAtrFrac;

                        // ===================== DANGER CONFIRM =====================
                        bool dangerCandidate =
                            IsDangerImpulseReverse(c0, c1, isLongPosition)
                            || (boundary > 0 && IsBoundaryBroken(c0.Close, boundary, isLongPosition, profile.EmaBreakTolerance));

                        bool reclaimed = false;
                        if (atr > 0m && boundary > 0m)
                        {
                            if (isLongPosition)
                                reclaimed = c0.Close >= boundary + atr * ReclaimAtrFrac;
                            else
                                reclaimed = c0.Close <= boundary - atr * ReclaimAtrFrac;
                        }

                        bool dangerHard = netRr <= DangerHardCutNetRr;

                        bool dangerConfirmed = await ApplyDangerConfirmAsync(
                            symbol: symbol,
                            profile: profile,
                            lastClosed: c0,
                            dangerCandidate: dangerCandidate,
                            reclaimed: reclaimed,
                            dangerHard: dangerHard,
                            netRr: netRr,
                            netPnlUsd: netPnlUsd);

                        // ======================================================================
                        // MICRO PROFIT PROTECT (R-based) - IMPROVE
                        // - baseRiskUsd ưu tiên riskUsd thật; nếu có plannedRiskUsd thì clamp (0.5x..2.0x)
                        // - NO-KILL ZONE: cấm MICRO TAKE close (0.50R) -> chuyển thành LOCK (giữ kèo)
                        // ======================================================================
                        if (hasEntry && canUseRR)
                        {
                            decimal plannedRiskUsd = GetPlannedRiskUsdFromConfig(symbol);

                            decimal baseRiskUsd;
                            if (riskUsd > 0m && plannedRiskUsd > 0m)
                            {
                                baseRiskUsd = Clamp(riskUsd, plannedRiskUsd * 0.50m, plannedRiskUsd * 2.00m);
                            }
                            else
                            {
                                baseRiskUsd = riskUsd > 0m ? riskUsd : plannedRiskUsd;
                            }

                            if (baseRiskUsd > 0m)
                            {
                                decimal microLockUsd = baseRiskUsd * MicroLockAtR;
                                decimal microTakeUsd = baseRiskUsd * MicroTakeAtR;

                                // MICRO TAKE
                                if (netPnlUsd >= microTakeUsd && netRr >= MicroMinNetRrToAct)
                                {
                                    if (!inNoKillZone)
                                    {
                                        await _notify.SendAsync(
                                            $"[{symbol}] MICRO TAKE (R): net={netPnlUsd:F4} >= {microTakeUsd:F4} ({MicroTakeAtR:F2}R of baseRisk={baseRiskUsd:F4}) → close NOW | mode={profile.Tag}");
                                        await _exchange.ClosePositionAsync(symbol, qty);
                                        await SafeCancelLeftoverProtectiveAsync(symbol);
                                        ClearDangerPending(symbol);
                                        return;
                                    }
                                    else
                                    {
                                        // NO-KILL: không close, chuyển sang lock SL (giữ TP)
                                        await _notify.SendAsync(
                                            $"[{symbol}] MICRO TAKE blocked by NO-KILL (plannedRR={plannedRR:F2}) → convert to LOCK (keep TP) | net={netPnlUsd:F4} baseRisk={baseRiskUsd:F4} | mode={profile.Tag}");
                                    }
                                }

                                // MICRO LOCK (hoặc NO-KILL fallback từ MICRO TAKE)
                                bool shouldMicroLock = (netPnlUsd >= microLockUsd && netRr >= MicroMinNetRrToAct)
                                    || (inNoKillZone && netPnlUsd >= microTakeUsd && netRr >= MicroMinNetRrToAct);

                                if (hasSL && IsValidStopLoss(sl, isLongPosition, entry) && shouldMicroLock)
                                {
                                    decimal targetSL;

                                    if (atr > 0m)
                                    {
                                        targetSL = isLongPosition
                                            ? (price - atr * MicroTrailAtrMult)
                                            : (price + atr * MicroTrailAtrMult);
                                    }
                                    else
                                    {
                                        targetSL = entry;
                                    }

                                    decimal feeBeBuffer = GetFeeBreakevenBufferPrice(estFeeUsd, absQty);
                                    if (feeBeBuffer > 0m)
                                    {
                                        if (isLongPosition) targetSL = Math.Max(targetSL, entry + feeBeBuffer);
                                        else targetSL = Math.Min(targetSL, entry - feeBeBuffer);
                                    }

                                    if (!IsSlTooCloseToPrice(price, targetSL, atr))
                                    {
                                        if (IsBetterStopLoss(targetSL, sl, isLongPosition)
                                            && CanUpdateTrailing(symbol, sl, targetSL, isLongPosition, atr))
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
                                                $"[{symbol}] MICRO LOCK (R): net={netPnlUsd:F4} lockAt={(netPnlUsd >= microTakeUsd ? "0.50R(no-kill)" : "0.25R")} baseRisk={baseRiskUsd:F4} → SL={Math.Round(sl, 6)} | mode={profile.Tag}");
                                        }
                                    }
                                }
                            }
                        }

                        // ===== TIME-STOP (smarter + NO-KILL ZONE) =====
                        if (!timeStopTriggered && timeStopAnchorUtc.HasValue)
                        {
                            int barsPassed = CountClosedBarsSince(timeStopAnchorUtc.Value, c0.OpenTime, tfMinutes);

                            bool timeStopConfirmBad = weakening || stall || nearBoundary || dangerConfirmed;

                            bool allowTimeStop = !inNoKillZone
                                || dangerConfirmed
                                || (nearBoundary && stall);

                            if (allowTimeStop && barsPassed >= profile.TimeStopBars && netRr < effTimeStopMinRR && timeStopConfirmBad)
                            {
                                timeStopTriggered = true;

                                await _notify.SendAsync(
                                    $"[{symbol}] TIME-STOP: {barsPassed} bars({tfMinutes}m) netRr={netRr:F2} < {effTimeStopMinRR:F2}R + confirmBad={timeStopConfirmBad} " +
                                    $"(stall={(stall ? "Y" : "N")} weak={(weakening ? "Y" : "N")} nearB={(nearBoundary ? "Y" : "N")} danger={(dangerConfirmed ? "Y" : "N")}) → close | " +
                                    $"pnl={pnlUsd:F4} fee~{estFeeUsd:F4} net={netPnlUsd:F4} risk={riskUsd:F4} plannedRR={(plannedRR > 0 ? plannedRR.ToString("F2") : "NA")} | mode={profile.Tag}");

                                await _exchange.ClosePositionAsync(symbol, qty);
                                await SafeCancelLeftoverProtectiveAsync(symbol);
                                ClearDangerPending(symbol);
                                return;
                            }
                        }

                        // ===== EARLY EXIT (fee-safe) - NO-KILL ZONE: skip =====
                        if (!inNoKillZone
                            && timeStopAnchorUtc.HasValue
                            && netPnlUsd >= minEarlyNetProfitUsd
                            && netRr >= profile.EarlyExitMinRR)
                        {
                            int barsPassed = CountClosedBarsSince(timeStopAnchorUtc.Value, c0.OpenTime, tfMinutes);

                            if (barsPassed >= profile.EarlyExitBars && (weakening || stall || (nearBoundary && !dangerConfirmed)))
                            {
                                await _notify.SendAsync(
                                    $"[{symbol}] EARLY EXIT (NET+FEE): bars={barsPassed} netRr={netRr:F2} net={netPnlUsd:F4} (pnl={pnlUsd:F4} fee~{estFeeUsd:F4} minNet={minEarlyNetProfitUsd:F4}) " +
                                    $"atr={(atr > 0 ? atr.ToString("F4") : "0")} stall={(stall ? "Y" : "N")} weak={(weakening ? "Y" : "N")} nearB={(nearBoundary ? "Y" : "N")} plannedRR={(plannedRR > 0 ? plannedRR.ToString("F2") : "NA")} → close | mode={profile.Tag}");

                                await _exchange.ClosePositionAsync(symbol, qty);
                                await SafeCancelLeftoverProtectiveAsync(symbol);
                                ClearDangerPending(symbol);
                                return;
                            }
                        }

                        // ===== Profit protect (ATR-based) =====
                        if (netRr >= effProtectAtRR
                            && netPnlUsd >= minProtectNetProfitUsd
                            && hasSL
                            && IsValidStopLoss(sl, isLongPosition, entry))
                        {
                            // Scalp plannedRR >= 1.10 => chỉ BE(+fee) + giữ TP
                            if (noAtrProtectForScalp)
                            {
                                decimal feeBeBuffer = GetFeeBreakevenBufferPrice(estFeeUsd, absQty);
                                decimal targetSL = isLongPosition ? (entry + feeBeBuffer) : (entry - feeBeBuffer);

                                if (!IsSlTooCloseToPrice(price, targetSL, atr))
                                {
                                    if (IsBetterStopLoss(targetSL, sl, isLongPosition)
                                        && CanUpdateTrailing(symbol, sl, targetSL, isLongPosition, atr))
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
                                            $"[{symbol}] PROTECT BE(+FEE) (ANTI-KILL): plannedRR={plannedRR:F2} scalp forbid ATR lock. netRr={netRr:F2} net={netPnlUsd:F4} → SL={Math.Round(sl, 6)} | mode={profile.Tag}");
                                    }
                                }

                                goto AFTER_PROTECT_BLOCK;
                            }

                            if (atr > 0m)
                            {
                                decimal atrMult = isScalp ? ScalpAtrMult : TrendAtrMult;

                                bool movedEnoughForProfitLock =
                                    isLongPosition
                                        ? price >= entry + atr * AtrToAllowProfitLock
                                        : price <= entry - atr * AtrToAllowProfitLock;

                                if (isScalp)
                                {
                                    bool movedMin = isLongPosition
                                        ? price >= entry + atr * RequireMoveBeforeTrailAtrFrac
                                        : price <= entry - atr * RequireMoveBeforeTrailAtrFrac;

                                    bool pullbackResumeOk = movedMin && HasPullbackThenResume(c0, c1, isLongPosition, entry, atr);

                                    if (!pullbackResumeOk)
                                    {
                                        decimal feeBeBuffer = GetFeeBreakevenBufferPrice(estFeeUsd, absQty);
                                        decimal beSl = isLongPosition ? (entry + feeBeBuffer) : (entry - feeBeBuffer);

                                        if (!IsSlTooCloseToPrice(price, beSl, atr)
                                            && IsBetterStopLoss(beSl, sl, isLongPosition)
                                            && CanUpdateTrailing(symbol, sl, beSl, isLongPosition, atr))
                                        {
                                            sl = beSl;
                                            lastSlTpCheckUtc = await UpdateStopLossAsync(symbol, beSl, isLongPosition, hasTP, tp, pos, lastSlTpCheckUtc);

                                            await _notify.SendAsync(
                                                $"[{symbol}] PROTECT BE(+FEE) (SCALP wait continuation): netRr={netRr:F2} net={netPnlUsd:F4} → SL={Math.Round(sl, 6)} | mode={profile.Tag}");
                                        }

                                        goto AFTER_PROTECT_BLOCK;
                                    }
                                }

                                decimal targetSL;

                                if (!movedEnoughForProfitLock)
                                {
                                    decimal beGuard = Math.Min(atr * 0.25m, riskPrice * 0.50m);

                                    targetSL = isLongPosition
                                        ? (entry - beGuard)
                                        : (entry + beGuard);

                                    if (isLongPosition) targetSL = Math.Min(targetSL, entry);
                                    else targetSL = Math.Max(targetSL, entry);
                                }
                                else
                                {
                                    targetSL = isLongPosition
                                        ? (price - atr * atrMult)
                                        : (price + atr * atrMult);
                                }

                                decimal feeBe = GetFeeBreakevenBufferPrice(estFeeUsd, absQty);
                                if (feeBe > 0m)
                                {
                                    if (isLongPosition)
                                    {
                                        if (targetSL >= entry) targetSL = Math.Max(targetSL, entry + feeBe);
                                    }
                                    else
                                    {
                                        if (targetSL <= entry) targetSL = Math.Min(targetSL, entry - feeBe);
                                    }
                                }

                                if (IsSlTooCloseToPrice(price, targetSL, atr))
                                    goto AFTER_PROTECT_BLOCK;

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
                                        $"[{symbol}] PROTECT ATR (NET+FEE): netRr={netRr:F2} net={netPnlUsd:F4} fee~{estFeeUsd:F4} minNet={minProtectNetProfitUsd:F4} atr={atr:F4} moved={(movedEnoughForProfitLock ? "Y" : "N")} plannedRR={(plannedRR > 0 ? plannedRR.ToString("F2") : "NA")} → SL={Math.Round(sl, 6)} | mode={profile.Tag}");
                                }
                            }
                            else
                            {
                                decimal targetSL = GetBreakEvenLockSL(entry, sl, riskPrice, isLongPosition, profile.BreakEvenBufferR);

                                if (isLongPosition) targetSL = Math.Min(targetSL, entry);
                                else targetSL = Math.Max(targetSL, entry);

                                decimal feeBeBuffer = GetFeeBreakevenBufferPrice(estFeeUsd, absQty);
                                if (feeBeBuffer > 0m)
                                {
                                    if (isLongPosition)
                                    {
                                        if (targetSL >= entry) targetSL = Math.Max(targetSL, entry + feeBeBuffer);
                                    }
                                    else
                                    {
                                        if (targetSL <= entry) targetSL = Math.Min(targetSL, entry - feeBeBuffer);
                                    }
                                }

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

                                    await _notify.SendAsync($"[{symbol}] PROTECT (fallback NET+FEE): netRr={netRr:F2} net={netPnlUsd:F4} plannedRR={(plannedRR > 0 ? plannedRR.ToString("F2") : "NA")} → SL={Math.Round(sl, 6)} | mode={profile.Tag}");
                                }
                            }
                        }

                    AFTER_PROTECT_BLOCK:

                        // ===== Quick take - NO-KILL: skip trừ danger =====
                        bool allowQuickTake = !inNoKillZone || dangerConfirmed;

                        if (allowQuickTake && netRr >= effQuickMinRR && netPnlUsd >= minQuickNetProfitUsd && weakening)
                        {
                            if (netRr >= effQuickGoodRR || dangerConfirmed || IsOppositeStrongCandle(c0, isLongPosition))
                            {
                                await _notify.SendAsync($"[{symbol}] QUICK TAKE (NET+FEE): netRr={netRr:F2} net={netPnlUsd:F4} (fee~{estFeeUsd:F4} minNet={minQuickNetProfitUsd:F4}) plannedRR={(plannedRR > 0 ? plannedRR.ToString("F2") : "NA")} → close | mode={profile.Tag}");
                                await _exchange.ClosePositionAsync(symbol, qty);
                                await SafeCancelLeftoverProtectiveAsync(symbol);
                                ClearDangerPending(symbol);
                                return;
                            }
                        }

                        // ===== Danger cut - CONFIRMED =====
                        if (netRr <= profile.DangerCutIfRRBelow && dangerConfirmed)
                        {
                            var absLossUsd = Math.Max(0m, -netPnlUsd);
                            if (absLossUsd >= profile.MinDangerCutAbsLossUsd)
                            {
                                await _notify.SendAsync($"[{symbol}] DANGER CUT (CONFIRMED): netRr={netRr:F2} loss={absLossUsd:F4} + dangerConfirmed → close | mode={profile.Tag}");
                                await _exchange.ClosePositionAsync(symbol, qty);
                                await SafeCancelLeftoverProtectiveAsync(symbol);
                                ClearDangerPending(symbol);
                                return;
                            }
                        }

                        // ===== Exit on boundary break nếu đã dương chút - CONFIRMED =====
                        if (dangerConfirmed && netRr >= 0.10m && netPnlUsd >= minBoundaryNetProfitUsd)
                        {
                            await _notify.SendAsync($"[{symbol}] EXIT ON BOUNDARY BREAK (CONFIRMED): netRr={netRr:F2} net={netPnlUsd:F4} (fee~{estFeeUsd:F4} minNet={minBoundaryNetProfitUsd:F4}) plannedRR={(plannedRR > 0 ? plannedRR.ToString("F2") : "NA")} → close | mode={profile.Tag}");
                            await _exchange.ClosePositionAsync(symbol, qty);
                            await SafeCancelLeftoverProtectiveAsync(symbol);
                            ClearDangerPending(symbol);
                            return;
                        }
                    }
                    else
                    {
                        // IMPROVE: không clear pending ngay khi candle thiếu (tránh reset confirm do fetch fail)
                        // chỉ clear nếu pending quá lâu
                        if (_pendingDangerSinceUtc.TryGetValue(symbol, out var sinceUtc))
                        {
                            if ((DateTime.UtcNow - sinceUtc) > DangerPendingMaxAge)
                                ClearDangerPending(symbol);
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
                            ClearDangerPending(symbol);
                            return;
                        }
                    }
                }
            }
            finally
            {
                ClearMonitoringPosition(symbol);
                ClearDangerPending(symbol);
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
                ClearDangerPending(symbol);
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
        //                       ATR HELPER
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

        private static bool IsStallByAtrRange(IReadOnlyList<Candle> candles, decimal atr, int lookbackBars, decimal rangeAtrFrac)
        {
            if (candles == null || candles.Count < lookbackBars + 3) return false;
            if (atr <= 0m) return false;

            int end = candles.Count - 2;
            int start = Math.Max(0, end - lookbackBars + 1);

            decimal maxHigh = decimal.MinValue;
            decimal minLow = decimal.MaxValue;

            int smallBodyCount = 0;
            int total = 0;

            for (int i = start; i <= end; i++)
            {
                var c = candles[i];
                maxHigh = Math.Max(maxHigh, c.High);
                minLow = Math.Min(minLow, c.Low);
                total++;

                decimal range = c.High - c.Low;
                if (range > 0m)
                {
                    decimal body = Math.Abs(c.Close - c.Open);
                    decimal bodyToRange = body / range;
                    if (bodyToRange <= StallSmallBodyToRangeMax)
                        smallBodyCount++;
                }
            }

            decimal totalRange = maxHigh - minLow;
            bool tightRange = totalRange <= atr * rangeAtrFrac;

            bool manySmallBodies = total > 0 && smallBodyCount >= Math.Max(2, (int)Math.Ceiling(total * 0.6m));

            return tightRange && manySmallBodies;
        }

        private static bool HasPullbackThenResume(Candle lastClosed, Candle prevClosed, bool isLong, decimal entry, decimal atr)
        {
            bool pullback = isLong ? (prevClosed.Close < prevClosed.Open) : (prevClosed.Close > prevClosed.Open);
            bool resume = isLong ? (lastClosed.Close > lastClosed.Open) : (lastClosed.Close < lastClosed.Open);

            if (!pullback || !resume) return false;

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

            if (isLong)
                return lastClosed.Close > prevClosed.Close;
            else
                return lastClosed.Close < prevClosed.Close;
        }

        private bool CanUpdateTrailing(string symbol, decimal currentSl, decimal targetSl, bool isLong, decimal atr)
        {
            var now = DateTime.UtcNow;
            if (_lastTrailingUpdateUtc.TryGetValue(symbol, out var lastUtc))
            {
                if ((now - lastUtc) < TimeSpan.FromSeconds(TrailingMinUpdateIntervalSec))
                    return false;
            }

            if (atr > 0m)
            {
                decimal minStep = atr * TrailingMinStepAtrFrac;
                decimal delta = Math.Abs(targetSl - currentSl);
                if (delta < minStep)
                    return false;
            }

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
        //                     UPDATE STOPLOSS (IMPROVED)
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

            await SafeCancelStopLossBothAsync(symbol);

            // IMPROVE: delay dài hơn để exchange sync (giảm case “mất SL”)
            await Task.Delay(400);

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

            // Place SL
            await _exchange.PlaceStopOnlyAsync(symbol, side, posSide, qty, newSL);

            // IMPROVE: verify SL tồn tại trên sàn, retry 1 lần nếu missing
            await Task.Delay(250);
            var check1 = await DetectManualSlTpAsync(symbol, isLong, currentPos.EntryPrice, currentPos);
            if (!check1.Sl.HasValue)
            {
                await _notify.SendAsync($"[{symbol}] WARN: SL not detected after place → retry place SL once.");
                await _exchange.PlaceStopOnlyAsync(symbol, side, posSide, qty, newSL);

                await Task.Delay(250);
                var check2 = await DetectManualSlTpAsync(symbol, isLong, currentPos.EntryPrice, currentPos);
                if (!check2.Sl.HasValue)
                    await _notify.SendAsync($"[{symbol}] WARN: SL still missing after retry. (exchange lag/format?)");
            }

            // Keep TP if needed
            if (hasTp && expectedTp.HasValue)
            {
                // force check TP quickly
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

            return lastSlTpCheckUtc;
        }

        // ============================================================
        //                      CLEANUP / SAFETY
        // ============================================================

        private async Task SafeCancelLeftoverProtectiveAsync(string symbol)
        {
            try { await SafeCancelAllOpenOrdersBothAsync(symbol); } catch { }
            try { await SafeCancelStopLossBothAsync(symbol); } catch { }
        }

        // ============================================================
        //                FEE MODEL (estimate + adaptive)
        // ============================================================

        private decimal GetEffectiveFeeRate(string symbol)
        {
            var s = _feeStatsBySymbol.GetOrAdd(symbol, _ => new FeeStats());
            if (s.EwmaRate < DefaultTakerFeeRate) s.EwmaRate = DefaultTakerFeeRate;
            return s.EwmaRate;
        }

        private async Task TryAdaptiveFeeUpdateOnCloseAsync(string symbol, decimal entry, decimal lastMarkPrice, decimal absQty)
        {
            try
            {
                if (entry <= 0m || lastMarkPrice <= 0m || absQty <= 0m)
                    return;

                decimal notional2Sides = (entry * absQty) + (lastMarkPrice * absQty);
                if (notional2Sides <= 0m) return;

                var fromUtc = DateTime.UtcNow - RealFeeLookback;
                var toUtc = DateTime.UtcNow;

                decimal? realCommissionAbsUsd = await TryGetRealCommissionAbsUsdAsync(symbol, fromUtc, toUtc);
                if (!realCommissionAbsUsd.HasValue || realCommissionAbsUsd.Value <= 0m)
                    return;

                decimal realizedRate = realCommissionAbsUsd.Value / notional2Sides;
                if (realizedRate <= 0m || realizedRate > 0.01m)
                    return;

                var stats = _feeStatsBySymbol.GetOrAdd(symbol, _ => new FeeStats());

                decimal old = stats.EwmaRate <= 0m ? DefaultTakerFeeRate : stats.EwmaRate;
                decimal updated = old + FeeEwmaAlpha * (realizedRate - old);

                stats.EwmaRate = Clamp(updated, DefaultTakerFeeRate, DefaultTakerFeeRate * 2.5m);
                stats.Samples++;
                stats.LastUpdateUtc = DateTime.UtcNow;

                await _notify.SendAsync(
                    $"[{symbol}] FEE ADAPT: realComm={realCommissionAbsUsd.Value:F6} notional2={notional2Sides:F2} rate={realizedRate:P4} ewma->{stats.EwmaRate:P4} samples={stats.Samples}");
            }
            catch
            {
                // silent
            }
        }

        private async Task<decimal?> TryGetRealCommissionAbsUsdAsync(string symbol, DateTime fromUtc, DateTime toUtc)
        {
            object? svc = _exchange;
            if (svc == null) return null;

            var type = svc.GetType();

            MethodInfo? mi = type.GetMethod("GetCommissionFromUserTradesAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (mi == null) return null;

            var ps = mi.GetParameters();

            if (ps.Length != 3
                || ps[0].ParameterType != typeof(string)
                || ps[1].ParameterType != typeof(DateTime)
                || ps[2].ParameterType != typeof(DateTime))
                return null;

            object? invokeResult = mi.Invoke(svc, new object?[] { symbol, fromUtc, toUtc });

            if (invokeResult is Task t)
            {
                await t.ConfigureAwait(false);

                object? resObj = null;
                var taskType = t.GetType();
                if (taskType.IsGenericType)
                    resObj = taskType.GetProperty("Result")?.GetValue(t);

                return ExtractCommissionAbsUsd(resObj);
            }

            return ExtractCommissionAbsUsd(invokeResult);
        }

        private static decimal? ExtractCommissionAbsUsd(object? obj)
        {
            if (obj == null) return null;

            if (obj is decimal d) return Math.Abs(d);
            if (obj is double dd) return (decimal)Math.Abs(dd);
            if (obj is float ff) return (decimal)Math.Abs(ff);

            var t = obj.GetType();
            var pComm = t.GetProperty("Commission", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (pComm != null)
            {
                var v = pComm.GetValue(obj);
                if (v is decimal cdec) return Math.Abs(cdec);
                if (v is double cdbl) return (decimal)Math.Abs(cdbl);
            }

            return null;
        }

        private static decimal GetFeeBreakevenBufferPrice(decimal estFeeUsd, decimal absQty)
        {
            if (estFeeUsd <= 0m || absQty <= 0m) return 0m;
            return (estFeeUsd * FeeBreakevenBufferMult) / absQty;
        }

        private static bool IsSlTooCloseToPrice(decimal price, decimal sl, decimal atr)
        {
            if (atr <= 0m || price <= 0m || sl <= 0m) return false;
            return Math.Abs(price - sl) < atr * MinSlDistanceAtrFrac;
        }

        private static decimal Clamp(decimal v, decimal min, decimal max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        private static int ParseIntervalMinutesSafe(string? frameTime)
        {
            if (string.IsNullOrWhiteSpace(frameTime)) return 15;

            string s = frameTime.Trim().ToLowerInvariant();

            try
            {
                if (s.EndsWith("m"))
                {
                    if (int.TryParse(s[..^1], out int m) && m > 0) return m;
                }
                if (s.EndsWith("h"))
                {
                    if (int.TryParse(s[..^1], out int h) && h > 0) return h * 60;
                }
            }
            catch { }

            return 15;
        }

        // ============================================================
        //  cancel BOTH normal + algo (best-effort via reflection)
        // ============================================================

        private async Task SafeCancelAllOpenOrdersBothAsync(string symbol)
        {
            try { await _exchange.CancelAllOpenOrdersAsync(symbol); } catch { }
            await TryInvokeCancelAlgoAsync(symbol, cancelStopsOnly: false);
        }

        private async Task SafeCancelStopLossBothAsync(string symbol)
        {
            await TryInvokeCancelNormalStopAsync(symbol);
            await TryInvokeCancelAlgoAsync(symbol, cancelStopsOnly: true);
        }

        private async Task TryInvokeCancelNormalStopAsync(string symbol)
        {
            try
            {
                object? svc = _exchange;
                if (svc == null) return;

                var type = svc.GetType();

                string[] candidates =
                {
                    "CancelStopLossOrdersAsync",
                    "CancelStopOrdersAsync",
                    "CancelAllStopOrdersAsync"
                };

                MethodInfo? mi = null;
                foreach (var name in candidates)
                {
                    mi = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (mi == null) continue;

                    var ps = mi.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(string))
                        break;

                    mi = null;
                }

                if (mi == null) return;

                object? res = mi.Invoke(svc, new object?[] { symbol });
                if (res is Task t) await t.ConfigureAwait(false);
            }
            catch
            {
                // silent
            }
        }

        private async Task TryInvokeCancelAlgoAsync(string symbol, bool cancelStopsOnly)
        {
            try
            {
                object? svc = _exchange;
                if (svc == null) return;

                var type = svc.GetType();

                string[] candidatesStops =
                {
                    "CancelStopLossAlgoOrdersAsync",
                    "CancelAlgoStopOrdersAsync",
                    "CancelAlgoStopLossOrdersAsync",
                    "CancelAlgoOrdersStopAsync",
                    "CancelAlgoOrdersSLAsync"
                };

                string[] candidatesAll =
                {
                    "CancelAllOpenAlgoOrdersAsync",
                    "CancelAllAlgoOrdersAsync",
                    "CancelAlgoOrdersAsync",
                    "CancelOpenAlgoOrdersAsync"
                };

                var names = cancelStopsOnly ? candidatesStops.Concat(candidatesAll) : candidatesAll;

                MethodInfo? mi = null;
                foreach (var name in names)
                {
                    mi = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (mi == null) continue;

                    var ps = mi.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(string))
                        break;

                    mi = null;
                }

                if (mi == null) return;

                object? res = mi.Invoke(svc, new object?[] { symbol });
                if (res is Task t) await t.ConfigureAwait(false);
            }
            catch
            {
                // silent
            }
        }

        // ============================================================
        // planned risk USD from appsettings (% risk)
        // ============================================================

        private decimal GetPlannedRiskUsdFromConfig(string symbol)
        {
            try
            {
                if (_botConfig == null) return 0m;

                decimal balance = 0m;
                try { balance = _botConfig.AccountBalance; } catch { }

                if (balance <= 0m)
                {
                    var pBal = _botConfig.GetType().GetProperty("AccountBalance", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                               ?? _botConfig.GetType().GetProperty("Balance", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (pBal != null)
                    {
                        var v = pBal.GetValue(_botConfig);
                        if (v is decimal d) balance = d;
                        else if (v is double dd) balance = (decimal)dd;
                    }
                }

                if (balance <= 0m) return 0m;

                var coinInfos = _botConfig.CoinInfos;
                if (coinInfos == null) return 0m;

                var coin = coinInfos.FirstOrDefault(x => string.Equals(x.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
                if (coin == null) return 0m;

                decimal pct = 0m;
                try { pct = coin.RiskPerTradePercent; } catch { }

                if (pct <= 0m)
                {
                    var pPct = coin.GetType().GetProperty("RiskPerTradePercent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                               ?? coin.GetType().GetProperty("RiskPercent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (pPct != null)
                    {
                        var v = pPct.GetValue(coin);
                        if (v is decimal d) pct = d;
                        else if (v is double dd) pct = (decimal)dd;
                    }
                }

                if (pct <= 0m) return 0m;

                return balance * (pct / 100m);
            }
            catch
            {
                return 0m;
            }
        }

        // ============================================================
        // MAJOR detection + Danger confirm bars
        // ============================================================

        private int GetDangerConfirmBars(string symbol)
        {
            return IsMajorSymbol(symbol) ? DangerConfirmBarsMajor : DangerConfirmBarsAlt;
        }

        private bool IsMajorSymbol(string symbol)
        {
            try
            {
                if (_botConfig?.CoinInfos == null) return false;

                var coin = _botConfig.CoinInfos.FirstOrDefault(x =>
                    string.Equals(x.Symbol, symbol, StringComparison.OrdinalIgnoreCase));

                if (coin == null) return false;

                try
                {
                    var v = coin.GetType().GetProperty("IsMajor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(coin);
                    if (v is bool b) return b;
                }
                catch { }

                try
                {
                    var p = coin.GetType().GetProperty("Major", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                         ?? coin.GetType().GetProperty("IsMain", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var v2 = p?.GetValue(coin);
                    if (v2 is bool b2) return b2;
                }
                catch { }

                if (symbol.Equals("BTCUSDT", StringComparison.OrdinalIgnoreCase)) return true;
                if (symbol.Equals("ETHUSDT", StringComparison.OrdinalIgnoreCase)) return true;

                return false;
            }
            catch
            {
                return false;
            }
        }

        private void ClearDangerPending(string symbol)
        {
            _pendingDangerSinceUtc.TryRemove(symbol, out _);
            _pendingDangerBars.TryRemove(symbol, out _);
            _pendingDangerLastClosedOpenTime.TryRemove(symbol, out _);
        }

        // ============================================================
        // DANGER CONFIRM (FIXED)
        // - Count từ candle danger đầu tiên (bars=1 ngay khi init)
        // - Confirm khi đủ needBars nến đã đóng liên tiếp dangerCandidate
        // ============================================================
        private async Task<bool> ApplyDangerConfirmAsync(
            string symbol,
            ModeProfile profile,
            Candle lastClosed,
            bool dangerCandidate,
            bool reclaimed,
            bool dangerHard,
            decimal netRr,
            decimal netPnlUsd)
        {
            if (!dangerCandidate || reclaimed)
            {
                if (_pendingDangerSinceUtc.ContainsKey(symbol))
                {
                    ClearDangerPending(symbol);

                    if (reclaimed)
                        await _notify.SendAsync($"[{symbol}] DANGER INVALIDATED (reclaim) → keep position | mode={profile.Tag}");
                }
                return false;
            }

            // HARD CUT: lỗ sâu thì cho cut ngay (tránh dump thật)
            if (dangerHard)
            {
                var absLossUsd = Math.Max(0m, -netPnlUsd);
                if (absLossUsd >= profile.MinDangerCutAbsLossUsd)
                {
                    await _notify.SendAsync($"[{symbol}] DANGER HARD (NET): netRr={netRr:F2} loss={absLossUsd:F4} → allow immediate cut | mode={profile.Tag}");
                    ClearDangerPending(symbol);
                    return true;
                }
            }

            int needBars = GetDangerConfirmBars(symbol);

            // init pending: bars=1 ngay tại candle danger đầu tiên
            if (!_pendingDangerSinceUtc.ContainsKey(symbol))
            {
                _pendingDangerSinceUtc[symbol] = DateTime.UtcNow;
                _pendingDangerBars[symbol] = 1;
                _pendingDangerLastClosedOpenTime[symbol] = lastClosed.OpenTime;

                if (needBars <= 1)
                {
                    await _notify.SendAsync($"[{symbol}] DANGER CONFIRMED immediately (needBars=1) | netRr={netRr:F2} | mode={profile.Tag}");
                    ClearDangerPending(symbol);
                    return true;
                }

                await _notify.SendAsync($"[{symbol}] DANGER CANDIDATE → wait confirm {needBars} closed bar(s) (major={(IsMajorSymbol(symbol) ? "Y" : "N")}) | netRr={netRr:F2} | mode={profile.Tag}");
                return false;
            }

            // timeout pending (safety)
            if (_pendingDangerSinceUtc.TryGetValue(symbol, out var sinceUtc))
            {
                if ((DateTime.UtcNow - sinceUtc) > DangerPendingMaxAge)
                {
                    ClearDangerPending(symbol);
                    await _notify.SendAsync($"[{symbol}] DANGER pending timeout → reset | mode={profile.Tag}");
                    return false;
                }
            }

            // chỉ tăng bar khi sang nến đã đóng mới
            if (_pendingDangerLastClosedOpenTime.TryGetValue(symbol, out var lastSeenOpen))
            {
                if (lastSeenOpen != lastClosed.OpenTime)
                {
                    _pendingDangerLastClosedOpenTime[symbol] = lastClosed.OpenTime;
                    _pendingDangerBars[symbol] = _pendingDangerBars.TryGetValue(symbol, out var b) ? (b + 1) : 1;
                }
            }
            else
            {
                _pendingDangerLastClosedOpenTime[symbol] = lastClosed.OpenTime;
                _pendingDangerBars[symbol] = 1;
            }

            int bars = _pendingDangerBars.TryGetValue(symbol, out var barsNow) ? barsNow : 0;

            if (bars >= needBars)
            {
                ClearDangerPending(symbol);
                await _notify.SendAsync($"[{symbol}] DANGER CONFIRMED: bars={bars}/{needBars} → eligible to cut/exit | mode={profile.Tag}");
                return true;
            }

            return false;
        }
    }
} 
