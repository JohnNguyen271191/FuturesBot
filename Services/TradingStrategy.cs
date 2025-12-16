using System;
using System.Collections.Generic;
using System.Linq;
using FuturesBot.Config;
using FuturesBot.IServices;
using FuturesBot.Models;
using FuturesBot.Utils;
using static FuturesBot.Utils.EnumTypesHelper;

namespace FuturesBot.Services
{
    /// <summary>
    /// V4 WINRATE PATCH:
    /// 1) 2-step retest: A (touch) + B (confirm) -> giảm fake retest
    /// 2) Trend strength filter: EMA separation + EMA slope -> lọc chop
    /// 3) Momentum thay reject: chỉ hợp lệ nếu confirm candle mạnh + close vượt EMA rõ
    /// 4) Anti-impulse: check ở nến A (retest) thay vì nến B (confirm)
    /// 5) Max distance to anchor: tránh entry trễ / RR xấu
    /// 6) RSI cap cho trend retest: tránh long quá nóng / short quá quá bán
    ///
    /// NOTE: dùng NẾN ĐÃ ĐÓNG (Count - 2)
    /// </summary>
    public class TradingStrategy : IStrategyService
    {
        private readonly IndicatorService _indicators;

        public TradingStrategy(IndicatorService indicators)
        {
            _indicators = indicators ?? throw new ArgumentNullException(nameof(indicators));
        }

        // ========================= CONFIG ==============================

        private const int MinBars = 120;
        private const int SwingLookback = 5;
        private const int PullbackVolumeLookback = 5;

        private const decimal EmaRetestBand = 0.002m;        // ±0.2%

        // buffer chung (dùng cho scalp swing)
        private const decimal StopBufferPercent = 0.005m;    // 0.5%

        // RR default cho Altcoin (trend)
        private const decimal RiskReward = 1.5m;

        // RR cho sideway scalp (cơ bản)
        private const decimal RiskRewardSideway = 1m;

        // RR cho Major (BTC/ETH)
        private const decimal RiskRewardMajor = 2.0m;
        private const decimal RiskRewardSidewayMajor = 1.0m;

        private const decimal RsiBullThreshold = 55m;
        private const decimal RsiBearThreshold = 45m;

        private const decimal ExtremeRsiHigh = 75m;
        private const decimal ExtremeRsiLow = 30m;
        private const decimal ExtremeEmaBoost = 0.01m;       // 1%

        // V4: RSI cap cho TREND RETEST (tăng winrate)
        private const decimal TrendRetestRsiMaxForLong = 68m;
        private const decimal TrendRetestRsiMinForShort = 32m;

        // Entry offset để tránh đỉnh/đáy (trend)
        private const decimal EntryOffsetPercent = 0.003m;   // 0.3% cho trend

        // Entry offset cho scalp
        private const decimal EntryOffsetPercentForScal = 0.001m;   // 0.1% cho scalp

        // SL neo EMA cho trend
        private const decimal AnchorSlBufferPercent = 0.0015m;      // 0.15%

        // OPTION: dùng swing để đặt SL "an toàn hơn" (tránh quét EMA)
        private const bool UseSwingForTrendStop = true;
        private const decimal SwingStopExtraBufferPercent = 0.0010m; // +0.10% quanh swing

        // Nến climax + overextended xa EMA gần nhất (tránh vừa vào là đảo)
        private const int ClimaxLookback = 20;
        private const decimal ClimaxBodyMultiplier = 1.8m;
        private const decimal ClimaxVolumeMultiplier = 1.5m;
        private const decimal OverextendedFromEmaPercent = 0.01m; // 1% xa EMA gần nhất

        // ========================= BTC vs ALT CONFIG ====================

        private const decimal MinMajorVolumeUsd15m = 2_000_000m; // BTC/ETH
        private const decimal MinAltVolumeUsd15m = 600_000m;     // Altcoin

        private const int VolumeMedianLookback = 40;
        private const decimal MinVolumeVsMedianRatioMajor = 0.55m;
        private const decimal MinVolumeVsMedianRatioAlt = 0.65m;

        // Alt: EMA34 H1 slope tối thiểu
        private const int EmaSlopeLookbackH1 = 3;
        private const decimal MinAltEmaSlopeH1 = 0.003m; // 0.3%

        // V4: thêm slope nhẹ cho Major (lọc chop)
        private const decimal MinMajorEmaSlopeH1 = 0.0015m; // 0.15%

        // V4: EMA separation (ema34 vs ema89) để xác nhận trend khỏe
        private const decimal MinEmaSeparationMajor = 0.0025m; // 0.25%
        private const decimal MinEmaSeparationAlt = 0.0035m;   // 0.35%

        // ========================= V4: 2-STEP RETEST + CONFIRM =========================
        private const decimal ConfirmCloseBeyondAnchorMajor = 0.0008m; // 0.08%
        private const decimal ConfirmCloseBeyondAnchorAlt = 0.0012m;   // 0.12%
        private const decimal ConfirmBodyToRangeMin = 0.55m;           // confirm candle mạnh

        // ========================= V4: MAX DISTANCE TO ANCHOR =========================
        private const decimal MaxEntryDistanceToAnchorMajor = 0.0035m; // 0.35%
        private const decimal MaxEntryDistanceToAnchorAlt = 0.0050m;   // 0.50%

        // ========================= SIDEWAY FILTER (FIX: bớt over-trigger) =========================
        private const int SidewaySlopeLookback = 10;
        private const decimal SidewayEmaDistThreshold = 0.0015m; // 0.15%
        private const decimal SidewaySlopeThreshold = 0.002m;    // 0.2%
        private const int SidewayConfirmBars = 3;

        // ========================= MARKET STRUCTURE (V3) =========================
        private const int StructureSwingStrength = 3;
        private const int StructureMaxLookbackBars = 80;
        private const int StructureNeedSwings = 2;
        private const decimal StructureBreakToleranceMajor = 0.0020m; // 0.20%
        private const decimal StructureBreakToleranceAlt = 0.0030m;   // 0.30%

        // ========================= MODE 1: MARKET ON STRONG REJECT =========================
        private const bool EnableMarketOnStrongRejectForMajor = true;
        private const bool EnableMarketOnStrongRejectForAlt = false;

        private const decimal StrongRejectWickToBody = 2.0m;
        private const decimal StrongRejectCloseInRange = 0.25m;
        private const decimal StrongRejectMinVolVsMedian = 0.80m;

        private const decimal MarketableLimitOffset = 0.0002m; // 0.02%

        // ========================= MODE 2: PULLBACK -> REJECT -> CONTINUATION =========================
        private const bool EnableBreakdownContinuationForMajor = true;
        private const bool EnableBreakoutContinuationForMajor = true;

        private const int ContinuationLookback = 20;
        private const decimal ContinuationBreakBuffer = 0.0012m;
        private const decimal ContinuationPullbackBand = 0.0030m;
        private const decimal ContinuationMinVolVsMedian = 0.85m;

        private const decimal ContinuationMinRsiForShort = 20m;
        private const decimal ContinuationMaxRsiForLong = 80m;

        private const decimal RiskRewardContinuationMajor = 1.3m;
        private const decimal ContinuationTightSlBuffer = 0.0008m;
        private const int ContinuationFindEventLookback = 12;

        // ========================= PATCH: MOMENTUM "HARD" =========================
        private const bool AllowMomentumInsteadOfReject = true;

