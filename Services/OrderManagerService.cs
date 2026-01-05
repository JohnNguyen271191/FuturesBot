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
    /// OrderManagerService - WINRATE + FLEX EXIT + MODE AWARE
    /// - ROI-based gates: theo % margin (có leverage) + fee-safe
    /// - Tất cả threshold dynamic lấy từ ModeProfile (không hardcode)
    ///
    /// NOTE:
    /// - Micro profit protect chỉ chạy cho Scalp (đỡ đóng sớm trend).
    /// - Trailing ATR chỉ cho phép khi đạt "AllowTrailing gate" (MinTrailStartRoi/MinTrailStartRR + fee-safe net).
    /// - "Chốt luôn nếu không ổn" dùng QuickTakeNotOk (dynamic).
    ///
    /// PATCH (NEW - anti "ăn non bị cắn pullback"):
    /// - Tách 2 giai đoạn rõ ràng:
    ///   (1) PROTECT BE(+fee) sớm khi đạt ProtectAtRR (không kéo sát theo giá)
    ///   (2) ATR TRAIL chỉ bật khi:
    ///       + đạt HardTrailStartRR (derive từ profile + plannedRR) HOẶC
    ///       + có ContinuationConfirmed (pullback->resume) trên MAIN TF
    ///     => tránh case chạm +0.6R rồi pullback quét SL -> còn +0.05R
    /// - ATR mult dynamic theo TF: TF càng nhỏ -> trail càng rộng (đỡ bị quét)
    ///
    /// PATCH (NEW):
    /// - "Không ổn thì chốt sớm" áp dụng cả khi ĐANG THUA:
    ///   + Nếu NotOkConfirm (weakening/stall/nearBoundary/dangerCandidate/dangerConfirmed)
    ///   + Và lỗ đã vượt ngưỡng "NotOkCutLoss" (derive từ DangerCutIfRRBelow + MinDangerCutAbsLossRoi)
    ///   => close sớm để tránh lỗ phình / bị kéo dài vô nghĩa.
    /// </summary>
    public class OrderManagerService
    {
        private readonly IFuturesExchangeService _exchange;
        private readonly SlackNotifierService _notify;
        private readonly BotConfig _botConfig;

        // ================= Polling / Throttle =====================
        private const int MonitorIntervalMs = 10000; // anti -1003
        private const int SlTpCheckEverySec = 30;
        private const int CandleFetchEverySec = 20;

        // Grace after fill (sàn sync algo orders chậm)
        private const int SlTpGraceAfterFillSec = 8;

        // ================= ATR (internal constant) =================
        private const int AtrPeriod = 14;
        private const decimal ScalpAtrMult = 0.8m;
        private const decimal TrendAtrMult = 1.2m;
        private const decimal AtrToAllowProfitLock = 1.0m;

        // ================= Weak/Impulse heuristics =================
        private const decimal ImpulseBodyToRangeMin = 0.65m;
        private const decimal ImpulseVolVsPrevMin = 1.20m;

        // ================= Early exit patterns =====================
        private const decimal StallRangeAtrFrac = 0.45m;
        private const decimal NearBoundaryAtrFrac = 0.20m;
        private const decimal StallSmallBodyToRangeMax = 0.45m;

        // ================= Fee model ===============================
        private const decimal DefaultTakerFeeRate = 0.0004m;
        private const decimal FeeEwmaAlpha = 0.20m;
        private static readonly TimeSpan RealFeeLookback = TimeSpan.FromMinutes(30);

        // ================= Micro scalp only ========================
        private const decimal MicroLockAtR = 0.25m;
        private const decimal MicroTakeAtR = 0.50m;
        private const decimal MicroMinNetRrToAct = 0.05m;
        private const decimal MicroTrailAtrMult = 0.45m;

        // ================= No-kill zone ============================
        private const decimal NoKillZonePlannedRR = 1.20m;

        // ================= Progress-to-TP allow protect ============
        private const decimal AllowProtectIfProgressToTpAtLeast = 0.25m;

        // ================= Major scalp ROI override (keep) ==========
        private const decimal MajorScalpMinProtectRoiOverride = 0.10m;
        private const decimal MajorScalpMinQuickTakeRoiOverride = 0.18m;

        // ================= Danger confirm (trend TF) ================
        private const int DangerConfirmBarsMajor = 2;
        private const int DangerConfirmBarsAlt = 1;
        private const decimal DangerHardCutNetRr = -0.80m;
        private const decimal ReclaimAtrFrac = 0.15m;
        private static readonly TimeSpan DangerPendingMaxAge = TimeSpan.FromMinutes(10);

        // ================= Cross-instance tracking ==================
        private static readonly ConcurrentDictionary<string, bool> _monitoringLimit = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, bool> _monitoringPosition = new(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<string, DateTime> _lastTrailingUpdateUtc = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, decimal> _lastTrailingSl = new(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<string, DateTime> _pendingDangerSinceUtc = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, int> _pendingDangerBars = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, DateTime> _pendingDangerLastClosedOpenTime = new(StringComparer.OrdinalIgnoreCase);

        // Manual attach cross-instance throttle
        private static readonly TimeSpan ManualAttachThrottle = TimeSpan.FromMinutes(2);
        private static readonly ConcurrentDictionary<string, DateTime> _lastManualAttachUtc = new(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<string, FeeStats> _feeStatsBySymbol
            = new(StringComparer.OrdinalIgnoreCase);

        public OrderManagerService(IFuturesExchangeService exchange, SlackNotifierService notify, BotConfig config)
        {
            _exchange = exchange;
            _notify = notify;
            _botConfig = config;
        }

        public async Task ClearMonitoringTrigger(string symbol)
        {
            if (_monitoringPosition.ContainsKey(symbol) || _monitoringLimit.ContainsKey(symbol))
            {
                _monitoringLimit.TryRemove(symbol, out _);
                _monitoringPosition.TryRemove(symbol, out _);
                ClearDangerPending(symbol);
                await _notify.SendAsync($"[{symbol}] đã clear monitoring.");
            }
        }

        // ============================================================
        // LIMIT MONITOR (MODE AWARE)
        // ============================================================

        public async Task MonitorLimitOrderAsync(TradeSignal signal)
        {
            string symbol = signal.Symbol;
            bool isLong = signal.Type == SignalType.Long;

            var profile = ModeProfile.For(signal.Mode);

            if (_monitoringPosition.ContainsKey(symbol) || !_monitoringLimit.TryAdd(symbol, true))
            {
                await _notify.SendAsync($"[{symbol}] LIMIT: already monitoring → skip.");
                return;
            }

            await _notify.SendAsync($"[{symbol}] Monitor LIMIT start (mode={profile.Tag}, timeout={profile.LimitTimeout.TotalMinutes:F0}m)");

            var startTime = DateTime.UtcNow;

            try
            {
                while (true)
                {
                    await Task.Delay(MonitorIntervalMs);

                    if ((DateTime.UtcNow - startTime) > profile.LimitTimeout)
                    {
                        await _notify.SendAsync($"[{symbol}] LIMIT timeout → cancel open orders & stop.");
                        await _exchange.CancelAllOpenOrdersAsync(symbol);
                        return;
                    }

                    var openOrders = await _exchange.GetOpenOrdersAsync(symbol);
                    var pos = await _exchange.GetPositionAsync(symbol);

                    bool hasPosition = pos.PositionAmt != 0;
                    bool hasOpenOrder = openOrders != null && openOrders.Any();

                    var coinInfo = _botConfig.Futures.Coins.FirstOrDefault(i => i.Symbol.Equals(symbol));
                    if (coinInfo == null)
                    {
                        await _notify.SendAsync($"[{symbol}] CoinInfo not found in config.");
                        return;
                    }

                    if (hasPosition)
                    {
                        await _notify.SendAsync($"[{symbol}] LIMIT filled → switch to POSITION monitor (mode={profile.Tag})");
                        _monitoringLimit.TryRemove(symbol, out _);
                        _ = MonitorPositionAsync(signal, coinInfo);
                        return;
                    }

                    if (!hasOpenOrder)
                    {
                        await _notify.SendAsync($"[{symbol}] LIMIT order disappeared → stop LIMIT monitor.");
                        return;
                    }

                    var candles = await _exchange.GetRecentCandlesAsync(symbol, coinInfo.MainTimeFrame, 80);
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
                        await _notify.SendAsync($"[{symbol}] LIMIT setup broke → cancel open orders.");
                        await _exchange.CancelAllOpenOrdersAsync(symbol);
                        return;
                    }
                }
            }
            finally
            {
                _monitoringLimit.TryRemove(symbol, out _);
            }
        }

        // ============================================================
        // POSITION MONITOR (FLEX EXIT)
        // ============================================================

        public async Task MonitorPositionAsync(TradeSignal signal, CoinInfo coinInfo)
        {
            string symbol = signal.Symbol;
            var profile = ModeProfile.For(signal.Mode);

            if (!_monitoringPosition.TryAdd(symbol, true))
            {
                await _notify.SendAsync($"[{symbol}] POSITION: already monitoring → skip.");
                return;
            }

            _monitoringLimit.TryRemove(symbol, out _);

            decimal entry = signal.EntryPrice ?? 0m;
            decimal sl = signal.StopLoss ?? 0m;
            decimal tp = signal.TakeProfit ?? 0m;

            bool autoTpPlaced = false;

            DateTime lastSlTpCheckUtc = DateTime.MinValue;
            DateTime lastCandleFetchUtc = DateTime.MinValue;
            IReadOnlyList<Candle>? cachedCandles = null;

            DateTime lastTrendFetchUtc = DateTime.MinValue;
            IReadOnlyList<Candle>? trendCandles = null;

            var positionMonitorStartedUtc = DateTime.UtcNow;

            DateTime? timeStopAnchorUtc = null;

            decimal lastKnownAbsQty = 0m;
            decimal lastKnownMarkPrice = 0m;
            decimal lastKnownEntry = 0m;

            int tfMinutes = ParseIntervalMinutesSafe(coinInfo.MainTimeFrame);

            await _notify.SendAsync($"[{symbol}] Monitor POSITION start (mode={profile.Tag}) | timeStopBars={profile.TimeStopBars}x{tfMinutes}m | trendTF={coinInfo.TrendTimeFrame}");

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
                        await _exchange.CancelAllOpenOrdersAsync(symbol);
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

                    // =================== Fetch MAIN candles (throttle) ===================
                    if ((DateTime.UtcNow - lastCandleFetchUtc) >= TimeSpan.FromSeconds(CandleFetchEverySec))
                    {
                        cachedCandles = await _exchange.GetRecentCandlesAsync(symbol, coinInfo.MainTimeFrame, 120);
                        lastCandleFetchUtc = DateTime.UtcNow;
                    }

                    // =================== Fetch TREND candles (throttle) ===================
                    if ((DateTime.UtcNow - lastTrendFetchUtc) >= TimeSpan.FromSeconds(CandleFetchEverySec))
                    {
                        if (!string.IsNullOrWhiteSpace(coinInfo.TrendTimeFrame))
                            trendCandles = await _exchange.GetRecentCandlesAsync(symbol, coinInfo.TrendTimeFrame, 160);
                        lastTrendFetchUtc = DateTime.UtcNow;
                    }

                    // =================== Compute PnL / RR / FEE ===================
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

                    // =================== ROI (margin-based) ===================
                    decimal leverage = coinInfo.Leverage;
                    decimal initialMarginUsd = 0m;
                    decimal roi = 0m;        // +0.30 => +30%
                    decimal absLossRoi = 0m; // abs(-roi)

                    if (hasEntry && absQty > 0m && leverage > 0m)
                    {
                        decimal notional = entry * absQty;
                        if (notional > 0m)
                        {
                            initialMarginUsd = notional / leverage;
                            if (initialMarginUsd > 0m)
                            {
                                roi = netPnlUsd / initialMarginUsd;
                                absLossRoi = Math.Max(0m, -netPnlUsd) / initialMarginUsd;
                            }
                        }
                    }

                    // ===== plannedRR =====
                    decimal plannedRR = 0m;
                    if (hasEntry && hasSL && hasTP && riskPrice > 0m && IsValidTakeProfit(tp, isLongPosition, entry))
                    {
                        decimal rewardPrice = isLongPosition ? (tp - entry) : (entry - tp);
                        if (rewardPrice > 0m)
                            plannedRR = rewardPrice / riskPrice;
                    }

                    // ===== progress-to-TP =====
                    decimal progressToTp = 0m; // 0..2
                    if (hasEntry && hasTP && IsValidTakeProfit(tp, isLongPosition, entry))
                    {
                        decimal total = isLongPosition ? (tp - entry) : (entry - tp);
                        decimal moved = isLongPosition ? (price - entry) : (entry - price);
                        if (total > 0m)
                            progressToTp = Clamp(moved / total, 0m, 2m);
                    }

                    bool isScalp = signal.Mode == TradeMode.Scalp;
                    bool enableMicro = isScalp;
                    bool inNoKillZone = plannedRR >= NoKillZonePlannedRR;

                    // =================== Dynamic thresholds (profile-based) ===================
                    decimal effProtectAtRR = profile.ProtectAtRR;
                    decimal effQuickMinRR = profile.QuickTakeMinRR;
                    decimal effQuickGoodRR = profile.QuickTakeGoodRR;
                    decimal effTimeStopMinRR = profile.TimeStopMinRR;

                    // optional: soften thresholds by plannedRR (still dynamic)
                    if (plannedRR > 0.25m)
                    {
                        effProtectAtRR = Clamp(Math.Min(profile.ProtectAtRR, plannedRR * 0.30m), 0.15m, profile.ProtectAtRR);
                        effQuickMinRR = Clamp(Math.Min(profile.QuickTakeMinRR, plannedRR * 0.35m), 0.20m, profile.QuickTakeMinRR);
                        effQuickGoodRR = Clamp(Math.Min(profile.QuickTakeGoodRR, plannedRR * 0.55m), effQuickMinRR + 0.05m, profile.QuickTakeGoodRR);
                        effTimeStopMinRR = Clamp(Math.Min(profile.TimeStopMinRR, plannedRR * 0.30m), 0.20m, profile.TimeStopMinRR);
                    }

                    // =================== Fee-safe gates (dynamic from profile) ===================
                    decimal minProtectNetProfitUsd = estFeeUsd * profile.ProtectMinNetProfitVsFeeMult;
                    decimal minQuickNetProfitUsd = estFeeUsd * profile.QuickMinNetProfitVsFeeMult;
                    decimal minEarlyNetProfitUsd = estFeeUsd * profile.EarlyExitMinNetProfitVsFeeMult;
                    decimal minBoundaryNetProfitUsd = estFeeUsd * profile.BoundaryExitMinNetProfitVsFeeMult;

                    // =================== ROI override (Major scalp) ===================
                    decimal effMinProtectRoi = profile.MinProtectRoi;
                    decimal effMinQuickTakeRoi = profile.MinQuickTakeRoi;

                    if (isScalp && coinInfo.IsMajor)
                    {
                        effMinProtectRoi = Math.Min(effMinProtectRoi, MajorScalpMinProtectRoiOverride);
                        effMinQuickTakeRoi = Math.Min(effMinQuickTakeRoi, MajorScalpMinQuickTakeRoiOverride);
                    }

                    // ===== allow early protect by progress-to-TP =====
                    bool allowProtectByProgress = progressToTp >= AllowProtectIfProgressToTpAtLeast;

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

                    // =================== SL hit (fallback) ===================
                    if (!inGrace && hasEntry && hasSL && IsValidStopLoss(sl, isLongPosition, entry))
                    {
                        if ((isLongPosition && price <= sl) || (!isLongPosition && price >= sl))
                        {
                            var det = await DetectManualSlTpAsync(symbol, isLongPosition, entry, pos);
                            if (!det.Sl.HasValue)
                            {
                                await _notify.SendAsync($"[{symbol}] SL touched but missing on exchange → force close.");
                                await _exchange.ClosePositionAsync(symbol, qty);
                            }

                            await _exchange.CancelAllOpenOrdersAsync(symbol);
                            ClearDangerPending(symbol);
                            return;
                        }
                    }

                    // =================== FLEX EXIT BLOCK ===================
                    if (!inGrace && cachedCandles != null && cachedCandles.Count >= 30 && hasEntry && canUseRR)
                    {
                        var c0 = cachedCandles[^2];
                        var c1 = cachedCandles[^3];

                        if (!timeStopAnchorUtc.HasValue)
                            timeStopAnchorUtc = c0.OpenTime;

                        decimal ema34 = ComputeEmaLast(cachedCandles, 34);
                        decimal ema89 = ComputeEmaLast(cachedCandles, 89);
                        decimal ema200 = ComputeEmaLast(cachedCandles, 200);

                        decimal boundary = isLongPosition
                            ? GetDynamicBoundaryForLong(entry, ema34, ema89, ema200)
                            : GetDynamicBoundaryForShort(entry, ema34, ema89, ema200);

                        decimal atr = ComputeAtr(cachedCandles, AtrPeriod);

                        bool weakening = IsWeakeningAfterMove(cachedCandles, isLongPosition);
                        bool stall = atr > 0m && IsStallByAtrRange(cachedCandles, atr, profile.EarlyExitBars, StallRangeAtrFrac);
                        bool nearBoundary = atr > 0m && boundary > 0m && Math.Abs(c0.Close - boundary) <= atr * NearBoundaryAtrFrac;

                        // ===================== TREND TF STATE =====================
                        bool trendValid = true;
                        Candle? t0 = null;
                        Candle? t1 = null;
                        decimal trendBoundary = 0m;

                        if (trendCandles != null && trendCandles.Count >= 30)
                        {
                            t0 = trendCandles[^2];
                            t1 = trendCandles[^3];

                            var tEma34 = ComputeEmaLast(trendCandles, 34);
                            var tEma89 = ComputeEmaLast(trendCandles, 89);
                            var tEma200 = ComputeEmaLast(trendCandles, 200);

                            trendBoundary = isLongPosition
                                ? GetDynamicBoundaryForLong(entry, tEma34, tEma89, tEma200)
                                : GetDynamicBoundaryForShort(entry, tEma34, tEma89, tEma200);

                            trendValid = IsTrendStillValid(trendCandles, isLongPosition, tEma89);
                        }

                        // ===================== DANGER CANDIDATE =====================
                        bool dangerCandidateMainImpulse = IsDangerImpulseReverse(c0, c1, isLongPosition);
                        bool dangerCandidateTrendBreak =
                            (t0 != null && trendBoundary > 0m && IsBoundaryBroken(t0.Close, trendBoundary, isLongPosition, profile.EmaBreakTolerance));

                        bool dangerCandidate = dangerCandidateMainImpulse || dangerCandidateTrendBreak;

                        bool reclaimed = false;
                        if (atr > 0m && trendBoundary > 0m && t0 != null)
                        {
                            reclaimed = isLongPosition
                                ? t0.Close >= trendBoundary + atr * ReclaimAtrFrac
                                : t0.Close <= trendBoundary - atr * ReclaimAtrFrac;
                        }

                        bool dangerHard = netRr <= DangerHardCutNetRr;

                        bool dangerConfirmed = await ApplyDangerConfirmAsync(
                            profile,
                            t0?.OpenTime ?? c0.OpenTime,
                            dangerCandidate,
                            reclaimed,
                            dangerHard,
                            netRr,
                            netPnlUsd,
                            coinInfo);

                        // ======================================================================
                        // (A) QUICK TAKE IF NOT OK ✅
                        // ======================================================================
                        bool quickNotOkProfitOk =
                            netPnlUsd >= estFeeUsd * profile.QuickTakeNotOkMinNetProfitVsFeeMult &&
                            netRr >= profile.QuickTakeNotOkMinRR &&
                            (initialMarginUsd <= 0m || roi >= profile.QuickTakeNotOkMinRoi);

                        bool notOkConfirm = weakening || stall || nearBoundary || dangerCandidate || dangerConfirmed;

                        if (!inNoKillZone && quickNotOkProfitOk && notOkConfirm)
                        {
                            await _notify.SendAsync(
                                $"[{symbol}] QUICK TAKE IF NOT OK: netRr={netRr:F2} roi={(initialMarginUsd > 0 ? (roi * 100m).ToString("F1") + "%" : "NA")} " +
                                $"net={netPnlUsd:F4} fee~{estFeeUsd:F4} weak={(weakening ? "Y" : "N")} stall={(stall ? "Y" : "N")} nearB={(nearBoundary ? "Y" : "N")} danger={(dangerConfirmed ? "Y" : "N")} → close | mode={profile.Tag}");

                            await _exchange.ClosePositionAsync(symbol, qty);
                            await _exchange.CancelAllOpenOrdersAsync(symbol);
                            ClearDangerPending(symbol);
                            return;
                        }

                        // ======================================================================
                        // (A2) CUT LOSS IF NOT OK ✅
                        // ======================================================================
                        int barsSinceAnchor = timeStopAnchorUtc.HasValue
                            ? CountClosedBarsSince(timeStopAnchorUtc.Value, c0.OpenTime, tfMinutes)
                            : 0;

                        decimal notOkCutNetRr = Clamp(profile.DangerCutIfRRBelow * 0.60m, -0.60m, -0.12m);
                        decimal notOkCutAbsLossRoi = Clamp(profile.MinDangerCutAbsLossRoi * 0.60m, 0.02m, profile.MinDangerCutAbsLossRoi);

                        bool notOkLossGate =
                            notOkConfirm &&
                            (barsSinceAnchor >= 1) &&
                            (netRr <= notOkCutNetRr) &&
                            (
                                initialMarginUsd <= 0m
                                || absLossRoi >= notOkCutAbsLossRoi
                            );

                        if (!inNoKillZone && notOkLossGate)
                        {
                            await _notify.SendAsync(
                                $"[{symbol}] CUT LOSS IF NOT OK: netRr={netRr:F2} <= {notOkCutNetRr:F2} " +
                                $"lossRoi={(initialMarginUsd > 0 ? (absLossRoi * 100m).ToString("F1") + "%" : "NA")} >= {(initialMarginUsd > 0 ? (notOkCutAbsLossRoi * 100m).ToString("F1") + "%" : "NA")} " +
                                $"bars={barsSinceAnchor} weak={(weakening ? "Y" : "N")} stall={(stall ? "Y" : "N")} nearB={(nearBoundary ? "Y" : "N")} dangerCand={(dangerCandidate ? "Y" : "N")} dangerConf={(dangerConfirmed ? "Y" : "N")} " +
                                $"net={netPnlUsd:F4} fee~{estFeeUsd:F4} → close | mode={profile.Tag}");

                            await _exchange.ClosePositionAsync(symbol, qty);
                            await _exchange.CancelAllOpenOrdersAsync(symbol);
                            ClearDangerPending(symbol);
                            return;
                        }

                        // ======================================================================
                        // (B) MICRO PROFIT (SCALP ONLY)
                        // ======================================================================
                        if (enableMicro && hasEntry && canUseRR)
                        {
                            decimal plannedRiskUsd = _botConfig.AccountBalance * (coinInfo.RiskPerTradePercent / 100m);

                            decimal baseRiskUsd;
                            if (riskUsd > 0m && plannedRiskUsd > 0m)
                                baseRiskUsd = Clamp(riskUsd, plannedRiskUsd * 0.50m, plannedRiskUsd * 2.00m);
                            else
                                baseRiskUsd = riskUsd > 0m ? riskUsd : plannedRiskUsd;

                            if (baseRiskUsd > 0m)
                            {
                                decimal microLockUsd = baseRiskUsd * MicroLockAtR;
                                decimal microTakeUsd = baseRiskUsd * MicroTakeAtR;

                                if (!inNoKillZone && netPnlUsd >= microTakeUsd && netRr >= MicroMinNetRrToAct)
                                {
                                    await _notify.SendAsync(
                                        $"[{symbol}] MICRO TAKE: net={netPnlUsd:F4} >= {microTakeUsd:F4} ({MicroTakeAtR:F2}R) → close | mode={profile.Tag}");
                                    await _exchange.ClosePositionAsync(symbol, qty);
                                    await _exchange.CancelAllOpenOrdersAsync(symbol);
                                    ClearDangerPending(symbol);
                                    return;
                                }

                                bool shouldMicroLock = netPnlUsd >= microLockUsd && netRr >= MicroMinNetRrToAct;

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

                                    decimal feeBeBuffer = GetFeeBreakevenBufferPrice(estFeeUsd, absQty, profile.FeeBreakevenBufferMult);
                                    if (feeBeBuffer > 0m)
                                    {
                                        if (isLongPosition) targetSL = Math.Max(targetSL, entry + feeBeBuffer);
                                        else targetSL = Math.Min(targetSL, entry - feeBeBuffer);
                                    }

                                    if (!IsSlTooCloseToPrice(price, targetSL, atr, profile.MinSlDistanceAtrFrac)
                                        && IsBetterStopLoss(targetSL, sl, isLongPosition)
                                        && CanUpdateTrailing(symbol, sl, targetSL, atr, profile))
                                    {
                                        var oldSl = sl;

                                        var (newLastCheck, slDetected) = await UpdateStopLossAsync(
                                            symbol,
                                            newSL: targetSL,
                                            isLong: isLongPosition,
                                            currentPos: pos,
                                            lastSlTpCheckUtc: lastSlTpCheckUtc);

                                        lastSlTpCheckUtc = newLastCheck;

                                        if (slDetected)
                                        {
                                            sl = targetSL;
                                            CommitTrailing(symbol, targetSL);
                                            await _notify.SendAsync(
                                                $"[{symbol}] MICRO LOCK: net={netPnlUsd:F4} lockAt={MicroLockAtR:F2}R → SL={Math.Round(sl, 6)} | mode={profile.Tag}");
                                        }
                                        else
                                        {
                                            sl = oldSl;
                                        }
                                    }
                                }
                            }
                        }

                        // ======================================================================
                        // (C) TIME-STOP
                        // ======================================================================
                        if (timeStopAnchorUtc.HasValue)
                        {
                            int barsPassed = CountClosedBarsSince(timeStopAnchorUtc.Value, c0.OpenTime, tfMinutes);

                            bool confirmBad = weakening || stall || nearBoundary || dangerConfirmed;
                            bool allowTimeStop = (!trendValid) || dangerConfirmed;

                            if (allowTimeStop && barsPassed >= profile.TimeStopBars && netRr < effTimeStopMinRR && confirmBad)
                            {
                                await _notify.SendAsync(
                                    $"[{symbol}] TIME-STOP: bars={barsPassed} netRr={netRr:F2} < {effTimeStopMinRR:F2}R confirmBad={confirmBad} " +
                                    $"net={netPnlUsd:F4} fee~{estFeeUsd:F4} roi={(initialMarginUsd > 0 ? (roi * 100m).ToString("F1") + "%" : "NA")} → close | mode={profile.Tag}");

                                await _exchange.ClosePositionAsync(symbol, qty);
                                await _exchange.CancelAllOpenOrdersAsync(symbol);
                                ClearDangerPending(symbol);
                                return;
                            }
                        }

                        // ======================================================================
                        // (D) EARLY EXIT
                        // ======================================================================
                        if (!inNoKillZone
                            && timeStopAnchorUtc.HasValue
                            && netPnlUsd >= minEarlyNetProfitUsd
                            && netRr >= profile.EarlyExitMinRR
                            && (initialMarginUsd <= 0m || roi >= profile.EarlyExitMinRoi)
                            && (!trendValid || dangerConfirmed))
                        {
                            int barsPassed = CountClosedBarsSince(timeStopAnchorUtc.Value, c0.OpenTime, tfMinutes);

                            if (barsPassed >= profile.EarlyExitBars && (weakening || stall || (nearBoundary && !dangerConfirmed)))
                            {
                                await _notify.SendAsync(
                                    $"[{symbol}] EARLY EXIT: bars={barsPassed} netRr={netRr:F2} roi={(initialMarginUsd > 0 ? (roi * 100m).ToString("F1") + "%" : "NA")} " +
                                    $"net={netPnlUsd:F4} (fee~{estFeeUsd:F4} minNet={minEarlyNetProfitUsd:F4}) " +
                                    $"stall={(stall ? "Y" : "N")} weak={(weakening ? "Y" : "N")} nearB={(nearBoundary ? "Y" : "N")} trendValid={(trendValid ? "Y" : "N")} → close | mode={profile.Tag}");

                                await _exchange.ClosePositionAsync(symbol, qty);
                                await _exchange.CancelAllOpenOrdersAsync(symbol);
                                ClearDangerPending(symbol);
                                return;
                            }
                        }

                        // ======================================================================
                        // (E) PROFIT PROTECT (BE / ATR trailing) — anti "ăn non bị cắn pullback"
                        // ======================================================================
                        bool roiOkForProtect = (initialMarginUsd <= 0m) || (roi >= effMinProtectRoi) || allowProtectByProgress;

                        // Gate 1 (profile-based)
                        bool allowAtrTrailingGate =
                            (initialMarginUsd <= 0m || roi >= profile.MinTrailStartRoi || netRr >= profile.MinTrailStartRR) &&
                            (netPnlUsd >= estFeeUsd * profile.TrailMinNetProfitVsFeeMult);

                        // Gate 2 (hard start RR derived, no new config)
                        // - Trend: thường cần >= ~1R mới trail
                        // - Scalp: cho sớm hơn nhưng vẫn tránh bị quét pullback => >= ~0.8R
                        decimal derivedHardStart = DeriveHardTrailStartRR(profile, plannedRR, isScalp);

                        bool reachedHardTrailStart = netRr >= derivedHardStart;

                        // Gate 3 (pattern-based continuation on MAIN TF): pullback -> resume
                        bool continuationConfirmed = IsPullbackResumeContinuation(cachedCandles, isLongPosition, atr);

                        bool allowAtrTrailingFinal = allowAtrTrailingGate && (reachedHardTrailStart || continuationConfirmed);

                        if (netRr >= effProtectAtRR
                            && netPnlUsd >= minProtectNetProfitUsd
                            && roiOkForProtect
                            && hasSL
                            && IsValidStopLoss(sl, isLongPosition, entry))
                        {
                            // If not allowed to trail => BE(+fee) only
                            if (!allowAtrTrailingFinal)
                            {
                                decimal feeBeBuffer = GetFeeBreakevenBufferPrice(estFeeUsd, absQty, profile.FeeBreakevenBufferMult);
                                decimal beSl = isLongPosition ? (entry + feeBeBuffer) : (entry - feeBeBuffer);

                                if (!IsSlTooCloseToPrice(price, beSl, atr, profile.MinSlDistanceAtrFrac)
                                    && IsBetterStopLoss(beSl, sl, isLongPosition)
                                    && CanUpdateTrailing(symbol, sl, beSl, atr, profile))
                                {
                                    var oldSl = sl;

                                    var (newLastCheck, slDetected) = await UpdateStopLossAsync(
                                        symbol,
                                        newSL: beSl,
                                        isLong: isLongPosition,
                                        currentPos: pos,
                                        lastSlTpCheckUtc: lastSlTpCheckUtc);

                                    lastSlTpCheckUtc = newLastCheck;

                                    if (slDetected)
                                    {
                                        sl = beSl;
                                        CommitTrailing(symbol, beSl);
                                        await _notify.SendAsync(
                                            $"[{symbol}] PROTECT BE(+FEE) (WAIT TRAIL): netRr={netRr:F2} hardStart={derivedHardStart:F2} cont={(continuationConfirmed ? "Y" : "N")} " +
                                            $"net={netPnlUsd:F4} allowTrail=N → SL={Math.Round(sl, 6)} | mode={profile.Tag}");
                                    }
                                    else sl = oldSl;
                                }

                                goto AFTER_PROTECT;
                            }

                            // ATR trailing allowed (final)
                            if (atr > 0m)
                            {
                                decimal atrMultBase = isScalp ? ScalpAtrMult : TrendAtrMult;
                                decimal atrMult = GetAtrTrailMultByTfMinutes(tfMinutes, atrMultBase);

                                bool movedEnoughForProfitLock =
                                    isLongPosition
                                        ? price >= entry + atr * AtrToAllowProfitLock
                                        : price <= entry - atr * AtrToAllowProfitLock;

                                decimal targetSL;

                                if (!movedEnoughForProfitLock)
                                {
                                    decimal feeBeBuffer = GetFeeBreakevenBufferPrice(estFeeUsd, absQty, profile.FeeBreakevenBufferMult);
                                    targetSL = isLongPosition ? (entry + feeBeBuffer) : (entry - feeBeBuffer);
                                }
                                else
                                {
                                    targetSL = isLongPosition
                                        ? (price - atr * atrMult)
                                        : (price + atr * atrMult);

                                    decimal feeBeBuffer = GetFeeBreakevenBufferPrice(estFeeUsd, absQty, profile.FeeBreakevenBufferMult);
                                    if (feeBeBuffer > 0m)
                                    {
                                        if (isLongPosition) targetSL = Math.Max(targetSL, entry + feeBeBuffer);
                                        else targetSL = Math.Min(targetSL, entry - feeBeBuffer);
                                    }
                                }

                                if (!IsSlTooCloseToPrice(price, targetSL, atr, profile.MinSlDistanceAtrFrac)
                                    && IsBetterStopLoss(targetSL, sl, isLongPosition)
                                    && CanUpdateTrailing(symbol, sl, targetSL, atr, profile))
                                {
                                    var oldSl = sl;

                                    var (newLastCheck, slDetected) = await UpdateStopLossAsync(
                                        symbol,
                                        newSL: targetSL,
                                        isLong: isLongPosition,
                                        currentPos: pos,
                                        lastSlTpCheckUtc: lastSlTpCheckUtc);

                                    lastSlTpCheckUtc = newLastCheck;

                                    if (slDetected)
                                    {
                                        sl = targetSL;
                                        CommitTrailing(symbol, targetSL);
                                        await _notify.SendAsync(
                                            $"[{symbol}] PROTECT ATR: netRr={netRr:F2} hardStart={derivedHardStart:F2} cont={(continuationConfirmed ? "Y" : "N")} " +
                                            $"net={netPnlUsd:F4} fee~{estFeeUsd:F4} atr={atr:F4} mult={atrMult:F2} → SL={Math.Round(sl, 6)} | mode={profile.Tag}");
                                    }
                                    else sl = oldSl;
                                }
                            }
                            else
                            {
                                decimal targetSL = GetBreakEvenLockSL(entry, sl, riskPrice, isLongPosition, profile.BreakEvenBufferR);

                                decimal feeBeBuffer = GetFeeBreakevenBufferPrice(estFeeUsd, absQty, profile.FeeBreakevenBufferMult);
                                if (feeBeBuffer > 0m)
                                {
                                    if (isLongPosition) targetSL = Math.Max(targetSL, entry + feeBeBuffer);
                                    else targetSL = Math.Min(targetSL, entry - feeBeBuffer);
                                }

                                if (IsBetterStopLoss(targetSL, sl, isLongPosition)
                                    && CanUpdateTrailing(symbol, sl, targetSL, 0m, profile))
                                {
                                    var oldSl = sl;

                                    var (newLastCheck, slDetected) = await UpdateStopLossAsync(
                                        symbol,
                                        newSL: targetSL,
                                        isLong: isLongPosition,
                                        currentPos: pos,
                                        lastSlTpCheckUtc: lastSlTpCheckUtc);

                                    lastSlTpCheckUtc = newLastCheck;

                                    if (slDetected)
                                    {
                                        sl = targetSL;
                                        CommitTrailing(symbol, targetSL);
                                        await _notify.SendAsync(
                                            $"[{symbol}] PROTECT (fallback): netRr={netRr:F2} → SL={Math.Round(sl, 6)} | mode={profile.Tag}");
                                    }
                                    else sl = oldSl;
                                }
                            }
                        }

                    AFTER_PROTECT:

                        // ======================================================================
                        // (F) QUICK TAKE (dynamic)
                        // ======================================================================
                        bool allowQuickTake = !inNoKillZone || dangerConfirmed;
                        bool roiOkForQuick = (initialMarginUsd <= 0m) || (roi >= effMinQuickTakeRoi);

                        if (allowQuickTake
                            && netRr >= effQuickMinRR
                            && netPnlUsd >= minQuickNetProfitUsd
                            && roiOkForQuick
                            && (weakening || dangerConfirmed))
                        {
                            if (netRr >= effQuickGoodRR || dangerConfirmed || IsOppositeStrongCandle(c0, isLongPosition))
                            {
                                await _notify.SendAsync(
                                    $"[{symbol}] QUICK TAKE: netRr={netRr:F2} roi={(initialMarginUsd > 0 ? (roi * 100m).ToString("F1") + "%" : "NA")} " +
                                    $"net={netPnlUsd:F4} (fee~{estFeeUsd:F4} minNet={minQuickNetProfitUsd:F4}) → close | mode={profile.Tag}");

                                await _exchange.ClosePositionAsync(symbol, qty);
                                await _exchange.CancelAllOpenOrdersAsync(symbol);
                                ClearDangerPending(symbol);
                                return;
                            }
                        }

                        // ======================================================================
                        // (G) DANGER CUT
                        // ======================================================================
                        if (netRr <= profile.DangerCutIfRRBelow && dangerConfirmed)
                        {
                            if (initialMarginUsd <= 0m || absLossRoi >= profile.MinDangerCutAbsLossRoi)
                            {
                                await _notify.SendAsync(
                                    $"[{symbol}] DANGER CUT: netRr={netRr:F2} lossRoi={(initialMarginUsd > 0 ? (absLossRoi * 100m).ToString("F1") + "%" : "NA")} → close | mode={profile.Tag}");

                                await _exchange.ClosePositionAsync(symbol, qty);
                                await _exchange.CancelAllOpenOrdersAsync(symbol);
                                ClearDangerPending(symbol);
                                return;
                            }
                        }

                        // ======================================================================
                        // (H) EXIT ON BOUNDARY BREAK
                        // ======================================================================
                        if (dangerConfirmed
                            && netRr >= 0.10m
                            && netPnlUsd >= minBoundaryNetProfitUsd
                            && (initialMarginUsd > 0m ? roi >= profile.MinBoundaryExitRoi : true))
                        {
                            await _notify.SendAsync(
                                $"[{symbol}] EXIT ON BOUNDARY BREAK: netRr={netRr:F2} roi={(initialMarginUsd > 0 ? (roi * 100m).ToString("F1") + "%" : "NA")} " +
                                $"net={netPnlUsd:F4} (fee~{estFeeUsd:F4} minNet={minBoundaryNetProfitUsd:F4}) → close | mode={profile.Tag}");

                            await _exchange.ClosePositionAsync(symbol, qty);
                            await _exchange.CancelAllOpenOrdersAsync(symbol);
                            ClearDangerPending(symbol);
                            return;
                        }
                    }
                    else
                    {
                        if (_pendingDangerSinceUtc.TryGetValue(symbol, out var sinceUtc))
                        {
                            if ((DateTime.UtcNow - sinceUtc) > DangerPendingMaxAge)
                                ClearDangerPending(symbol);
                        }
                    }

                    // =================== TP hit (safety) ===================
                    if (hasEntry && hasTP && IsValidTakeProfit(tp, isLongPosition, entry))
                    {
                        bool hitTp = (isLongPosition && price >= tp) || (!isLongPosition && price <= tp);
                        if (hitTp)
                        {
                            decimal? tpOnExchange = null;

                            if ((DateTime.UtcNow - lastSlTpCheckUtc) >= TimeSpan.FromSeconds(5))
                            {
                                var det2 = await DetectManualSlTpAsync(symbol, isLongPosition, entry, pos);
                                tpOnExchange = det2.Tp;
                                lastSlTpCheckUtc = DateTime.UtcNow;
                            }

                            if (!tpOnExchange.HasValue)
                            {
                                await _notify.SendAsync($"[{symbol}] Price touched TP but TP missing on exchange → force close. | mode={profile.Tag}");
                                await _exchange.ClosePositionAsync(symbol, qty);
                            }
                            else
                            {
                                await _notify.SendAsync($"[{symbol}] TP touched → stop monitor. | mode={profile.Tag}");
                            }

                            await _exchange.CancelAllOpenOrdersAsync(symbol);
                            ClearDangerPending(symbol);
                            return;
                        }
                    }
                }
            }
            finally
            {
                _monitoringPosition.TryRemove(symbol, out _);
                ClearDangerPending(symbol);
            }
        }

        // ============================================================
        // MANUAL ATTACH (keep)
        // ============================================================

        public async Task AttachManualPositionAsync(FuturesPosition pos)
        {
            if (pos == null || pos.PositionAmt == 0)
                return;

            var symbol = pos.Symbol;

            if (_monitoringPosition.ContainsKey(symbol) || _monitoringLimit.ContainsKey(symbol))
                return;

            var now = DateTime.UtcNow;
            var last = _lastManualAttachUtc.GetOrAdd(symbol, _ => DateTime.MinValue);
            if (now - last < ManualAttachThrottle)
                return;
            _lastManualAttachUtc[symbol] = now;

            _monitoringLimit.TryRemove(symbol, out _);

            decimal qty = pos.PositionAmt;
            bool isLong = qty > 0;

            decimal entry = pos.EntryPrice;

            var det = await DetectManualSlTpAsync(symbol, isLong, entry, pos);

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

                    await _notify.SendAsync($"[{symbol}] MANUAL ATTACH: missing TP → place SAFETY-TP={Math.Round(safetyTp, 6)}");
                    var ok = await _exchange.PlaceTakeProfitAsync(symbol, isLong ? "LONG" : "SHORT", Math.Abs(qty), safetyTp);
                    if (!ok) await _notify.SendAsync($"[{symbol}] SAFETY-TP FAILED (manual attach).");
                }
            }

            await _notify.SendAsync($"[{symbol}] MANUAL ATTACH → side={(isLong ? "LONG" : "SHORT")} entry={entry}, SL={sl}, TP={tp}");

            var signal = new TradeSignal
            {
                Symbol = symbol,
                Type = isLong ? SignalType.Long : SignalType.Short,
                EntryPrice = entry,
                StopLoss = sl,
                TakeProfit = tp,
                Time = DateTime.UtcNow,
                Reason = "MANUAL ATTACH",
                Mode = TradeMode.Trend
            };

            var coinInfo = _botConfig.Futures.Coins.FirstOrDefault(i => i.Symbol.Equals(symbol));
            if (coinInfo == null)
            {
                await _notify.SendAsync($"[{symbol}] CoinInfo not found in config.");
                return;
            }

            _ = MonitorPositionAsync(signal, coinInfo);
        }

        private async Task<SlTpDetection> DetectManualSlTpAsync(
            string symbol, bool isLong, decimal entryPriceFromCaller, FuturesPosition pos)
        {
            var normalOrders = await _exchange.GetOpenOrdersAsync(symbol);
            var algoOrders = await _exchange.GetOpenAlgoOrdersAsync(symbol);

            var orders = new List<OpenOrderInfo>();
            if (normalOrders != null) orders.AddRange(normalOrders);
            if (algoOrders != null) orders.AddRange(algoOrders);

            var result = new SlTpDetection { TotalOrders = orders.Count };
            if (orders.Count == 0) return result;

            static decimal GetTrigger(OpenOrderInfo o)
            {
                if (o == null) return 0m;
                if (o.TriggerPrice > 0) return o.TriggerPrice;
                if (o.StopPrice > 0) return o.StopPrice;
                if (o.Price > 0) return o.Price;
                return 0m;
            }

            static bool IsTake(string type)
                => !string.IsNullOrWhiteSpace(type) && type.Contains("TAKE", StringComparison.OrdinalIgnoreCase);

            static bool IsStop(string type)
                => !string.IsNullOrWhiteSpace(type) &&
                   (type.Contains("STOP", StringComparison.OrdinalIgnoreCase) || type.Contains("LOSS", StringComparison.OrdinalIgnoreCase)) &&
                   !type.Contains("TAKE", StringComparison.OrdinalIgnoreCase);

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
        // Update SL (verify + re-add TP if missing)
        // ============================================================

        private async Task<(DateTime lastSlTpCheckUtc, bool slDetected)> UpdateStopLossAsync(
            string symbol,
            decimal newSL,
            bool isLong,
            FuturesPosition currentPos,
            DateTime lastSlTpCheckUtc)
        {
            await _notify.SendAsync($"[{symbol}] Trailing SL update → {Math.Round(newSL, 6)}");

            await _exchange.CancelStopLossOrdersAsync(symbol);
            await Task.Delay(350);

            currentPos ??= await _exchange.GetPositionAsync(symbol);

            decimal qty = Math.Abs(currentPos?.PositionAmt ?? 0m);
            string posSide = isLong ? "LONG" : "SHORT";

            if (qty <= 0m)
            {
                var pos = await _exchange.GetPositionAsync(symbol);
                qty = Math.Abs(pos.PositionAmt);
                if (qty <= 0m)
                {
                    await _notify.SendAsync($"[{symbol}] Cannot find position when updating SL.");
                    return (lastSlTpCheckUtc, false);
                }
            }

            string side = isLong ? "SELL" : "BUY";

            await _exchange.PlaceStopOnlyAsync(symbol, side, posSide, qty, newSL);

            return (lastSlTpCheckUtc, true);
        }

        // ============================================================
        // Fee model (adaptive)
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

                decimal realCommissionAbsUsd = await _exchange.GetCommissionFromUserTradesAsync(symbol, fromUtc, toUtc);
                realCommissionAbsUsd = Math.Abs(realCommissionAbsUsd);

                decimal realizedRate = realCommissionAbsUsd / notional2Sides;
                if (realizedRate <= 0m || realizedRate > 0.01m)
                    return;

                var stats = _feeStatsBySymbol.GetOrAdd(symbol, _ => new FeeStats());

                decimal old = stats.EwmaRate <= 0m ? DefaultTakerFeeRate : stats.EwmaRate;
                decimal updated = old + FeeEwmaAlpha * (realizedRate - old);

                stats.EwmaRate = Clamp(updated, DefaultTakerFeeRate, DefaultTakerFeeRate * 2.5m);
                stats.Samples++;
                stats.LastUpdateUtc = DateTime.UtcNow;

                await _notify.SendAsync(
                    $"[{symbol}] FEE ADAPT: realComm={realCommissionAbsUsd:F6} notional2={notional2Sides:F2} rate={realizedRate:P4} ewma->{stats.EwmaRate:P4} samples={stats.Samples}");
            }
            catch
            {
                // ignore
            }
        }

        // ============================================================
        // Trailing throttle (dynamic from profile)
        // ============================================================

        private bool CanUpdateTrailing(string symbol, decimal currentSl, decimal targetSl, decimal atr, ModeProfile profile)
        {
            var now = DateTime.UtcNow;

            if (_lastTrailingUpdateUtc.TryGetValue(symbol, out var lastUtc))
            {
                if ((now - lastUtc) < TimeSpan.FromSeconds(Math.Max(5, profile.TrailingMinUpdateIntervalSec)))
                    return false;
            }

            if (atr > 0m)
            {
                decimal minStep = atr * profile.TrailingMinStepAtrFrac;
                decimal delta = Math.Abs(targetSl - currentSl);
                if (delta < minStep)
                    return false;
            }

            if (_lastTrailingSl.TryGetValue(symbol, out var lastSl))
            {
                if (Math.Abs(lastSl - targetSl) < (atr > 0m ? atr * 0.05m : 0.000001m))
                    return false;
            }

            return true;
        }

        private void CommitTrailing(string symbol, decimal targetSl)
        {
            var now = DateTime.UtcNow;
            _lastTrailingUpdateUtc[symbol] = now;
            _lastTrailingSl[symbol] = targetSl;
        }

        // ============================================================
        // Danger confirm (trend TF based)
        // ============================================================

        private async Task<bool> ApplyDangerConfirmAsync(
            ModeProfile profile,
            DateTime trendLastClosedOpenTime,
            bool dangerCandidate,
            bool reclaimed,
            bool dangerHard,
            decimal netRr,
            decimal netPnlUsd,
            CoinInfo coinInfo)
        {
            var symbol = coinInfo.Symbol;

            if (!dangerCandidate || reclaimed)
            {
                if (_pendingDangerSinceUtc.ContainsKey(symbol))
                {
                    ClearDangerPending(symbol);

                    if (reclaimed)
                        await _notify.SendAsync($"[{symbol}] DANGER INVALIDATED (reclaim) → keep | mode={profile.Tag}");
                }
                return false;
            }

            if (dangerHard)
            {
                await _notify.SendAsync($"[{symbol}] DANGER HARD: netRr={netRr:F2} net={netPnlUsd:F4} → confirm | mode={profile.Tag}");
                ClearDangerPending(symbol);
                return true;
            }

            int needBars = coinInfo.IsMajor ? DangerConfirmBarsMajor : DangerConfirmBarsAlt;

            if (!_pendingDangerSinceUtc.ContainsKey(symbol))
            {
                _pendingDangerSinceUtc[symbol] = DateTime.UtcNow;
                _pendingDangerBars[symbol] = 1;
                _pendingDangerLastClosedOpenTime[symbol] = trendLastClosedOpenTime;

                if (needBars <= 1)
                {
                    await _notify.SendAsync($"[{symbol}] DANGER CONFIRMED immediately (needBars=1) | mode={profile.Tag}");
                    ClearDangerPending(symbol);
                    return true;
                }

                await _notify.SendAsync($"[{symbol}] DANGER CANDIDATE → wait {needBars} trend closed bar(s) | mode={profile.Tag}");
                return false;
            }

            if (_pendingDangerSinceUtc.TryGetValue(symbol, out var sinceUtc))
            {
                if ((DateTime.UtcNow - sinceUtc) > DangerPendingMaxAge)
                {
                    ClearDangerPending(symbol);
                    await _notify.SendAsync($"[{symbol}] DANGER pending timeout → reset | mode={profile.Tag}");
                    return false;
                }
            }

            if (_pendingDangerLastClosedOpenTime.TryGetValue(symbol, out var lastSeenOpen))
            {
                if (lastSeenOpen != trendLastClosedOpenTime)
                {
                    _pendingDangerLastClosedOpenTime[symbol] = trendLastClosedOpenTime;
                    _pendingDangerBars[symbol] = _pendingDangerBars.TryGetValue(symbol, out var b) ? (b + 1) : 1;
                }
            }
            else
            {
                _pendingDangerLastClosedOpenTime[symbol] = trendLastClosedOpenTime;
                _pendingDangerBars[symbol] = 1;
            }

            int bars = _pendingDangerBars.TryGetValue(symbol, out var barsNow) ? barsNow : 0;

            if (bars >= needBars)
            {
                ClearDangerPending(symbol);
                await _notify.SendAsync($"[{symbol}] DANGER CONFIRMED: bars={bars}/{needBars} | mode={profile.Tag}");
                return true;
            }

            return false;
        }

        private void ClearDangerPending(string symbol)
        {
            _pendingDangerSinceUtc.TryRemove(symbol, out _);
            _pendingDangerBars.TryRemove(symbol, out _);
            _pendingDangerLastClosedOpenTime.TryRemove(symbol, out _);
        }

        // ============================================================
        // Validation helpers
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

        private static bool IsBetterStopLoss(decimal newSL, decimal oldSL, bool isLong)
        {
            if (newSL <= 0m || oldSL <= 0m) return false;
            return isLong ? newSL > oldSL : newSL < oldSL;
        }

        private static bool IsBoundaryBroken(decimal close, decimal boundary, bool isLong, decimal emaTol)
        {
            if (boundary <= 0m) return false;
            return isLong
                ? close < boundary * (1m - emaTol)
                : close > boundary * (1m + emaTol);
        }

        // ============================================================
        // EMA / ATR helpers
        // ============================================================

        private static decimal ComputeEmaLast(IReadOnlyList<Candle> candles, int period)
        {
            if (candles == null || candles.Count == 0) return 0m;

            var closes = candles
                .Skip(Math.Max(0, candles.Count - period * 3))
                .Select(c => c.Close)
                .ToArray();

            if (closes.Length == 0) return 0m;

            decimal k = 2m / (period + 1);
            decimal ema = closes[0];

            for (int i = 1; i < closes.Length; i++)
                ema = closes[i] * k + ema * (1 - k);

            return ema;
        }

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

        private static bool IsTrendStillValid(IReadOnlyList<Candle> trendCandles, bool isLong, decimal ema89)
        {
            if (trendCandles == null || trendCandles.Count < 5) return true;
            if (ema89 <= 0m) return true;

            var lastClosed = trendCandles[^2];
            var prevClosed = trendCandles[^3];

            if (isLong)
                return lastClosed.Close >= ema89 || prevClosed.Close >= ema89;
            else
                return lastClosed.Close <= ema89 || prevClosed.Close <= ema89;
        }

        // ============================================================
        // Candle pattern helpers
        // ============================================================

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

            bool closeNearEdge;
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

            bool rejectWick;
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

        // ============================================================
        // Break-even lock helper
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

        // ============================================================
        // Fee buffer helpers (dynamic)
        // ============================================================

        private static decimal GetFeeBreakevenBufferPrice(decimal estFeeUsd, decimal absQty, decimal feeBeMult)
        {
            if (estFeeUsd <= 0m || absQty <= 0m) return 0m;
            feeBeMult = feeBeMult <= 0m ? 1.0m : feeBeMult;
            return (estFeeUsd * feeBeMult) / absQty;
        }

        private static bool IsSlTooCloseToPrice(decimal price, decimal sl, decimal atr, decimal minSlDistanceAtrFrac)
        {
            if (atr <= 0m || price <= 0m || sl <= 0m) return false;
            var frac = minSlDistanceAtrFrac <= 0m ? 0.35m : minSlDistanceAtrFrac;
            return Math.Abs(price - sl) < atr * frac;
        }

        // ============================================================
        // NEW: Anti "ăn non bị cắn pullback" helpers
        // ============================================================

        private static decimal DeriveHardTrailStartRR(ModeProfile profile, decimal plannedRR, bool isScalp)
        {
            // Base: lấy từ profile.MinTrailStartRR nhưng nâng lên để tránh trailing quá sớm.
            // - Trend: aim ~1.0R (hoặc 60% plannedRR nếu plannedRR nhỏ)
            // - Scalp: aim ~0.8R (nhưng vẫn >= profile.MinTrailStartRR)
            decimal baseMin = Math.Max(0.20m, profile.MinTrailStartRR);

            decimal target = isScalp ? 0.80m : 1.00m;

            if (plannedRR > 0.20m)
            {
                decimal byPlanned = plannedRR * 0.60m;
                target = Math.Min(target, Clamp(byPlanned, isScalp ? 0.55m : 0.70m, 1.10m));
            }

            return Math.Max(baseMin, target);
        }

        private static decimal GetAtrTrailMultByTfMinutes(int tfMinutes, decimal baseMult)
        {
            // TF càng nhỏ -> noise càng lớn -> trail rộng hơn (mult tăng)
            // 1m: x1.35, 3m: x1.20, 5m: x1.10, 15m+: x1.00
            if (tfMinutes <= 1) return baseMult * 1.35m;
            if (tfMinutes <= 3) return baseMult * 1.20m;
            if (tfMinutes <= 5) return baseMult * 1.10m;
            return baseMult;
        }

        private static bool IsPullbackResumeContinuation(IReadOnlyList<Candle>? candles, bool isLong, decimal atr)
        {
            if (candles == null || candles.Count < 6) return false;

            // Use last 3 CLOSED candles: c2 (older), c1 (pullback), c0 (resume)
            var c0 = candles[^2];
            var c1 = candles[^3];
            var c2 = candles[^4];

            if (atr <= 0m)
            {
                // fallback simple structure check without ATR
                if (isLong)
                    return c1.Close < c1.Open && c0.Close > c0.Open && c0.Close > c1.High;
                else
                    return c1.Close > c1.Open && c0.Close < c0.Open && c0.Close < c1.Low;
            }

            // Require pullback not too deep (avoid "reversal"): pullback size <= 1.2*ATR
            decimal pullbackRange = c1.High - c1.Low;
            bool pullbackReasonable = pullbackRange <= atr * 1.20m;

            // Resume candle strong: close breaks pullback extremum by a small margin
            decimal margin = atr * 0.05m;

            if (isLong)
            {
                bool pullback = c1.Close < c1.Open;          // red
                bool resume = c0.Close > c0.Open;            // green
                bool breakHigh = c0.Close >= c1.High + margin;
                bool stillHigherLow = c1.Low >= c2.Low - atr * 0.30m; // avoid deep dump
                return pullback && resume && breakHigh && pullbackReasonable && stillHigherLow;
            }
            else
            {
                bool pullback = c1.Close > c1.Open;          // green
                bool resume = c0.Close < c0.Open;            // red
                bool breakLow = c0.Close <= c1.Low - margin;
                bool stillLowerHigh = c1.High <= c2.High + atr * 0.30m;
                return pullback && resume && breakLow && pullbackReasonable && stillLowerHigh;
            }
        }

        // ============================================================
        // Small helpers
        // ============================================================

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
    }
}