        // ========================= PATCH: ANTI-SQUEEZE / ANTI-DUMP =========================
        private const bool EnableAntiSqueezeShort = true;
        private const bool EnableAntiDumpLong = true;

        private const decimal AntiImpulseBodyToRangeMin = 0.65m;
        private const decimal AntiImpulseCloseNearEdgeMax = 0.20m;
        private const decimal AntiImpulseMinVolVsMedian = 0.90m;
        private const decimal AntiImpulseMaxDistToAnchor = 0.0025m;

        // =====================================================================
        //                           ENTRY SIGNAL
        // =====================================================================

        public TradeSignal GenerateSignal(IReadOnlyList<Candle> candles15m, IReadOnlyList<Candle> candles1h, CoinInfo coinInfo)
        {
            if (candles15m == null || candles1h == null || candles15m.Count < MinBars || candles1h.Count < MinBars)
                return new TradeSignal();

            if (coinInfo == null)
                return new TradeSignal();

            bool isMajor = coinInfo.IsMajor;

            // nến đã đóng
            int i15 = candles15m.Count - 2;
            int iH1 = candles1h.Count - 2;
            if (i15 <= 2 || iH1 <= 2) return new TradeSignal(); // cần thêm buffer cho 2-step retest

            var last15 = candles15m[i15];
            var lastH1 = candles1h[iH1];

            // --- Indicators ---
            var ema34_15 = _indicators.Ema(candles15m, 34);
            var ema89_15 = _indicators.Ema(candles15m, 89);
            var ema200_15 = _indicators.Ema(candles15m, 200);

            var ema34_h1 = _indicators.Ema(candles1h, 34);
            var ema89_h1 = _indicators.Ema(candles1h, 89);

            var rsi15 = _indicators.Rsi(candles15m, 6);
            var (macd15, sig15, _) = _indicators.Macd(candles15m, 5, 13, 5);

            decimal ema34_15Now = ema34_15[i15];
            decimal ema89_15Now = ema89_15[i15];
            decimal ema200_15Now = ema200_15[i15];

            // =================== BTC vs ALT PROFILE =======================

            decimal rrTrend = isMajor ? RiskRewardMajor : RiskReward;
            decimal rrSideway = isMajor ? RiskRewardSidewayMajor : RiskRewardSideway;
            bool allowSideway = isMajor;

            // Volume ước lượng
            decimal volUsd15 = last15.Close * last15.Volume;

            decimal medianVolUsd = GetMedianVolUsd(candles15m, i15, VolumeMedianLookback);
            decimal ratioVsMedian = medianVolUsd > 0 ? (volUsd15 / medianVolUsd) : 1m;

            if (isMajor)
            {
                if (volUsd15 < MinMajorVolumeUsd15m && ratioVsMedian < MinVolumeVsMedianRatioMajor)
                {
                    return new TradeSignal
                    {
                        Type = SignalType.None,
                        Reason = $"{coinInfo.Symbol}: Volume M15 yếu ({volUsd15:F0} USDT, vsMedian={ratioVsMedian:P0}) → bỏ qua.",
                        Symbol = coinInfo.Symbol
                    };
                }
            }
            else
            {
                if (volUsd15 < MinAltVolumeUsd15m && ratioVsMedian < MinVolumeVsMedianRatioAlt)
                {
                    return new TradeSignal
                    {
                        Type = SignalType.None,
                        Reason = $"{coinInfo.Symbol}: Alt volume M15 yếu ({volUsd15:F0} USDT, vsMedian={ratioVsMedian:P0}) → bỏ qua.",
                        Symbol = coinInfo.Symbol
                    };
                }

                // Alt: EMA34 H1 slope tối thiểu
                if (!IsEmaSlopeOk(ema34_h1, iH1, EmaSlopeLookbackH1, MinAltEmaSlopeH1))
                {
                    return new TradeSignal
                    {
                        Type = SignalType.None,
                        Reason = $"{coinInfo.Symbol}: Alt sideway, EMA34 H1 slope yếu → NO TRADE.",
                        Symbol = coinInfo.Symbol
                    };
                }
            }

            // =================== SIDEWAY FILTER ===========================

            bool sideway15 = IsSidewayStrong(candles15m, ema34_15, ema89_15);
            bool sidewayH1 = IsSidewayStrong(candles1h, ema34_h1, ema89_h1);

            if (!isMajor && (sideway15 || sidewayH1))
            {
                return new TradeSignal
                {
                    Type = SignalType.None,
                    Reason = $"{coinInfo.Symbol}: Altcoin SIDEWAY mạnh trên {(sidewayH1 ? "H1" : "M15")} → NO TRADE.",
                    Symbol = coinInfo.Symbol
                };
            }

            // ================= FILTER: CLIMAX + OVEREXTENDED = NO TRADE ========

            bool climaxDanger =
                IsClimaxAwayFromEma(candles15m, i15, ema34_15Now, ema89_15Now, ema200_15Now) ||
                IsClimaxAwayFromEma(candles15m, i15 - 1, ema34_15Now, ema89_15Now, ema200_15Now);

            if (climaxDanger)
            {
                return new TradeSignal
                {
                    Type = SignalType.None,
                    Reason = $"{coinInfo.Symbol}: Climax + overextended xa EMA → chờ retest.",
                    Symbol = coinInfo.Symbol
                };
            }

            // =================== XÁC NHẬN TREND H1 (BIAS) ======================

            bool h1BiasUp = ema34_h1[iH1] > ema89_h1[iH1];
            bool h1BiasDown = ema34_h1[iH1] < ema89_h1[iH1];

            // =================== XÁC NHẬN TREND MẠNH (H1 + M15) ===============

            bool upTrend =
                lastH1.Close > ema34_h1[iH1] &&
                ema34_h1[iH1] > ema89_h1[iH1] &&
                last15.Close > ema34_15Now &&
                ema34_15Now > ema89_15Now;

            bool downTrend =
                lastH1.Close < ema34_h1[iH1] &&
                ema34_h1[iH1] < ema89_h1[iH1] &&
                last15.Close < ema34_15Now &&
                ema34_15Now < ema89_15Now;

            // Extreme cases (tham khảo – KHÔNG cho phép đu trend)
            bool extremeUp =
                last15.Close > ema34_15Now * (1 + ExtremeEmaBoost) &&
                macd15[i15] > sig15[i15] &&
                rsi15[i15] > ExtremeRsiHigh;

            bool extremeDump =
                last15.Close < ema34_15Now * (1 - ExtremeEmaBoost) &&
                macd15[i15] < sig15[i15] &&
                rsi15[i15] < ExtremeRsiLow;

            // =================== MARKET STRUCTURE (V3) ==================
            decimal stTol = isMajor ? StructureBreakToleranceMajor : StructureBreakToleranceAlt;

            var (lowerHigh, higherLow, lastSwingHigh, prevSwingHigh, lastSwingLow, prevSwingLow)
                = DetectMarketStructure(candles15m, i15, stTol);

            bool blockLongByStructure = lowerHigh && !higherLow;
            bool blockShortByStructure = higherLow && !lowerHigh;

            // =================== SIDEWAY / PULLBACK SCALP ======================

            if (!extremeUp && !extremeDump)
            {
                bool m15PullbackDown =
                    h1BiasDown &&
                    ema34_15Now <= last15.Close &&
                    last15.Close <= ema89_15Now;

                bool m15PullbackUp =
                    h1BiasUp &&
                    ema89_15Now <= last15.Close &&
                    last15.Close <= ema34_15Now;

                bool shouldTrySideway =
                    (!upTrend && !downTrend) || m15PullbackDown || m15PullbackUp || sideway15;

                if (allowSideway && shouldTrySideway)
                {
                    bool biasUp = h1BiasUp;
                    bool biasDown = h1BiasDown;

                    // structure override: không scalp ngược cấu trúc
                    if (blockLongByStructure)
                    {
                        biasUp = false;
                        biasDown = true;
                    }
                    else if (blockShortByStructure)
                    {
                        biasDown = false;
                        biasUp = true;
                    }

                    var sidewaySignal = BuildSidewayScalp(
                        candles15m,
                        ema34_15,
                        ema89_15,
                        ema200_15,
                        rsi15,
                        macd15,
                        sig15,
                        last15,
                        coinInfo,
                        biasUp,
                        biasDown,
                        rrSideway);

                    if (sidewaySignal.Type != SignalType.None)
                        return sidewaySignal;
                }
                else if (!isMajor && shouldTrySideway && !upTrend && !downTrend)
                {
                    return new TradeSignal
                    {
                        Type = SignalType.None,
                        Reason = $"{coinInfo.Symbol}: Altcoin sideway/pullback, H1+M15 chưa align trend rõ → bỏ qua.",
                        Symbol = coinInfo.Symbol
                    };
                }
            }

            // =================== Pullback volume filter ===================
            decimal avgVol = PriceActionHelper.AverageVolume(candles15m, i15 - 1, PullbackVolumeLookback);
            bool volumeOkSoft = avgVol <= 0 || last15.Volume >= avgVol * 0.7m;

            // =================== MODE 2 (MAJOR ONLY) ===================
            if (isMajor)
            {
                if (EnableBreakdownContinuationForMajor &&
                    (downTrend || h1BiasDown) &&
                    !blockShortByStructure &&
                    !extremeDump)
                {
                    var contShort = BuildBreakdownPullbackThenRejectShort(
                        candles15m,
                        i15,
                        rsi15[i15],
                        volUsd15,
                        medianVolUsd,
                        coinInfo);

                    if (contShort.Type != SignalType.None)
                        return contShort;
                }

                if (EnableBreakoutContinuationForMajor &&
                    (upTrend || h1BiasUp) &&
                    !blockLongByStructure &&
                    !extremeUp)
                {
                    var contLong = BuildBreakoutPullbackThenRejectLong(
                        candles15m,
                        i15,
                        rsi15[i15],
                        volUsd15,
                        medianVolUsd,
                        coinInfo);

                    if (contLong.Type != SignalType.None)
                        return contLong;
                }
            }

            // =================== V4 TREND STRENGTH FILTER (chỉ áp cho TREND TRADE) ===================
            // Nếu trend đúng nhưng EMA separation + slope yếu => dễ chop => bỏ để tăng winrate
            bool trendStrengthOk = true;
            if (upTrend || downTrend)
            {
                decimal price = last15.Close;
                decimal sep15 = price > 0 ? Math.Abs(ema34_15Now - ema89_15Now) / price : 0m;
                decimal sepH1 = lastH1.Close > 0 ? Math.Abs(ema34_h1[iH1] - ema89_h1[iH1]) / lastH1.Close : 0m;

                decimal minSep = isMajor ? MinEmaSeparationMajor : MinEmaSeparationAlt;
                bool sepOk = sep15 >= minSep && sepH1 >= minSep * 0.75m; // H1 nhẹ hơn chút

                bool slopeOk = isMajor
                    ? IsEmaSlopeOk(ema34_h1, iH1, EmaSlopeLookbackH1, MinMajorEmaSlopeH1)
                    : true; // alt đã check ở trên

                trendStrengthOk = sepOk && slopeOk;

                if (!trendStrengthOk)
                {
                    return new TradeSignal
                    {
                        Type = SignalType.None,
                        Reason = $"{coinInfo.Symbol}: V4 NO TREND – Trend yếu/chop (sep15={sep15:P2}, sepH1={sepH1:P2}, slopeOk={slopeOk}).",
                        Symbol = coinInfo.Symbol
                    };
                }
            }

            // =================== BUILD LONG / SHORT (TREND TRADE) =========

            if (upTrend && !blockLongByStructure)
            {
                var longSignal = BuildLongV4Winrate(
                    candles15m,
                    ema34_15,
                    ema89_15,
                    ema200_15,
                    rsi15,
                    macd15,
                    sig15,
                    coinInfo,
                    rrTrend,
                    volumeOkSoft);

                if (longSignal.Type != SignalType.None)
                    return longSignal;
            }
            else if (upTrend && blockLongByStructure)
            {
                return new TradeSignal
                {
                    Type = SignalType.None,
                    Reason = $"{coinInfo.Symbol}: Block LONG vì Market Structure Lower-High (LH={lowerHigh}, lastH={lastSwingHigh:F2} < prevH={prevSwingHigh:F2}).",
                    Symbol = coinInfo.Symbol
                };
            }

            if (downTrend && !blockShortByStructure)
            {
                var shortSignal = BuildShortV4Winrate(
                    candles15m,
                    ema34_15,
                    ema89_15,
                    ema200_15,
                    rsi15,
                    macd15,
                    sig15,
                    coinInfo,
                    rrTrend,
                    volumeOkSoft);

                if (shortSignal.Type != SignalType.None)
                    return shortSignal;
            }
            else if (downTrend && blockShortByStructure)
            {
                return new TradeSignal
                {
                    Type = SignalType.None,
                    Reason = $"{coinInfo.Symbol}: Block SHORT vì Market Structure Higher-Low (HL={higherLow}, lastL={lastSwingLow:F2} > prevL={prevSwingLow:F2}).",
                    Symbol = coinInfo.Symbol
                };
            }

            return new TradeSignal();
        }

        // =====================================================================
        //                    MODE 2: PULLBACK -> REJECT -> CONTINUATION
        // =====================================================================

        private TradeSignal BuildBreakdownPullbackThenRejectShort(
            IReadOnlyList<Candle> candles15m,
            int i15,
            decimal rsiNow,
            decimal volUsd15,
            decimal medianVolUsd,
            CoinInfo coinInfo)
        {
            if (i15 < ContinuationLookback + 5) return new TradeSignal();

            if (rsiNow < ContinuationMinRsiForShort) return new TradeSignal();

            decimal ratio = medianVolUsd > 0 ? (volUsd15 / medianVolUsd) : 1m;
            if (ratio < ContinuationMinVolVsMedian) return new TradeSignal();

            var c = candles15m[i15];       // C (rejection)
            var b = candles15m[i15 - 1];   // B (pullback)

            decimal level = FindLowestLow(candles15m, i15 - 2, ContinuationLookback);
            if (level <= 0) return new TradeSignal();

            int breakdownIdx = FindRecentBreakdownIndex(candles15m, i15 - 1, level, ContinuationFindEventLookback);
            if (breakdownIdx < 0) return new TradeSignal();

            bool pullbackTouch =
                b.High >= level * (1m - ContinuationPullbackBand) &&
                b.High <= level * (1m + ContinuationPullbackBand);

            if (!pullbackTouch) return new TradeSignal();

            bool reject =
                c.High >= level * (1m - ContinuationPullbackBand) &&
                c.Close < c.Open &&
                c.Close < level;

            if (!reject) return new TradeSignal();

            if (IsClimaxCandle(candles15m, i15)) return new TradeSignal();

            decimal entry = c.Close * (1m - MarketableLimitOffset);

            decimal sl = c.High * (1m + ContinuationTightSlBuffer);
            if (sl <= entry) return new TradeSignal();

            decimal risk = sl - entry;
            decimal tp = entry - risk * RiskRewardContinuationMajor;

            return new TradeSignal
            {
                Type = SignalType.Short,
                EntryPrice = entry,
                StopLoss = sl,
                TakeProfit = tp,
                Reason = $"{coinInfo.Symbol}: MODE2 PULLBACK SHORT – pullback->reject @level={level:F2}, SL tight, vsMedian={ratio:P0}, RSI={rsiNow:F1}.",
                Symbol = coinInfo.Symbol
            };
        }

        private TradeSignal BuildBreakoutPullbackThenRejectLong(
            IReadOnlyList<Candle> candles15m,
            int i15,
            decimal rsiNow,
            decimal volUsd15,
            decimal medianVolUsd,
            CoinInfo coinInfo)
        {
            if (i15 < ContinuationLookback + 5) return new TradeSignal();

            if (rsiNow > ContinuationMaxRsiForLong) return new TradeSignal();

            decimal ratio = medianVolUsd > 0 ? (volUsd15 / medianVolUsd) : 1m;
            if (ratio < ContinuationMinVolVsMedian) return new TradeSignal();

            var c = candles15m[i15];       // C (rejection)
            var b = candles15m[i15 - 1];   // B (pullback)

            decimal level = FindHighestHigh(candles15m, i15 - 2, ContinuationLookback);
            if (level <= 0) return new TradeSignal();

            int breakoutIdx = FindRecentBreakoutIndex(candles15m, i15 - 1, level, ContinuationFindEventLookback);
            if (breakoutIdx < 0) return new TradeSignal();

            bool pullbackTouch =
                b.Low <= level * (1m + ContinuationPullbackBand) &&
                b.Low >= level * (1m - ContinuationPullbackBand);

            if (!pullbackTouch) return new TradeSignal();

            bool reject =
                c.Low <= level * (1m + ContinuationPullbackBand) &&
                c.Close > c.Open &&
                c.Close > level;

            if (!reject) return new TradeSignal();

            if (IsClimaxCandle(candles15m, i15)) return new TradeSignal();

            decimal entry = c.Close * (1m + MarketableLimitOffset);

            decimal sl = c.Low * (1m - ContinuationTightSlBuffer);
            if (sl >= entry || sl <= 0) return new TradeSignal();

            decimal risk = entry - sl;
            decimal tp = entry + risk * RiskRewardContinuationMajor;

            return new TradeSignal
            {
                Type = SignalType.Long,
                EntryPrice = entry,
                StopLoss = sl,
                TakeProfit = tp,
                Reason = $"{coinInfo.Symbol}: MODE2 PULLBACK LONG – pullback->reject @level={level:F2}, SL tight, vsMedian={ratio:P0}, RSI={rsiNow:F1}.",
                Symbol = coinInfo.Symbol
            };
        }

        private int FindRecentBreakdownIndex(IReadOnlyList<Candle> candles, int endIdx, decimal level, int lookbackBars)
        {
            int start = Math.Max(0, endIdx - lookbackBars + 1);
            for (int i = endIdx; i >= start; i--)
            {
                if (candles[i].Close < level * (1m - ContinuationBreakBuffer))
                    return i;
            }
            return -1;
        }

        private int FindRecentBreakoutIndex(IReadOnlyList<Candle> candles, int endIdx, decimal level, int lookbackBars)
        {
            int start = Math.Max(0, endIdx - lookbackBars + 1);
            for (int i = endIdx; i >= start; i--)
            {
                if (candles[i].Close > level * (1m + ContinuationBreakBuffer))
                    return i;
            }
            return -1;
        }

        private decimal FindLowestLow(IReadOnlyList<Candle> candles, int endIdx, int lookback)
        {
            int start = Math.Max(0, endIdx - lookback + 1);
            decimal min = decimal.MaxValue;
            for (int i = start; i <= endIdx; i++)
            {
                if (candles[i].Low < min) min = candles[i].Low;
            }
            return min == decimal.MaxValue ? 0m : min;
        }

        private decimal FindHighestHigh(IReadOnlyList<Candle> candles, int endIdx, int lookback)
        {
            int start = Math.Max(0, endIdx - lookback + 1);
            decimal max = 0m;
            for (int i = start; i <= endIdx; i++)
            {
                if (candles[i].High > max) max = candles[i].High;
            }
            return max;
        }

        // =====================================================================
        //                       V4 WINRATE LONG (2-STEP)
        // =====================================================================

        private TradeSignal BuildLongV4Winrate(
            IReadOnlyList<Candle> candles15m,
            IReadOnlyList<decimal> ema34_15,
            IReadOnlyList<decimal> ema89_15,
            IReadOnlyList<decimal> ema200_15,
            IReadOnlyList<decimal> rsi15,
            IReadOnlyList<decimal> macd15,
            IReadOnlyList<decimal> sig15,
            CoinInfo coinInfo,
            decimal riskRewardTrend,
            bool volumeOkSoft)
        {
            int iB = candles15m.Count - 2;    // B = confirm candle (đã đóng)
            int iA = iB - 1;                  // A = retest candle (đã đóng)
            if (iA <= 1) return new TradeSignal();

            var A = candles15m[iA];
            var B = candles15m[iB];

            decimal ema34 = ema34_15[iB];
            decimal ema89 = ema89_15[iB];
            decimal ema200 = ema200_15[iB];

            // RSI cap (tăng winrate) cho TREND RETEST
            if (rsi15[iB] > TrendRetestRsiMaxForLong)
            {
                return new TradeSignal
                {
                    Type = SignalType.None,
                    Reason = $"{coinInfo.Symbol}: V4 NO LONG – RSI quá cao ({rsi15[iB]:F1}) cho trend retest.",
                    Symbol = coinInfo.Symbol
                };
            }

            // 1) SUPPORT dynamic (EMA dưới giá B.close)
            var supports = new List<decimal>();
            if (ema34 > 0 && ema34 < B.Close) supports.Add(ema34);
            if (ema89 > 0 && ema89 < B.Close) supports.Add(ema89);
            if (ema200 > 0 && ema200 < B.Close) supports.Add(ema200);

            if (supports.Count == 0)
            {
                return new TradeSignal
                {
                    Type = SignalType.None,
                    Reason = $"{coinInfo.Symbol}: Uptrend nhưng không có EMA support dưới giá.",
                    Symbol = coinInfo.Symbol
                };
            }

            decimal anchor = supports.Max();
            decimal confirmBeyond = coinInfo.IsMajor ? ConfirmCloseBeyondAnchorMajor : ConfirmCloseBeyondAnchorAlt;

            // 2) A touch anchor (retest)
            bool touchA =
                anchor > 0m &&
                A.Low <= anchor * (1 + EmaRetestBand) &&
                A.Low >= anchor * (1 - EmaRetestBand);

            // 3) Anti-dump: check bearish impulse ở nến A (rất quan trọng V4)
            if (EnableAntiDumpLong && touchA && IsBearishImpulseAtRetest(candles15m, iA, anchor))
            {
                return new TradeSignal
                {
                    Type = SignalType.None,
                    Reason = $"{coinInfo.Symbol}: V4 NO LONG – AntiDump at retest(A) near EMA({anchor:F6}).",
                    Symbol = coinInfo.Symbol
                };
            }

            // 4) B confirm (đóng bullish + đóng vượt anchor rõ + phá high nhỏ)
            bool bullishB = B.Close > B.Open;
            bool closeBackAbove = B.Close >= anchor * (1m + confirmBeyond);
            bool breakSmall = B.High > A.High;

            // 5) Reject truyền thống (wick xuyên anchor rồi đóng trên) - ưu tiên trên B
            bool rejectB =
                bullishB &&
                B.Low < anchor &&
                B.Close > anchor;

            // 6) Momentum
            bool macdCrossUp = macd15[iB] > sig15[iB] && macd15[iB - 1] <= sig15[iB - 1];
            bool rsiBull = rsi15[iB] > RsiBullThreshold && rsi15[iB] >= rsi15[iB - 1];

            bool bodyStrongB = IsBodyStrong(B, ConfirmBodyToRangeMin);

            // V4: momentumHard chỉ hợp lệ khi confirm candle B mạnh + đóng vượt anchor rõ
            bool momentumHard = macdCrossUp && rsiBull && bodyStrongB && closeBackAbove;

            // 7) Điều kiện vào lệnh: 2-step + (rejectB hoặc momentumHard)
            bool ok = touchA && bullishB && closeBackAbove && breakSmall && rejectB;

            if (!ok && AllowMomentumInsteadOfReject)
            {
                // cho phép thay reject bằng momentumHard (nhưng vẫn yêu cầu 2-step confirm)
                ok = touchA && bullishB && closeBackAbove && breakSmall && momentumHard && volumeOkSoft;
            }

            if (!ok)
            {
                return new TradeSignal
                {
                    Type = SignalType.None,
                    Reason = $"{coinInfo.Symbol}: V4 Long chưa đạt (touchA={touchA}, bullishB={bullishB}, close>{confirmBeyond:P2}={closeBackAbove}, break={breakSmall}, rejectB={rejectB}, momHard={momentumHard}, volOk={volumeOkSoft}).",
                    Symbol = coinInfo.Symbol
                };
            }

            // SL dynamic
            decimal slByEma = anchor * (1m - AnchorSlBufferPercent);

            decimal sl = slByEma;
            if (UseSwingForTrendStop)
            {
                decimal swingLow = PriceActionHelper.FindSwingLow(candles15m, iB, SwingLookback);
                if (swingLow > 0)
                {
                    decimal slBySwing = swingLow * (1m - SwingStopExtraBufferPercent);
                    sl = Math.Min(slByEma, slBySwing);
                }
            }

            bool allowMode1 = (coinInfo.IsMajor && EnableMarketOnStrongRejectForMajor) ||
                              (!coinInfo.IsMajor && EnableMarketOnStrongRejectForAlt);

            bool strongReject = allowMode1 && rejectB && IsStrongRejectionLong(candles15m, iB);

            decimal entry = strongReject
                ? B.Close * (1m + MarketableLimitOffset) // marketable limit
                : anchor * (1m + EntryOffsetPercent);    // limit theo anchor

            // V4: max distance to anchor (tránh vào trễ)
            decimal maxDist = coinInfo.IsMajor ? MaxEntryDistanceToAnchorMajor : MaxEntryDistanceToAnchorAlt;
            if (anchor > 0)
            {
                decimal dist = Math.Abs(entry - anchor) / anchor;
                if (dist > maxDist)
                {
                    return new TradeSignal
                    {
                        Type = SignalType.None,
                        Reason = $"{coinInfo.Symbol}: V4 NO LONG – Entry quá xa anchor (dist={dist:P2} > {maxDist:P2}).",
                        Symbol = coinInfo.Symbol
                    };
                }
            }

            if (sl >= entry || sl <= 0)
            {
                return new TradeSignal
                {
                    Type = SignalType.None,
                    Reason = $"{coinInfo.Symbol}: V4 Long SL invalid (entry={entry:F6}, sl={sl:F6}).",
                    Symbol = coinInfo.Symbol
                };
            }

            decimal risk = entry - sl;
            decimal tp = entry + risk * riskRewardTrend;

            string modeTag = strongReject ? "MODE1" : "LIMIT";
            return new TradeSignal
            {
                Type = SignalType.Long,
                EntryPrice = entry,
                StopLoss = sl,
                TakeProfit = tp,
                Reason = $"{coinInfo.Symbol}: V4 LONG – 2-step retest(A)+confirm(B) @EMA({anchor:F6}) + {(strongReject ? "StrongReject" : (momentumHard ? "MomHard" : "Reject"))}. Entry={modeTag}.",
                Symbol = coinInfo.Symbol
            };
        }

        // =====================================================================
        //                       V4 WINRATE SHORT (2-STEP)
        // =====================================================================

        private TradeSignal BuildShortV4Winrate(
            IReadOnlyList<Candle> candles15m,
            IReadOnlyList<decimal> ema34_15,
            IReadOnlyList<decimal> ema89_15,
            IReadOnlyList<decimal> ema200_15,
            IReadOnlyList<decimal> rsi15,
            IReadOnlyList<decimal> macd15,
            IReadOnlyList<decimal> sig15,
            CoinInfo coinInfo,
            decimal riskRewardTrend,
            bool volumeOkSoft)
        {
            int iB = candles15m.Count - 2;    // B = confirm candle
            int iA = iB - 1;                  // A = retest candle
            if (iA <= 1) return new TradeSignal();

            var A = candles15m[iA];
            var B = candles15m[iB];

            decimal ema34 = ema34_15[iB];
            decimal ema89 = ema89_15[iB];
            decimal ema200 = ema200_15[iB];

            // RSI cap cho TREND RETEST
            if (rsi15[iB] < TrendRetestRsiMinForShort)
            {
                return new TradeSignal
                {
                    Type = SignalType.None,
                    Reason = $"{coinInfo.Symbol}: V4 NO SHORT – RSI quá thấp ({rsi15[iB]:F1}) cho trend retest.",
                    Symbol = coinInfo.Symbol
                };
            }

            // 1) RESISTANCE dynamic (EMA trên giá B.close)
            var resistances = new List<decimal>();
            if (ema34 > 0 && ema34 > B.Close) resistances.Add(ema34);
            if (ema89 > 0 && ema89 > B.Close) resistances.Add(ema89);
            if (ema200 > 0 && ema200 > B.Close) resistances.Add(ema200);

            if (resistances.Count == 0)
            {
                return new TradeSignal
                {
                    Type = SignalType.None,
                    Reason = $"{coinInfo.Symbol}: Downtrend nhưng không có EMA resistance trên giá.",
                    Symbol = coinInfo.Symbol
                };
            }

            decimal anchor = resistances.Min();
            decimal confirmBeyond = coinInfo.IsMajor ? ConfirmCloseBeyondAnchorMajor : ConfirmCloseBeyondAnchorAlt;

            // 2) A touch anchor (retest)
            bool touchA =
                anchor > 0m &&
                A.High >= anchor * (1 - EmaRetestBand) &&
                A.High <= anchor * (1 + EmaRetestBand);

            // 3) Anti-squeeze: check bullish impulse ở nến A
            if (EnableAntiSqueezeShort && touchA && IsBullishImpulseAtRetest(candles15m, iA, anchor))
            {
                return new TradeSignal
                {
                    Type = SignalType.None,
                    Reason = $"{coinInfo.Symbol}: V4 NO SHORT – AntiSqueeze at retest(A) near EMA({anchor:F6}).",
                    Symbol = coinInfo.Symbol
                };
            }

            // 4) B confirm (đóng bearish + đóng dưới anchor rõ + phá low nhỏ)
            bool bearishB = B.Close < B.Open;
            bool closeBackBelow = B.Close <= anchor * (1m - confirmBeyond);
            bool breakSmall = B.Low < A.Low;

            // 5) Reject truyền thống
            bool rejectB =
                bearishB &&
                B.High > anchor &&
                B.Close < anchor;

            // 6) Momentum
            bool macdCrossDown = macd15[iB] < sig15[iB] && macd15[iB - 1] >= sig15[iB - 1];
            bool rsiBear = rsi15[iB] < RsiBearThreshold && rsi15[iB] <= rsi15[iB - 1];

            bool bodyStrongB = IsBodyStrong(B, ConfirmBodyToRangeMin);

            bool momentumHard = macdCrossDown && rsiBear && bodyStrongB && closeBackBelow;

            // 7) Điều kiện vào: 2-step + (rejectB hoặc momentumHard)
            bool ok = touchA && bearishB && closeBackBelow && breakSmall && rejectB;

            if (!ok && AllowMomentumInsteadOfReject)
            {
                ok = touchA && bearishB && closeBackBelow && breakSmall && momentumHard && volumeOkSoft;
            }

            if (!ok)
            {
                return new TradeSignal
                {
                    Type = SignalType.None,
                    Reason = $"{coinInfo.Symbol}: V4 Short chưa đạt (touchA={touchA}, bearishB={bearishB}, close>{confirmBeyond:P2}Below={closeBackBelow}, break={breakSmall}, rejectB={rejectB}, momHard={momentumHard}, volOk={volumeOkSoft}).",
                    Symbol = coinInfo.Symbol
                };
            }

            // SL dynamic
            decimal slByEma = anchor * (1m + AnchorSlBufferPercent);

            decimal sl = slByEma;
            if (UseSwingForTrendStop)
            {
                decimal swingHigh = PriceActionHelper.FindSwingHigh(candles15m, iB, SwingLookback);
                if (swingHigh > 0)
                {
                    decimal slBySwing = swingHigh * (1m + SwingStopExtraBufferPercent);
                    sl = Math.Max(slByEma, slBySwing);
                }
            }

            bool allowMode1 = (coinInfo.IsMajor && EnableMarketOnStrongRejectForMajor) ||
                              (!coinInfo.IsMajor && EnableMarketOnStrongRejectForAlt);

            bool strongReject = allowMode1 && rejectB && IsStrongRejectionShort(candles15m, iB);

            decimal entry = strongReject
                ? B.Close * (1m - MarketableLimitOffset)
                : anchor * (1m - EntryOffsetPercent);

            // V4: max distance to anchor
            decimal maxDist = coinInfo.IsMajor ? MaxEntryDistanceToAnchorMajor : MaxEntryDistanceToAnchorAlt;
            if (anchor > 0)
            {
                decimal dist = Math.Abs(entry - anchor) / anchor;
                if (dist > maxDist)
                {
                    return new TradeSignal
                    {
                        Type = SignalType.None,
                        Reason = $"{coinInfo.Symbol}: V4 NO SHORT – Entry quá xa anchor (dist={dist:P2} > {maxDist:P2}).",
                        Symbol = coinInfo.Symbol
                    };
                }
            }

            if (sl <= entry)
            {
                return new TradeSignal
                {
                    Type = SignalType.None,
                    Reason = $"{coinInfo.Symbol}: V4 Short SL invalid (entry={entry:F6}, sl={sl:F6}).",
                    Symbol = coinInfo.Symbol
                };
            }

            decimal risk = sl - entry;
            decimal tp = entry - risk * riskRewardTrend;

            string modeTag = strongReject ? "MODE1" : "LIMIT";
            return new TradeSignal
            {
                Type = SignalType.Short,
                EntryPrice = entry,
                StopLoss = sl,
                TakeProfit = tp,
                Reason = $"{coinInfo.Symbol}: V4 SHORT – 2-step retest(A)+confirm(B) @EMA({anchor:F6}) + {(strongReject ? "StrongReject" : (momentumHard ? "MomHard" : "Reject"))}. Entry={modeTag}.",
                Symbol = coinInfo.Symbol
            };
        }

        // =====================================================================
        //                        SIDEWAY SCALP (15M) - giữ nguyên
        // =====================================================================

        private TradeSignal BuildSidewayScalp(
            IReadOnlyList<Candle> candles15m,
            IReadOnlyList<decimal> ema34_15,
            IReadOnlyList<decimal> ema89_15,
            IReadOnlyList<decimal> ema200_15,
            IReadOnlyList<decimal> rsi15,
            IReadOnlyList<decimal> macd15,
            IReadOnlyList<decimal> sig15,
            Candle last15,
            CoinInfo coinInfo,
            bool h1BiasUp,
            bool h1BiasDown,
            decimal riskRewardSideway)
        {
            int i15 = candles15m.Count - 2;
            if (i15 <= 0) return new TradeSignal();

            decimal ema34 = ema34_15[i15];
            decimal ema89 = ema89_15[i15];
            decimal ema200 = ema200_15[i15];

            bool shortBias15 = ema34 <= ema89 && ema34 <= ema200;
            bool longBias15 = ema34 >= ema89 && ema34 >= ema200;

            bool shortBias;
            bool longBias;

            if (h1BiasDown)
            {
                shortBias = true;
                longBias = false;
            }
            else if (h1BiasUp)
            {
                longBias = true;
                shortBias = false;
            }
            else
            {
                shortBias = shortBias15;
                longBias = longBias15;
            }

            if (!shortBias && !longBias)
            {
                return new TradeSignal
                {
                    Type = SignalType.None,
                    Reason = $"{coinInfo.Symbol}: SIDEWAY – không có bias rõ.",
                    Symbol = coinInfo.Symbol
                };
            }

            if (shortBias)
            {
                var resistances = new List<decimal>();
                if (ema89 > last15.Close) resistances.Add(ema89);
                if (ema200 > last15.Close) resistances.Add(ema200);

                if (resistances.Count == 0)
                    return new TradeSignal();

                decimal nearestRes = resistances.Min();

                bool touchRes =
                    last15.High >= nearestRes * (1 - EmaRetestBand) &&
                    last15.High <= nearestRes * (1 + EmaRetestBand);

                bool bearBody = last15.Close <= last15.Open;
                bool reject = last15.High > nearestRes && last15.Close < nearestRes && bearBody;

                bool rsiHigh = rsi15[i15] >= 55m;
                bool rsiTurnDown = rsi15[i15] <= rsi15[i15 - 1];
                bool macdDownOrFlatScalp = macd15[i15] <= macd15[i15 - 1];

                bool momentum = rsiHigh && (rsiTurnDown || macdDownOrFlatScalp);

                if (!(touchRes && reject && momentum))
                    return new TradeSignal();

                decimal rawEntry = last15.Close * (1 + EntryOffsetPercentForScal);

                decimal swingHigh = PriceActionHelper.FindSwingHigh(candles15m, i15, SwingLookback);

                decimal entry = rawEntry;
                if (swingHigh > 0 && entry >= swingHigh)
                    entry = (last15.Close + swingHigh) / 2;

                decimal sl = (swingHigh > 0 ? swingHigh : last15.High) + entry * StopBufferPercent;

                if (sl <= entry)
                    return new TradeSignal();

                decimal risk = sl - entry;
                decimal tp = entry - risk * riskRewardSideway;

                return new TradeSignal
                {
                    Type = SignalType.Short,
                    EntryPrice = entry,
                    StopLoss = sl,
                    TakeProfit = tp,
                    Reason = $"{coinInfo.Symbol}: SIDEWAY SCALP SHORT – retest EMA + reject + RSI/MACD quay đầu.",
                    Symbol = coinInfo.Symbol
                };
            }

            {
                var supports = new List<decimal>();
                if (ema89 < last15.Close) supports.Add(ema89);
                if (ema200 < last15.Close) supports.Add(ema200);

                if (supports.Count == 0)
                    return new TradeSignal();

                decimal nearestSup = supports.Max();

                bool touchSup =
                    last15.Low <= nearestSup * (1 + EmaRetestBand) &&
                    last15.Low >= nearestSup * (1 - EmaRetestBand);

                bool bullBody = last15.Close >= last15.Open;
                bool reject = last15.Low < nearestSup && last15.Close > nearestSup && bullBody;

                bool rsiHighEnough = rsi15[i15] >= 45m;
                bool rsiTurnUp = rsi15[i15] >= rsi15[i15 - 1];
                bool macdUpOrFlatScalp = macd15[i15] >= macd15[i15 - 1];

                bool momentum = rsiHighEnough && (rsiTurnUp || macdUpOrFlatScalp);

                if (!(touchSup && reject && momentum))
                    return new TradeSignal();

                decimal rawEntry = last15.Close * (1 - EntryOffsetPercentForScal);

                decimal swingLow = PriceActionHelper.FindSwingLow(candles15m, i15, SwingLookback);

                decimal entry = rawEntry;
                if (swingLow > 0 && entry <= swingLow)
                    entry = (last15.Close + swingLow) / 2;

                decimal sl = (swingLow > 0 ? swingLow : last15.Low) - entry * StopBufferPercent;

                if (sl >= entry || sl <= 0)
                    return new TradeSignal();

                decimal risk = entry - sl;
                decimal tp = entry + risk * riskRewardSideway;

                return new TradeSignal
                {
                    Type = SignalType.Long,
                    EntryPrice = entry,
                    StopLoss = sl,
                    TakeProfit = tp,
                    Reason = $"{coinInfo.Symbol}: SIDEWAY SCALP LONG – retest EMA + reject + RSI/MACD quay đầu.",
                    Symbol = coinInfo.Symbol
                };
            }
        }

        // =====================================================================
        //                    MODE 1 HELPERS: STRONG REJECTION
        // =====================================================================

        private bool IsStrongRejectionLong(IReadOnlyList<Candle> candles15m, int idxClosed)
        {
            if (idxClosed <= 0 || idxClosed >= candles15m.Count) return false;

            var c = candles15m[idxClosed];
            decimal range = c.High - c.Low;
            if (range <= 0) return false;

            decimal body = Math.Abs(c.Close - c.Open);
            decimal bodySafe = Math.Max(body, range * 0.10m);
            decimal lowerWick = Math.Min(c.Open, c.Close) - c.Low;

            bool wickOk = lowerWick / bodySafe >= StrongRejectWickToBody;

            decimal closePosFromHigh = (c.High - c.Close) / range;
            bool closeOk = closePosFromHigh <= StrongRejectCloseInRange;

            decimal volUsd = c.Close * c.Volume;
            decimal median = GetMedianVolUsd(candles15m, idxClosed, VolumeMedianLookback);
            bool volOk = median <= 0 || (volUsd / median) >= StrongRejectMinVolVsMedian;

            return wickOk && closeOk && volOk;
        }

        private bool IsStrongRejectionShort(IReadOnlyList<Candle> candles15m, int idxClosed)
        {
            if (idxClosed <= 0 || idxClosed >= candles15m.Count) return false;

            var c = candles15m[idxClosed];
            decimal range = c.High - c.Low;
            if (range <= 0) return false;

            decimal body = Math.Abs(c.Close - c.Open);
            decimal bodySafe = Math.Max(body, range * 0.10m);
            decimal upperWick = c.High - Math.Max(c.Open, c.Close);

            bool wickOk = upperWick / bodySafe >= StrongRejectWickToBody;

            decimal closePosFromLow = (c.Close - c.Low) / range;
            bool closeOk = closePosFromLow <= StrongRejectCloseInRange;

            decimal volUsd = c.Close * c.Volume;
            decimal median = GetMedianVolUsd(candles15m, idxClosed, VolumeMedianLookback);
            bool volOk = median <= 0 || (volUsd / median) >= StrongRejectMinVolVsMedian;

            return wickOk && closeOk && volOk;
        }

        // =====================================================================
        //                     PATCH HELPERS: ANTI-IMPULSE AT RETEST
        // =====================================================================

        private bool IsBullishImpulseAtRetest(IReadOnlyList<Candle> candles15m, int idxClosed, decimal anchorResistance)
        {
            if (idxClosed <= 0 || idxClosed >= candles15m.Count) return false;

            var c = candles15m[idxClosed];
            if (anchorResistance <= 0) return false;

            decimal dist = Math.Abs(c.Close - anchorResistance) / anchorResistance;
            if (dist > AntiImpulseMaxDistToAnchor) return false;

            decimal range = c.High - c.Low;
            if (range <= 0) return false;

            bool bullish = c.Close > c.Open;
            if (!bullish) return false;

            decimal body = Math.Abs(c.Close - c.Open);
            decimal bodyToRange = body / range;
            if (bodyToRange < AntiImpulseBodyToRangeMin) return false;

            decimal closePosFromHigh = (c.High - c.Close) / range;
            if (closePosFromHigh > AntiImpulseCloseNearEdgeMax) return false;

            decimal volUsd = c.Close * c.Volume;
            decimal median = GetMedianVolUsd(candles15m, idxClosed, VolumeMedianLookback);
            if (median > 0)
            {
                decimal ratio = volUsd / median;
                if (ratio < AntiImpulseMinVolVsMedian) return false;
            }

            return true;
        }

        private bool IsBearishImpulseAtRetest(IReadOnlyList<Candle> candles15m, int idxClosed, decimal anchorSupport)
        {
            if (idxClosed <= 0 || idxClosed >= candles15m.Count) return false;

            var c = candles15m[idxClosed];
            if (anchorSupport <= 0) return false;

            decimal dist = Math.Abs(c.Close - anchorSupport) / anchorSupport;
            if (dist > AntiImpulseMaxDistToAnchor) return false;

            decimal range = c.High - c.Low;
            if (range <= 0) return false;

            bool bearish = c.Close < c.Open;
            if (!bearish) return false;

            decimal body = Math.Abs(c.Close - c.Open);
            decimal bodyToRange = body / range;
            if (bodyToRange < AntiImpulseBodyToRangeMin) return false;

            decimal closePosFromLow = (c.Close - c.Low) / range;
            if (closePosFromLow > AntiImpulseCloseNearEdgeMax) return false;

            decimal volUsd = c.Close * c.Volume;
            decimal median = GetMedianVolUsd(candles15m, idxClosed, VolumeMedianLookback);
            if (median > 0)
            {
                decimal ratio = volUsd / median;
                if (ratio < AntiImpulseMinVolVsMedian) return false;
            }

            return true;
        }

        // =====================================================================
        //                     HELPERS: CLIMAX / EMA / SIDEWAY
        // =====================================================================

        private bool IsClimaxCandle(IReadOnlyList<Candle> candles, int index)
        {
            if (index <= 0 || index >= candles.Count)
                return false;

            int start = Math.Max(0, index - ClimaxLookback);
            int end = index;
            int count = end - start;
            if (count <= 3)
                return false;

            decimal sumBody = 0m;
            decimal sumVolume = 0m;

            for (int i = start; i < end; i++)
            {
                var c = candles[i];
                sumBody += Math.Abs(c.Close - c.Open);
                sumVolume += c.Volume;
            }

            decimal avgBody = sumBody / count;
            decimal avgVolume = sumVolume / count;

            var last = candles[index];
            decimal body = Math.Abs(last.Close - last.Open);
            decimal volume = last.Volume;

            bool bigBody = avgBody > 0 && body >= avgBody * ClimaxBodyMultiplier;
            bool bigVolume = avgVolume > 0 && volume >= avgVolume * ClimaxVolumeMultiplier;

            return bigBody && bigVolume;
        }

        private decimal GetNearestEma(decimal price, decimal ema34, decimal ema89, decimal ema200)
        {
            var list = new List<decimal>();
            if (ema34 > 0) list.Add(ema34);
            if (ema89 > 0) list.Add(ema89);
            if (ema200 > 0) list.Add(ema200);

            if (list.Count == 0)
                return 0m;

            decimal nearest = list[0];
            decimal minDist = Math.Abs(price - nearest);

            for (int i = 1; i < list.Count; i++)
            {
                decimal d = Math.Abs(price - list[i]);
                if (d < minDist)
                {
                    minDist = d;
                    nearest = list[i];
                }
            }

            return nearest;
        }

        private bool IsClimaxAwayFromEma(
            IReadOnlyList<Candle> candles,
            int index,
            decimal ema34,
            decimal ema89,
            decimal ema200)
        {
            if (index <= 0 || index >= candles.Count)
                return false;

            if (!IsClimaxCandle(candles, index))
                return false;

            var c = candles[index];
            decimal nearestEma = GetNearestEma(c.Close, ema34, ema89, ema200);
            if (nearestEma <= 0)
                return false;

            var distance = Math.Abs(c.Close - nearestEma) / nearestEma;
            return distance >= OverextendedFromEmaPercent;
        }

        private bool IsSidewayStrong(IReadOnlyList<Candle> candles, IReadOnlyList<decimal> ema34, IReadOnlyList<decimal> ema89)
        {
            if (candles.Count < SidewaySlopeLookback + SidewayConfirmBars + 5)
                return false;

            int idx = candles.Count - 2;

            int hits = 0;
            for (int t = 0; t < SidewayConfirmBars; t++)
            {
                int i = idx - t;
                if (i - SidewaySlopeLookback < 0) break;

                var price = candles[i].Close;
                if (price <= 0) continue;

                decimal dist = Math.Abs(ema34[i] - ema89[i]) / price;

                int start = i - SidewaySlopeLookback;
                decimal emaStart = ema34[start];
                decimal emaEnd = ema34[i];
                if (emaStart <= 0) continue;

                decimal slope = (emaEnd - emaStart) / emaStart;

                bool distOk = dist < SidewayEmaDistThreshold;
                bool slopeOk = Math.Abs(slope) < SidewaySlopeThreshold;

                if (distOk && slopeOk) hits++;
            }

            return hits >= SidewayConfirmBars;
        }

        private decimal GetMedianVolUsd(IReadOnlyList<Candle> candles15m, int endIdx, int lookback)
        {
            int start = Math.Max(0, endIdx - lookback + 1);
            var list = new List<decimal>();

            for (int i = start; i <= endIdx; i++)
            {
                var c = candles15m[i];
                if (c.Close > 0 && c.Volume > 0)
                    list.Add(c.Close * c.Volume);
            }

            if (list.Count == 0) return 0m;
            list.Sort();
            int mid = list.Count / 2;
            return (list.Count % 2 == 1) ? list[mid] : (list[mid - 1] + list[mid]) / 2m;
        }

        // =====================================================================
        //                MARKET STRUCTURE HELPERS (Lower-High / Higher-Low)
        // =====================================================================

        private bool IsSwingHigh(IReadOnlyList<Candle> candles, int i, int strength)
        {
            if (i - strength < 0 || i + strength >= candles.Count) return false;

            var h = candles[i].High;
            for (int k = 1; k <= strength; k++)
            {
                if (h <= candles[i - k].High) return false;
                if (h <= candles[i + k].High) return false;
            }
            return true;
        }

        private bool IsSwingLow(IReadOnlyList<Candle> candles, int i, int strength)
        {
            if (i - strength < 0 || i + strength >= candles.Count) return false;

            var l = candles[i].Low;
            for (int k = 1; k <= strength; k++)
            {
                if (l >= candles[i - k].Low) return false;
                if (l >= candles[i + k].Low) return false;
            }
            return true;
        }

        private List<(int idx, decimal price)> GetRecentSwingHighs(IReadOnlyList<Candle> candles, int endIdx, int maxLookback, int strength)
        {
            var res = new List<(int, decimal)>();
            int start = Math.Max(0, endIdx - maxLookback);

            for (int i = endIdx - strength; i >= start + strength; i--)
            {
                if (IsSwingHigh(candles, i, strength))
                {
                    res.Add((i, candles[i].High));
                    if (res.Count >= 5) break;
                }
            }
            return res;
        }

        private List<(int idx, decimal price)> GetRecentSwingLows(IReadOnlyList<Candle> candles, int endIdx, int maxLookback, int strength)
        {
            var res = new List<(int, decimal)>();
            int start = Math.Max(0, endIdx - maxLookback);

            for (int i = endIdx - strength; i >= start + strength; i--)
            {
                if (IsSwingLow(candles, i, strength))
                {
                    res.Add((i, candles[i].Low));
                    if (res.Count >= 5) break;
                }
            }
            return res;
        }

        private (bool lowerHigh, bool higherLow, decimal lastSwingHigh, decimal prevSwingHigh, decimal lastSwingLow, decimal prevSwingLow)
            DetectMarketStructure(IReadOnlyList<Candle> candles15m, int i15, decimal tol)
        {
            var highs = GetRecentSwingHighs(candles15m, i15, StructureMaxLookbackBars, StructureSwingStrength);
            var lows = GetRecentSwingLows(candles15m, i15, StructureMaxLookbackBars, StructureSwingStrength);

            decimal lastH = 0m, prevH = 0m, lastL = 0m, prevL = 0m;
            bool lh = false, hl = false;

            if (highs.Count >= StructureNeedSwings)
            {
                lastH = highs[0].price;
                prevH = highs[1].price;
                lh = lastH < prevH * (1m - tol);
            }

            if (lows.Count >= StructureNeedSwings)
            {
                lastL = lows[0].price;
                prevL = lows[1].price;
                hl = lastL > prevL * (1m + tol);
            }

            return (lh, hl, lastH, prevH, lastL, prevL);
        }

        // =====================================================================
        //                               V4 HELPERS
        // =====================================================================

        private bool IsBodyStrong(Candle c, decimal minBodyToRange)
        {
            decimal range = c.High - c.Low;
            if (range <= 0) return false;

            decimal body = Math.Abs(c.Close - c.Open);
            decimal ratio = body / range;
            return ratio >= minBodyToRange;
        }

        private bool IsEmaSlopeOk(IReadOnlyList<decimal> ema, int idx, int lookback, decimal minAbsSlopeRatio)
        {
            if (idx - lookback < 0) return false;
            decimal start = ema[idx - lookback];
            decimal end = ema[idx];
            if (start <= 0) return false;

            decimal slope = Math.Abs(end - start) / start;
            return slope >= minAbsSlopeRatio;
        }
    }
}
