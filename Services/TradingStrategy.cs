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
    /// TradingStrategy V4 WINRATE PATCH + MTF (TF-agnostic)
    ///
    /// MAPPING (TF-agnostic, không fix cứng 15m/3m nữa):
    /// GenerateSignal(candlesTrend, candlesEntry, coinInfo)
    /// - candlesTrend: Trend TF (ví dụ 15m / 30m / 1h...)
    /// - candlesEntry: Entry TF (ví dụ 3m / 5m / 15m...)
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

        // Retest band
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
        private const decimal EntryOffsetPercent = 0.003m;          // 0.3% cho trend
        private const decimal EntryOffsetPercentForScal = 0.001m;   // 0.1% cho scalp

        // SL neo EMA cho trend
        private const decimal AnchorSlBufferPercent = 0.0015m;      // 0.15%

        // OPTION: dùng swing để đặt SL "an toàn hơn" (tránh quét EMA)
        private const bool UseSwingForTrendStop = true;
        private const int SwingLookback = 5;
        private const decimal SwingStopExtraBufferPercent = 0.0010m; // +0.10% quanh swing

        // Nến climax + overextended xa EMA gần nhất (tránh vừa vào là đảo)
        private const int ClimaxLookback = 20;
        private const decimal ClimaxBodyMultiplier = 1.8m;
        private const decimal ClimaxVolumeMultiplier = 1.5m;
        private const decimal OverextendedFromEmaPercent = 0.01m; // 1% xa EMA gần nhất

        // ========================= BTC vs ALT CONFIG ====================

        // Volume gate uses TREND candle (đặt theo USDT value của 1 nến trend)
        private const decimal MinMajorVolumeUsdTrend = 2_000_000m; // BTC/ETH
        private const decimal MinAltVolumeUsdTrend = 600_000m;     // Altcoin

        private const int VolumeMedianLookback = 40;
        private const decimal MinVolumeVsMedianRatioMajor = 0.55m;
        private const decimal MinVolumeVsMedianRatioAlt = 0.65m;

        // Trend slope (on TREND EMA89)
        private const int EmaSlopeLookbackTrend = 6;
        private const decimal MinAltEmaSlopeTrend = 0.0025m;   // 0.25%
        private const decimal MinMajorEmaSlopeTrend = 0.0015m; // 0.15%

        // V4: EMA separation (TREND: ema89 vs ema200)
        private const decimal MinEmaSeparationMajor = 0.0020m; // 0.20%
        private const decimal MinEmaSeparationAlt = 0.0030m;   // 0.30%

        // ========================= V4: 2-STEP RETEST + CONFIRM (ENTRY TF) =========================
        private const decimal ConfirmCloseBeyondAnchorMajor = 0.0008m; // 0.08%
        private const decimal ConfirmCloseBeyondAnchorAlt = 0.0012m;   // 0.12%
        private const decimal ConfirmBodyToRangeMin = 0.55m;           // confirm candle mạnh

        // ========================= V4: MAX DISTANCE TO ANCHOR (ENTRY TF) =========================
        private const decimal MaxEntryDistanceToAnchorMajor = 0.0035m; // 0.35%
        private const decimal MaxEntryDistanceToAnchorAlt = 0.0050m;   // 0.50%

        // ========================= ANTI-LATE ENTRY NEAR EXTREMUM (TREND TF) =========================
        private const int RecentExtremumLookback = 30;
        private const decimal MinDistFromRecentLowMajor = 0.0040m;    // 0.40%
        private const decimal MinDistFromRecentLowAlt = 0.0060m;      // 0.60%
        private const decimal MinDistFromRecentHighMajor = 0.0040m;   // 0.40%
        private const decimal MinDistFromRecentHighAlt = 0.0060m;     // 0.60%

        // ========================= SIDEWAY FILTER =========================
        private const int SidewaySlopeLookback = 10;
        private const decimal SidewayEmaDistThreshold = 0.0015m; // 0.15%
        private const decimal SidewaySlopeThreshold = 0.002m;    // 0.2%
        private const int SidewayConfirmBars = 3;

        // ========================= MARKET STRUCTURE (TREND TF) =========================
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

        private const decimal MarketableLimitOffset = 0.0002m; // 0.02%

        // ========================= MODE 2: PULLBACK -> REJECT -> CONTINUATION (TREND TF) =========================
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

        // ========================= PATCH: ANTI-SQUEEZE / ANTI-DUMP (ENTRY TF) =========================
        private const bool EnableAntiSqueezeShort = true;
        private const bool EnableAntiDumpLong = true;

        private const decimal AntiImpulseBodyToRangeMin = 0.65m;
        private const decimal AntiImpulseCloseNearEdgeMax = 0.20m;
        private const decimal AntiImpulseMaxDistToAnchor = 0.0025m;

        // ========================= HUMAN-LIKE FILTER (TREND TF) =========================
        private const bool EnableHumanLikeFilter = true;

        private const decimal DangerZoneBottomRatio = 0.30m;  // bottom 30% recent range -> WAIT (SHORT)
        private const decimal DangerZoneTopRatio = 0.30m;     // top 30% recent range -> WAIT (LONG)
        private const int HumanRecentRangeLookback = 40;

        private const int ExtremumTouchLookback = 30;
        private const int MinTouchesToBlock = 2;
        private const decimal ExtremumTouchEpsMajor = 0.0012m; // 0.12%
        private const decimal ExtremumTouchEpsAlt = 0.0018m;   // 0.18%

        private const int DivergenceLookback = 12;
        private const decimal MinRsiDivergenceGap = 3.0m;

        // ========================= DYNAMIC RR =========================
        private const bool EnableDynamicRiskReward = true;

        private const decimal TrendRR_MinMajor = 1.25m;
        private const decimal TrendRR_MaxMajor = 2.00m;

        private const decimal TrendRR_MinAlt = 1.15m;
        private const decimal TrendRR_MaxAlt = 1.60m;

        // =====================================================================
        //                           ENTRY SIGNAL (MTF)
        // =====================================================================

        public TradeSignal GenerateSignal(IReadOnlyList<Candle> candlesTrend, IReadOnlyList<Candle> candlesEntry, CoinInfo coinInfo)
        {
            if (candlesTrend == null || candlesEntry == null ||
                candlesTrend.Count < MinBars || candlesEntry.Count < MinBars ||
                coinInfo == null)
                return new TradeSignal();

            bool isMajor = coinInfo.IsMajor;

            // closed candles
            int iT = candlesTrend.Count - 2;
            int iE = candlesEntry.Count - 2;
            if (iT <= 2 || iE <= 2) return new TradeSignal();

            var lastT = candlesTrend[iT];
            var lastE = candlesEntry[iE];

            // ================== TREND TF INDICATORS ==================
            var ema34_T = _indicators.Ema(candlesTrend, 34);
            var ema89_T = _indicators.Ema(candlesTrend, 89);
            var ema200_T = _indicators.Ema(candlesTrend, 200);

            var rsiT = _indicators.Rsi(candlesTrend, 6);
            var (macdT, sigT, _) = _indicators.Macd(candlesTrend, 5, 13, 5);

            decimal ema89T = ema89_T[iT];
            decimal ema200T = ema200_T[iT];

            // ================== ENTRY TF INDICATORS ==================
            var ema34_E = _indicators.Ema(candlesEntry, 34);
            var ema89_E = _indicators.Ema(candlesEntry, 89);

            var rsiE = _indicators.Rsi(candlesEntry, 6);
            var (macdE, sigE, _) = _indicators.Macd(candlesEntry, 5, 13, 5);

            // =================== BTC vs ALT PROFILE =======================

            decimal rrTrend = isMajor ? RiskRewardMajor : RiskReward;
            decimal rrSideway = isMajor ? RiskRewardSidewayMajor : RiskRewardSideway;
            bool allowSideway = isMajor;

            // Volume gate uses TREND candle
            decimal volUsdTrend = lastT.Close * lastT.Volume;
            decimal medianVolUsd = GetMedianVolUsd(candlesTrend, iT, VolumeMedianLookback);
            decimal ratioVsMedian = medianVolUsd > 0 ? (volUsdTrend / medianVolUsd) : 1m;

            if (isMajor)
            {
                if (volUsdTrend < MinMajorVolumeUsdTrend && ratioVsMedian < MinVolumeVsMedianRatioMajor)
                {
                    return new TradeSignal
                    {
                        Type = SignalType.None,
                        Reason = $"{coinInfo.Symbol}: Volume Trend yếu ({volUsdTrend:F0} USDT, vsMedian={ratioVsMedian:P0}) → bỏ qua.",
                        Symbol = coinInfo.Symbol
                    };
                }
            }
            else
            {
                if (volUsdTrend < MinAltVolumeUsdTrend && ratioVsMedian < MinVolumeVsMedianRatioAlt)
                {
                    return new TradeSignal
                    {
                        Type = SignalType.None,
                        Reason = $"{coinInfo.Symbol}: Alt volume Trend yếu ({volUsdTrend:F0} USDT, vsMedian={ratioVsMedian:P0}) → bỏ qua.",
                        Symbol = coinInfo.Symbol
                    };
                }

                // Alt: slope on TREND EMA89
                if (!IsEmaSlopeOk(ema89_T, iT, EmaSlopeLookbackTrend, MinAltEmaSlopeTrend))
                {
                    return new TradeSignal
                    {
                        Type = SignalType.None,
                        Reason = $"{coinInfo.Symbol}: Alt sideway, EMA89 Trend slope yếu → NO TRADE.",
                        Symbol = coinInfo.Symbol
                    };
                }
            }

            // =================== SIDEWAY FILTER (TREND + ENTRY) ===========================

            bool sidewayTrend = IsSidewayStrong(candlesTrend, ema34_T, ema89_T);
            bool sidewayEntry = IsSidewayStrong(candlesEntry, ema34_E, ema89_E);

            if (!isMajor && (sidewayTrend || sidewayEntry))
            {
                return new TradeSignal
                {
                    Type = SignalType.None,
                    Reason = $"{coinInfo.Symbol}: Altcoin SIDEWAY mạnh trên {(sidewayTrend ? "TrendTF" : "EntryTF")} → NO TRADE.",
                    Symbol = coinInfo.Symbol
                };
            }

            // ================= FILTER: CLIMAX + OVEREXTENDED (TREND TF) =================

            bool climaxDanger =
                IsClimaxAwayFromEma(candlesTrend, iT, ema34_T[iT], ema89T, ema200T) ||
                IsClimaxAwayFromEma(candlesTrend, iT - 1, ema34_T[iT], ema89T, ema200T);

            if (climaxDanger)
            {
                return new TradeSignal
                {
                    Type = SignalType.None,
                    Reason = $"{coinInfo.Symbol}: Climax + overextended xa EMA (TrendTF) → chờ retest.",
                    Symbol = coinInfo.Symbol
                };
            }

            // =================== TREND FILTER (TREND TF EMA89/200) ======================

            bool trendUp =
                lastT.Close > ema89T &&
                ema89T > ema200T;

            bool trendDown =
                lastT.Close < ema89T &&
                ema89T < ema200T;

            // extra: avoid chop around ema200
            decimal distTo200 = ema200T > 0 ? Math.Abs(lastT.Close - ema200T) / ema200T : 0m;
            if (distTo200 < 0.0012m) // 0.12% near EMA200 => chop zone
            {
                return new TradeSignal
                {
                    Type = SignalType.None,
                    Reason = $"{coinInfo.Symbol}: TrendTF gần EMA200 (dist={distTo200:P2}) → NO TRADE (chop zone).",
                    Symbol = coinInfo.Symbol
                };
            }

            // =================== ENTRY ALIGN (ENTRY TF EMA34/89) ======================

            bool entryBiasUp = lastE.Close > ema34_E[iE] && ema34_E[iE] > ema89_E[iE];
            bool entryBiasDown = lastE.Close < ema34_E[iE] && ema34_E[iE] < ema89_E[iE];

            bool upTrend = trendUp && entryBiasUp;
            bool downTrend = trendDown && entryBiasDown;

            // Extreme cases (use TREND TF, avoid chasing)
            bool extremeUp =
                lastT.Close > ema89T * (1 + ExtremeEmaBoost) &&
                macdT[iT] > sigT[iT] &&
                rsiT[iT] > ExtremeRsiHigh;

            bool extremeDump =
                lastT.Close < ema89T * (1 - ExtremeEmaBoost) &&
                macdT[iT] < sigT[iT] &&
                rsiT[iT] < ExtremeRsiLow;

            // =================== MARKET STRUCTURE (TREND TF) ==================
            decimal stTol = isMajor ? StructureBreakToleranceMajor : StructureBreakToleranceAlt;
            var (lowerHigh, higherLow, lastSwingHigh, prevSwingHigh, lastSwingLow, prevSwingLow)
                = DetectMarketStructure(candlesTrend, iT, stTol);

            bool blockLongByStructure = lowerHigh && !higherLow;
            bool blockShortByStructure = higherLow && !lowerHigh;

            // =================== SIDEWAY / PULLBACK SCALP (TREND TF) ======================

            if (!extremeUp && !extremeDump)
            {
                bool shouldTrySideway =
                    (!trendUp && !trendDown) || sidewayTrend;

                if (allowSideway && shouldTrySideway)
                {
                    bool biasUp = trendUp;
                    bool biasDown = trendDown;

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
                        candlesTrend,
                        ema34_T,
                        ema89_T,
                        ema200_T,
                        rsiT,
                        macdT,
                        sigT,
                        lastT,
                        coinInfo,
                        biasUp,
                        biasDown,
                        rrSideway);

                    if (sidewaySignal.Type != SignalType.None)
                        return sidewaySignal;
                }
                else if (!isMajor && shouldTrySideway && !trendUp && !trendDown)
                {
                    return new TradeSignal
                    {
                        Type = SignalType.None,
                        Reason = $"{coinInfo.Symbol}: Altcoin sideway trên TrendTF → bỏ qua.",
                        Symbol = coinInfo.Symbol
                    };
                }
            }

            // =================== Pullback volume filter (ENTRY TF soft check) ===================
            int pullbackVolLookback = 5;
            decimal avgVolE = PriceActionHelper.AverageVolume(candlesEntry, iE - 1, pullbackVolLookback);
            bool volumeOkSoft = avgVolE <= 0 || lastE.Volume >= avgVolE * 0.7m;

            // =================== MODE 2 (MAJOR ONLY) - still on TREND TF ===================
            if (isMajor)
            {
                if (EnableBreakdownContinuationForMajor &&
                    (trendDown) &&
                    !blockShortByStructure &&
                    !extremeDump)
                {
                    var contShort = BuildBreakdownPullbackThenRejectShort(
                        candlesTrend,
                        iT,
                        rsiT[iT],
                        volUsdTrend,
                        medianVolUsd,
                        coinInfo);

                    if (contShort.Type != SignalType.None)
                        return contShort;
                }

                if (EnableBreakoutContinuationForMajor &&
                    (trendUp) &&
                    !blockLongByStructure &&
                    !extremeUp)
                {
                    var contLong = BuildBreakoutPullbackThenRejectLong(
                        candlesTrend,
                        iT,
                        rsiT[iT],
                        volUsdTrend,
                        medianVolUsd,
                        coinInfo);

                    if (contLong.Type != SignalType.None)
                        return contLong;
                }
            }

            // =================== TREND STRENGTH FILTER (TREND TF sep + slope) ===================
            if (trendUp || trendDown)
            {
                decimal price = lastT.Close > 0 ? lastT.Close : 1m;
                decimal sepTrend = Math.Abs(ema89T - ema200T) / price;

                decimal minSep = isMajor ? MinEmaSeparationMajor : MinEmaSeparationAlt;
                bool sepOk = sepTrend >= minSep;

                bool slopeOkNow = isMajor
                    ? IsEmaSlopeOk(ema89_T, iT, EmaSlopeLookbackTrend, MinMajorEmaSlopeTrend)
                    : true;

                if (!sepOk || !slopeOkNow)
                {
                    return new TradeSignal
                    {
                        Type = SignalType.None,
                        Reason = $"{coinInfo.Symbol}: NO TREND – TrendTF yếu/chop (sep={sepTrend:P2}, slopeOk={slopeOkNow}).",
                        Symbol = coinInfo.Symbol
                    };
                }

                if (EnableDynamicRiskReward)
                {
                    rrTrend = GetDynamicTrendRR(
                        isMajor: isMajor,
                        sepTrend: sepTrend,
                        slopeOk: slopeOkNow,
                        ratioVsMedian: ratioVsMedian,
                        volumeOkSoft: volumeOkSoft);

                    rrTrend = isMajor
                        ? Clamp(rrTrend, TrendRR_MinMajor, TrendRR_MaxMajor)
                        : Clamp(rrTrend, TrendRR_MinAlt, TrendRR_MaxAlt);
                }
            }

            // =================== BUILD LONG / SHORT (ENTRY TF) =========

            if (upTrend && !blockLongByStructure && !extremeUp)
            {
                var longSignal = BuildLongV4Winrate_EntryWithTrend(
                    candlesEntry,
                    candlesTrend,
                    ema34_E,
                    ema89_E,
                    rsiE,
                    macdE,
                    sigE,
                    rsiT,
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

            if (downTrend && !blockShortByStructure && !extremeDump)
            {
                var shortSignal = BuildShortV4Winrate_EntryWithTrend(
                    candlesEntry,
                    candlesTrend,
                    ema34_E,
                    ema89_E,
                    rsiE,
                    macdE,
                    sigE,
                    rsiT,
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
            IReadOnlyList<Candle> candlesTrend,
            int iT,
            decimal rsiNow,
            decimal volUsdTrend,
            decimal medianVolUsd,
            CoinInfo coinInfo)
        {
            if (iT < ContinuationLookback + 5) return new TradeSignal();
            if (rsiNow < ContinuationMinRsiForShort) return new TradeSignal();

            decimal ratio = medianVolUsd > 0 ? (volUsdTrend / medianVolUsd) : 1m;
            if (ratio < ContinuationMinVolVsMedian) return new TradeSignal();

            var c = candlesTrend[iT];       // C (rejection)
            var b = candlesTrend[iT - 1];   // B (pullback)

            decimal level = FindLowestLow(candlesTrend, iT - 2, ContinuationLookback);
            if (level <= 0) return new TradeSignal();

            int breakdownIdx = FindRecentBreakdownIndex(candlesTrend, iT - 1, level, ContinuationFindEventLookback);
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
            if (IsClimaxCandle(candlesTrend, iT)) return new TradeSignal();

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
                Symbol = coinInfo.Symbol,
                Mode = TradeMode.Mode2_Continuation
            };
        }

        private TradeSignal BuildBreakoutPullbackThenRejectLong(
            IReadOnlyList<Candle> candlesTrend,
            int iT,
            decimal rsiNow,
            decimal volUsdTrend,
            decimal medianVolUsd,
            CoinInfo coinInfo)
        {
            if (iT < ContinuationLookback + 5) return new TradeSignal();
            if (rsiNow > ContinuationMaxRsiForLong) return new TradeSignal();

            decimal ratio = medianVolUsd > 0 ? (volUsdTrend / medianVolUsd) : 1m;
            if (ratio < ContinuationMinVolVsMedian) return new TradeSignal();

            var c = candlesTrend[iT];       // C (rejection)
            var b = candlesTrend[iT - 1];   // B (pullback)

            decimal level = FindHighestHigh(candlesTrend, iT - 2, ContinuationLookback);
            if (level <= 0) return new TradeSignal();

            int breakoutIdx = FindRecentBreakoutIndex(candlesTrend, iT - 1, level, ContinuationFindEventLookback);
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
            if (IsClimaxCandle(candlesTrend, iT)) return new TradeSignal();

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
                Symbol = coinInfo.Symbol,
                Mode = TradeMode.Mode2_Continuation
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
                min = Math.Min(min, candles[i].Low);

            return min == decimal.MaxValue ? 0m : min;
        }

        private decimal FindHighestHigh(IReadOnlyList<Candle> candles, int endIdx, int lookback)
        {
            int start = Math.Max(0, endIdx - lookback + 1);
            decimal max = 0m;
            for (int i = start; i <= endIdx; i++)
                max = Math.Max(max, candles[i].High);

            return max;
        }

        // =====================================================================
        //                V4 WINRATE (ENTRY) + TREND FILTER (TREND)
        // =====================================================================

        private TradeSignal BuildLongV4Winrate_EntryWithTrend(
            IReadOnlyList<Candle> candlesEntry,
            IReadOnlyList<Candle> candlesTrend,
            IReadOnlyList<decimal> ema34_E,
            IReadOnlyList<decimal> ema89_E,
            IReadOnlyList<decimal> rsiE,
            IReadOnlyList<decimal> macdE,
            IReadOnlyList<decimal> sigE,
            IReadOnlyList<decimal> rsiT,
            CoinInfo coinInfo,
            decimal riskRewardTrend,
            bool volumeOkSoft)
        {
            int iB = candlesEntry.Count - 2; // confirm candle (closed)
            int iA = iB - 1;                 // retest candle (closed)
            if (iA <= 2) return new TradeSignal();

            var A = candlesEntry[iA];
            var B = candlesEntry[iB];

            if (rsiE[iB] > TrendRetestRsiMaxForLong)
            {
                return new TradeSignal
                {
                    Type = SignalType.None,
                    Reason = $"{coinInfo.Symbol}: NO LONG – RSI(EntryTF) quá cao ({rsiE[iB]:F1}) cho trend retest.",
                    Symbol = coinInfo.Symbol
                };
            }

            decimal ema34 = ema34_E[iB];
            decimal ema89 = ema89_E[iB];

            var supports = new List<decimal>();
            if (ema34 > 0 && ema34 < B.Close) supports.Add(ema34);
            if (ema89 > 0 && ema89 < B.Close) supports.Add(ema89);

            if (supports.Count == 0)
            {
                return new TradeSignal
                {
                    Type = SignalType.None,
                    Reason = $"{coinInfo.Symbol}: Uptrend nhưng không có EMA34/89 support dưới giá (EntryTF).",
                    Symbol = coinInfo.Symbol
                };
            }

            decimal anchor = supports.Max();
            decimal confirmBeyond = coinInfo.IsMajor ? ConfirmCloseBeyondAnchorMajor : ConfirmCloseBeyondAnchorAlt;

            bool touchA =
                anchor > 0m &&
                A.Low <= anchor * (1 + EmaRetestBand) &&
                A.Low >= anchor * (1 - EmaRetestBand);

            if (EnableAntiDumpLong && touchA && IsBearishImpulseAtRetest(candlesEntry, iA, anchor))
            {
                return new TradeSignal
                {
                    Type = SignalType.None,
                    Reason = $"{coinInfo.Symbol}: NO LONG – AntiDump at retest(A) near EMA({anchor:F6}) on EntryTF.",
                    Symbol = coinInfo.Symbol
                };
            }

            bool bullishB = B.Close > B.Open;
            bool closeBackAbove = B.Close >= anchor * (1m + confirmBeyond);
            bool breakSmall = B.High > A.High;

            bool rejectB =
                bullishB &&
                B.Low < anchor &&
                B.Close > anchor;

            bool macdCrossUp = macdE[iB] > sigE[iB] && macdE[iB - 1] <= sigE[iB - 1];
            bool rsiBull = rsiE[iB] > RsiBullThreshold && rsiE[iB] >= rsiE[iB - 1];

            bool bodyStrongB = IsBodyStrong(B, ConfirmBodyToRangeMin);
            bool momentumHard = macdCrossUp && rsiBull && bodyStrongB && closeBackAbove;

            bool ok = touchA && bullishB && closeBackAbove && breakSmall && rejectB;

            if (!ok && AllowMomentumInsteadOfReject)
                ok = touchA && bullishB && closeBackAbove && breakSmall && momentumHard && volumeOkSoft;

            if (!ok)
            {
                return new TradeSignal
                {
                    Type = SignalType.None,
                    Reason = $"{coinInfo.Symbol}: Long(EntryTF) chưa đạt (touchA={touchA}, bullishB={bullishB}, closeBackAbove={closeBackAbove}, break={breakSmall}, rejectB={rejectB}, momHard={momentumHard}, volOk={volumeOkSoft}).",
                    Symbol = coinInfo.Symbol
                };
            }

            decimal slByEma = anchor * (1m - AnchorSlBufferPercent);

            decimal sl = slByEma;
            if (UseSwingForTrendStop)
            {
                decimal swingLow = PriceActionHelper.FindSwingLow(candlesEntry, iB, SwingLookback);
                if (swingLow > 0)
                {
                    decimal slBySwing = swingLow * (1m - SwingStopExtraBufferPercent);
                    sl = Math.Min(slByEma, slBySwing);
                }
            }

            bool allowMode1 = (coinInfo.IsMajor && EnableMarketOnStrongRejectForMajor) ||
                              (!coinInfo.IsMajor && EnableMarketOnStrongRejectForAlt);

            bool strongReject = allowMode1 && rejectB && IsStrongRejectionLong_OnEntryTf(candlesEntry, iB);

            decimal proposedEntry = strongReject
                ? B.Close * (1m + MarketableLimitOffset)
                : anchor * (1m + EntryOffsetPercent);

            if (EnableHumanLikeFilter)
            {
                int iT = candlesTrend.Count - 2;
                if (iT > 5)
                {
                    decimal minDistHigh = coinInfo.IsMajor ? MinDistFromRecentHighMajor : MinDistFromRecentHighAlt;
                    if (IsTooCloseToRecentHigh(candlesTrend, iT, proposedEntry, RecentExtremumLookback, minDistHigh))
                    {
                        return new TradeSignal
                        {
                            Type = SignalType.None,
                            Reason = $"{coinInfo.Symbol}: HUMAN/V4 – Block LONG vì entry quá gần recent HIGH (TrendTF).",
                            Symbol = coinInfo.Symbol
                        };
                    }

                    if (Human_BlockLongNearExtremum(candlesTrend, rsiT, iT, proposedEntry, coinInfo.IsMajor))
                    {
                        return new TradeSignal
                        {
                            Type = SignalType.None,
                            Reason = $"{coinInfo.Symbol}: HUMAN FILTER – Block LONG (TrendTF near HIGH / multi-touch / bearish divergence).",
                            Symbol = coinInfo.Symbol
                        };
                    }

                    if (Human_ShouldWaitLongInDangerZone(candlesTrend, iT, proposedEntry))
                    {
                        return new TradeSignal
                        {
                            Type = SignalType.None,
                            Reason = $"{coinInfo.Symbol}: HUMAN FILTER – WAIT (LONG near top of recent range on TrendTF).",
                            Symbol = coinInfo.Symbol
                        };
                    }
                }
            }

            decimal entry = proposedEntry;

            decimal maxDist = coinInfo.IsMajor ? MaxEntryDistanceToAnchorMajor : MaxEntryDistanceToAnchorAlt;
            if (anchor > 0)
            {
                decimal dist = Math.Abs(entry - anchor) / anchor;
                if (dist > maxDist)
                {
                    return new TradeSignal
                    {
                        Type = SignalType.None,
                        Reason = $"{coinInfo.Symbol}: NO LONG – Entry quá xa anchor (dist={dist:P2} > {maxDist:P2}).",
                        Symbol = coinInfo.Symbol
                    };
                }
            }

            if (sl >= entry || sl <= 0)
            {
                return new TradeSignal
                {
                    Type = SignalType.None,
                    Reason = $"{coinInfo.Symbol}: Long SL invalid (entry={entry:F6}, sl={sl:F6}).",
                    Symbol = coinInfo.Symbol
                };
            }

            decimal rr = riskRewardTrend;
            if (AllowMomentumInsteadOfReject && momentumHard && !rejectB)
                rr *= 0.92m;

            rr = coinInfo.IsMajor
                ? Clamp(rr, TrendRR_MinMajor, TrendRR_MaxMajor)
                : Clamp(rr, TrendRR_MinAlt, TrendRR_MaxAlt);

            decimal risk = entry - sl;
            decimal tp = entry + risk * rr;

            var mode = strongReject ? TradeMode.Mode1_StrongReject : TradeMode.Trend;
            string modeTag = strongReject ? "MODE1" : "TREND_LIMIT";

            return new TradeSignal
            {
                Type = SignalType.Long,
                EntryPrice = entry,
                StopLoss = sl,
                TakeProfit = tp,
                Reason = $"{coinInfo.Symbol}: LONG (TrendTF + EntryTF) – 2-step retest(A)+confirm(B) @EMA({anchor:F6}) + {(strongReject ? "StrongReject" : (momentumHard ? "MomHard" : "Reject"))}. Entry={modeTag}, RR={rr:F2}.",
                Symbol = coinInfo.Symbol,
                Mode = mode
            };
        }

        private TradeSignal BuildShortV4Winrate_EntryWithTrend(
            IReadOnlyList<Candle> candlesEntry,
            IReadOnlyList<Candle> candlesTrend,
            IReadOnlyList<decimal> ema34_E,
            IReadOnlyList<decimal> ema89_E,
            IReadOnlyList<decimal> rsiE,
            IReadOnlyList<decimal> macdE,
            IReadOnlyList<decimal> sigE,
            IReadOnlyList<decimal> rsiT,
            CoinInfo coinInfo,
            decimal riskRewardTrend,
            bool volumeOkSoft)
        {
            int iB = candlesEntry.Count - 2;
            int iA = iB - 1;
            if (iA <= 2) return new TradeSignal();

            var A = candlesEntry[iA];
            var B = candlesEntry[iB];

            if (rsiE[iB] < TrendRetestRsiMinForShort)
            {
                return new TradeSignal
                {
                    Type = SignalType.None,
                    Reason = $"{coinInfo.Symbol}: NO SHORT – RSI(EntryTF) quá thấp ({rsiE[iB]:F1}) cho trend retest.",
                    Symbol = coinInfo.Symbol
                };
            }

            decimal ema34 = ema34_E[iB];
            decimal ema89 = ema89_E[iB];

            var resistances = new List<decimal>();
            if (ema34 > 0 && ema34 > B.Close) resistances.Add(ema34);
            if (ema89 > 0 && ema89 > B.Close) resistances.Add(ema89);

            if (resistances.Count == 0)
            {
                return new TradeSignal
                {
                    Type = SignalType.None,
                    Reason = $"{coinInfo.Symbol}: Downtrend nhưng không có EMA34/89 resistance trên giá (EntryTF).",
                    Symbol = coinInfo.Symbol
                };
            }

            decimal anchor = resistances.Min();
            decimal confirmBeyond = coinInfo.IsMajor ? ConfirmCloseBeyondAnchorMajor : ConfirmCloseBeyondAnchorAlt;

            bool touchA =
                anchor > 0m &&
                A.High >= anchor * (1 - EmaRetestBand) &&
                A.High <= anchor * (1 + EmaRetestBand);

            if (EnableAntiSqueezeShort && touchA && IsBullishImpulseAtRetest(candlesEntry, iA, anchor))
            {
                return new TradeSignal
                {
                    Type = SignalType.None,
                    Reason = $"{coinInfo.Symbol}: NO SHORT – AntiSqueeze at retest(A) near EMA({anchor:F6}) on EntryTF.",
                    Symbol = coinInfo.Symbol
                };
            }

            bool bearishB = B.Close < B.Open;
            bool closeBackBelow = B.Close <= anchor * (1m - confirmBeyond);
            bool breakSmall = B.Low < A.Low;

            bool rejectB =
                bearishB &&
                B.High > anchor &&
                B.Close < anchor;

            bool macdCrossDown = macdE[iB] < sigE[iB] && macdE[iB - 1] >= sigE[iB - 1];
            bool rsiBear = rsiE[iB] < RsiBearThreshold && rsiE[iB] <= rsiE[iB - 1];

            bool bodyStrongB = IsBodyStrong(B, ConfirmBodyToRangeMin);
            bool momentumHard = macdCrossDown && rsiBear && bodyStrongB && closeBackBelow;

            bool ok = touchA && bearishB && closeBackBelow && breakSmall && rejectB;

            if (!ok && AllowMomentumInsteadOfReject)
                ok = touchA && bearishB && closeBackBelow && breakSmall && momentumHard && volumeOkSoft;

            if (!ok)
            {
                return new TradeSignal
                {
                    Type = SignalType.None,
                    Reason = $"{coinInfo.Symbol}: Short(EntryTF) chưa đạt (touchA={touchA}, bearishB={bearishB}, closeBackBelow={closeBackBelow}, break={breakSmall}, rejectB={rejectB}, momHard={momentumHard}, volOk={volumeOkSoft}).",
                    Symbol = coinInfo.Symbol
                };
            }

            decimal slByEma = anchor * (1m + AnchorSlBufferPercent);

            decimal sl = slByEma;
            if (UseSwingForTrendStop)
            {
                decimal swingHigh = PriceActionHelper.FindSwingHigh(candlesEntry, iB, SwingLookback);
                if (swingHigh > 0)
                {
                    decimal slBySwing = swingHigh * (1m + SwingStopExtraBufferPercent);
                    sl = Math.Max(slByEma, slBySwing);
                }
            }

            bool allowMode1 = (coinInfo.IsMajor && EnableMarketOnStrongRejectForMajor) ||
                              (!coinInfo.IsMajor && EnableMarketOnStrongRejectForAlt);

            bool strongReject = allowMode1 && rejectB && IsStrongRejectionShort_OnEntryTf(candlesEntry, iB);

            decimal proposedEntry = strongReject
                ? B.Close * (1m - MarketableLimitOffset)
                : anchor * (1m - EntryOffsetPercent);

            if (EnableHumanLikeFilter)
            {
                int iT = candlesTrend.Count - 2;
                if (iT > 5)
                {
                    decimal minDistLow = coinInfo.IsMajor ? MinDistFromRecentLowMajor : MinDistFromRecentLowAlt;
                    if (IsTooCloseToRecentLow(candlesTrend, iT, proposedEntry, RecentExtremumLookback, minDistLow))
                    {
                        return new TradeSignal
                        {
                            Type = SignalType.None,
                            Reason = $"{coinInfo.Symbol}: HUMAN/V4 – Block SHORT vì entry quá gần recent LOW (TrendTF).",
                            Symbol = coinInfo.Symbol
                        };
                    }

                    if (Human_BlockShortNearExtremum(candlesTrend, rsiT, iT, proposedEntry, coinInfo.IsMajor))
                    {
                        return new TradeSignal
                        {
                            Type = SignalType.None,
                            Reason = $"{coinInfo.Symbol}: HUMAN FILTER – Block SHORT (TrendTF near LOW / multi-touch / bullish divergence).",
                            Symbol = coinInfo.Symbol
                        };
                    }

                    if (Human_ShouldWaitShortInDangerZone(candlesTrend, iT, proposedEntry))
                    {
                        return new TradeSignal
                        {
                            Type = SignalType.None,
                            Reason = $"{coinInfo.Symbol}: HUMAN FILTER – WAIT (SHORT near bottom of recent range on TrendTF).",
                            Symbol = coinInfo.Symbol
                        };
                    }
                }
            }

            decimal entry = proposedEntry;

            decimal maxDist = coinInfo.IsMajor ? MaxEntryDistanceToAnchorMajor : MaxEntryDistanceToAnchorAlt;
            if (anchor > 0)
            {
                decimal dist = Math.Abs(entry - anchor) / anchor;
                if (dist > maxDist)
                {
                    return new TradeSignal
                    {
                        Type = SignalType.None,
                        Reason = $"{coinInfo.Symbol}: NO SHORT – Entry quá xa anchor (dist={dist:P2} > {maxDist:P2}).",
                        Symbol = coinInfo.Symbol
                    };
                }
            }

            if (sl <= entry)
            {
                return new TradeSignal
                {
                    Type = SignalType.None,
                    Reason = $"{coinInfo.Symbol}: Short SL invalid (entry={entry:F6}, sl={sl:F6}).",
                    Symbol = coinInfo.Symbol
                };
            }

            decimal rr = riskRewardTrend;
            if (AllowMomentumInsteadOfReject && momentumHard && !rejectB)
                rr *= 0.92m;

            rr = coinInfo.IsMajor
                ? Clamp(rr, TrendRR_MinMajor, TrendRR_MaxMajor)
                : Clamp(rr, TrendRR_MinAlt, TrendRR_MaxAlt);

            decimal risk = sl - entry;
            decimal tp = entry - risk * rr;

            var mode = strongReject ? TradeMode.Mode1_StrongReject : TradeMode.Trend;
            string modeTag = strongReject ? "MODE1" : "TREND_LIMIT";

            return new TradeSignal
            {
                Type = SignalType.Short,
                EntryPrice = entry,
                StopLoss = sl,
                TakeProfit = tp,
                Reason = $"{coinInfo.Symbol}: SHORT (TrendTF + EntryTF) – 2-step retest(A)+confirm(B) @EMA({anchor:F6}) + {(strongReject ? "StrongReject" : (momentumHard ? "MomHard" : "Reject"))}. Entry={modeTag}, RR={rr:F2}.",
                Symbol = coinInfo.Symbol,
                Mode = mode
            };
        }

        // ========================= SIDEWAY SCALP (TREND TF) =========================

        private TradeSignal BuildSidewayScalp(
            IReadOnlyList<Candle> candlesTrend,
            IReadOnlyList<decimal> ema34_T,
            IReadOnlyList<decimal> ema89_T,
            IReadOnlyList<decimal> ema200_T,
            IReadOnlyList<decimal> rsiT,
            IReadOnlyList<decimal> macdT,
            IReadOnlyList<decimal> sigT,
            Candle lastT,
            CoinInfo coinInfo,
            bool trendBiasUp,
            bool trendBiasDown,
            decimal riskRewardSideway)
        {
            int iT = candlesTrend.Count - 2;
            if (iT <= 0) return new TradeSignal();

            decimal ema34 = ema34_T[iT];
            decimal ema89 = ema89_T[iT];
            decimal ema200 = ema200_T[iT];

            bool shortBiasT = ema34 <= ema89 && ema34 <= ema200;
            bool longBiasT = ema34 >= ema89 && ema34 >= ema200;

            bool shortBias;
            bool longBias;

            if (trendBiasDown)
            {
                shortBias = true;
                longBias = false;
            }
            else if (trendBiasUp)
            {
                longBias = true;
                shortBias = false;
            }
            else
            {
                shortBias = shortBiasT;
                longBias = longBiasT;
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
                if (ema89 > lastT.Close) resistances.Add(ema89);
                if (ema200 > lastT.Close) resistances.Add(ema200);

                if (resistances.Count == 0)
                    return new TradeSignal();

                decimal nearestRes = resistances.Min();

                bool touchRes =
                    lastT.High >= nearestRes * (1 - EmaRetestBand) &&
                    lastT.High <= nearestRes * (1 + EmaRetestBand);

                bool bearBody = lastT.Close <= lastT.Open;
                bool reject = lastT.High > nearestRes && lastT.Close < nearestRes && bearBody;

                bool rsiHigh = rsiT[iT] >= 55m;
                bool rsiTurnDown = rsiT[iT] <= rsiT[iT - 1];
                bool macdDownOrFlat = macdT[iT] <= macdT[iT - 1];

                bool momentum = rsiHigh && (rsiTurnDown || macdDownOrFlat);

                if (!(touchRes && reject && momentum))
                    return new TradeSignal();

                decimal rawEntry = lastT.Close * (1 + EntryOffsetPercentForScal);

                decimal swingHigh = PriceActionHelper.FindSwingHigh(candlesTrend, iT, SwingLookback);

                decimal entry = rawEntry;
                if (swingHigh > 0 && entry >= swingHigh)
                    entry = (lastT.Close + swingHigh) / 2;

                decimal sl = (swingHigh > 0 ? swingHigh : lastT.High) + entry * StopBufferPercent;
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
                    Symbol = coinInfo.Symbol,
                    Mode = TradeMode.Scalp
                };
            }

            // long sideway
            {
                var supports = new List<decimal>();
                if (ema89 < lastT.Close) supports.Add(ema89);
                if (ema200 < lastT.Close) supports.Add(ema200);

                if (supports.Count == 0)
                    return new TradeSignal();

                decimal nearestSup = supports.Max();

                bool touchSup =
                    lastT.Low <= nearestSup * (1 + EmaRetestBand) &&
                    lastT.Low >= nearestSup * (1 - EmaRetestBand);

                bool bullBody = lastT.Close >= lastT.Open;
                bool reject = lastT.Low < nearestSup && lastT.Close > nearestSup && bullBody;

                bool rsiOk = rsiT[iT] >= 45m;
                bool rsiTurnUp = rsiT[iT] >= rsiT[iT - 1];
                bool macdUpOrFlat = macdT[iT] >= macdT[iT - 1];

                bool momentum = rsiOk && (rsiTurnUp || macdUpOrFlat);

                if (!(touchSup && reject && momentum))
                    return new TradeSignal();

                decimal rawEntry = lastT.Close * (1 - EntryOffsetPercentForScal);

                decimal swingLow = PriceActionHelper.FindSwingLow(candlesTrend, iT, SwingLookback);

                decimal entry = rawEntry;
                if (swingLow > 0 && entry <= swingLow)
                    entry = (lastT.Close + swingLow) / 2;

                decimal sl = (swingLow > 0 ? swingLow : lastT.Low) - entry * StopBufferPercent;

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
                    Symbol = coinInfo.Symbol,
                    Mode = TradeMode.Scalp
                };
            }
        }

        // =====================================================================
        //                    MODE 1 HELPERS: STRONG REJECTION (ENTRY TF)
        // =====================================================================

        private bool IsStrongRejectionLong_OnEntryTf(IReadOnlyList<Candle> candles, int idxClosed)
        {
            if (idxClosed <= 0 || idxClosed >= candles.Count) return false;

            var c = candles[idxClosed];
            decimal range = c.High - c.Low;
            if (range <= 0) return false;

            decimal body = Math.Abs(c.Close - c.Open);
            decimal bodySafe = Math.Max(body, range * 0.10m);
            decimal lowerWick = Math.Min(c.Open, c.Close) - c.Low;

            bool wickOk = lowerWick / bodySafe >= StrongRejectWickToBody;

            decimal closePosFromHigh = (c.High - c.Close) / range;
            bool closeOk = closePosFromHigh <= StrongRejectCloseInRange;

            return wickOk && closeOk;
        }

        private bool IsStrongRejectionShort_OnEntryTf(IReadOnlyList<Candle> candles, int idxClosed)
        {
            if (idxClosed <= 0 || idxClosed >= candles.Count) return false;

            var c = candles[idxClosed];
            decimal range = c.High - c.Low;
            if (range <= 0) return false;

            decimal body = Math.Abs(c.Close - c.Open);
            decimal bodySafe = Math.Max(body, range * 0.10m);
            decimal upperWick = c.High - Math.Max(c.Open, c.Close);

            bool wickOk = upperWick / bodySafe >= StrongRejectWickToBody;

            decimal closePosFromLow = (c.Close - c.Low) / range;
            bool closeOk = closePosFromLow <= StrongRejectCloseInRange;

            return wickOk && closeOk;
        }

        // =====================================================================
        //                     PATCH HELPERS: ANTI-IMPULSE AT RETEST (ENTRY TF)
        // =====================================================================

        private bool IsBullishImpulseAtRetest(IReadOnlyList<Candle> candles, int idxClosed, decimal anchorResistance)
        {
            if (idxClosed <= 0 || idxClosed >= candles.Count) return false;
            if (anchorResistance <= 0) return false;

            var c = candles[idxClosed];

            decimal dist = Math.Abs(c.Close - anchorResistance) / anchorResistance;
            if (dist > AntiImpulseMaxDistToAnchor) return false;

            decimal range = c.High - c.Low;
            if (range <= 0) return false;

            if (c.Close <= c.Open) return false;

            decimal body = Math.Abs(c.Close - c.Open);
            decimal bodyToRange = body / range;
            if (bodyToRange < AntiImpulseBodyToRangeMin) return false;

            decimal closePosFromHigh = (c.High - c.Close) / range;
            if (closePosFromHigh > AntiImpulseCloseNearEdgeMax) return false;

            return true;
        }

        private bool IsBearishImpulseAtRetest(IReadOnlyList<Candle> candles, int idxClosed, decimal anchorSupport)
        {
            if (idxClosed <= 0 || idxClosed >= candles.Count) return false;
            if (anchorSupport <= 0) return false;

            var c = candles[idxClosed];

            decimal dist = Math.Abs(c.Close - anchorSupport) / anchorSupport;
            if (dist > AntiImpulseMaxDistToAnchor) return false;

            decimal range = c.High - c.Low;
            if (range <= 0) return false;

            if (c.Close >= c.Open) return false;

            decimal body = Math.Abs(c.Close - c.Open);
            decimal bodyToRange = body / range;
            if (bodyToRange < AntiImpulseBodyToRangeMin) return false;

            decimal closePosFromLow = (c.Close - c.Low) / range;
            if (closePosFromLow > AntiImpulseCloseNearEdgeMax) return false;

            return true;
        }

        // =====================================================================
        //                     HELPERS: CLIMAX / EMA / SIDEWAY
        // =====================================================================

        private bool IsClimaxCandle(IReadOnlyList<Candle> candles, int index)
        {
            if (index <= 0 || index >= candles.Count) return false;

            int start = Math.Max(0, index - ClimaxLookback);
            int end = index;
            int count = end - start;
            if (count <= 3) return false;

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
            decimal nearest = 0m;
            decimal minDist = decimal.MaxValue;

            foreach (var ema in new[] { ema34, ema89, ema200 })
            {
                if (ema <= 0) continue;
                decimal d = Math.Abs(price - ema);
                if (d < minDist)
                {
                    minDist = d;
                    nearest = ema;
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
            if (index <= 0 || index >= candles.Count) return false;
            if (!IsClimaxCandle(candles, index)) return false;

            var c = candles[index];
            decimal nearestEma = GetNearestEma(c.Close, ema34, ema89, ema200);
            if (nearestEma <= 0) return false;

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

        private decimal GetMedianVolUsd(IReadOnlyList<Candle> candlesTrend, int endIdx, int lookback)
        {
            int start = Math.Max(0, endIdx - lookback + 1);
            var list = new List<decimal>();

            for (int i = start; i <= endIdx; i++)
            {
                var c = candlesTrend[i];
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
            DetectMarketStructure(IReadOnlyList<Candle> candlesTrend, int iT, decimal tol)
        {
            var highs = GetRecentSwingHighs(candlesTrend, iT, StructureMaxLookbackBars, StructureSwingStrength);
            var lows = GetRecentSwingLows(candlesTrend, iT, StructureMaxLookbackBars, StructureSwingStrength);

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

        // =====================================================================
        //                      ANTI-LATE ENTRY HELPERS (TREND TF)
        // =====================================================================

        private bool IsTooCloseToRecentLow(IReadOnlyList<Candle> candles, int idxClosed, decimal entry, int lookback, decimal minDistRatio)
        {
            if (entry <= 0 || candles == null || candles.Count == 0) return false;
            int end = Math.Min(idxClosed, candles.Count - 1);
            int start = Math.Max(0, end - lookback + 1);

            decimal low = decimal.MaxValue;
            for (int i = start; i <= end; i++)
                low = Math.Min(low, candles[i].Low);

            if (low <= 0 || low == decimal.MaxValue) return false;

            decimal dist = (entry - low) / low;
            return dist >= 0m && dist < minDistRatio;
        }

        private bool IsTooCloseToRecentHigh(IReadOnlyList<Candle> candles, int idxClosed, decimal entry, int lookback, decimal minDistRatio)
        {
            if (entry <= 0 || candles == null || candles.Count == 0) return false;
            int end = Math.Min(idxClosed, candles.Count - 1);
            int start = Math.Max(0, end - lookback + 1);

            decimal high = 0m;
            for (int i = start; i <= end; i++)
                high = Math.Max(high, candles[i].High);

            if (high <= 0) return false;

            decimal dist = (high - entry) / high;
            return dist >= 0m && dist < minDistRatio;
        }

        // =====================================================================
        //                      DYNAMIC RR HELPERS
        // =====================================================================

        private decimal GetDynamicTrendRR(bool isMajor, decimal sepTrend, bool slopeOk, decimal ratioVsMedian, bool volumeOkSoft)
        {
            decimal minSep = isMajor ? MinEmaSeparationMajor : MinEmaSeparationAlt;

            decimal sepScore = Normalize01(sepTrend, minSep, minSep * 2.2m);
            decimal slopeScore = slopeOk ? 1m : 0m;
            decimal volScore = Normalize01(ratioVsMedian, 0.70m, 1.20m);
            decimal softScore = volumeOkSoft ? 1m : 0.7m;

            decimal score =
                (sepScore * 0.55m) +
                (slopeScore * 0.25m) +
                (volScore * 0.20m);

            score *= softScore;
            score = Clamp(score, 0m, 1m);

            decimal rrMin = isMajor ? TrendRR_MinMajor : TrendRR_MinAlt;
            decimal rrMax = isMajor ? TrendRR_MaxMajor : TrendRR_MaxAlt;

            decimal rr = rrMin + (rrMax - rrMin) * score;

            if (ratioVsMedian < 0.85m)
                rr *= 0.95m;

            return rr;
        }

        private decimal Normalize01(decimal x, decimal min, decimal max)
        {
            if (max <= min) return 0m;
            if (x <= min) return 0m;
            if (x >= max) return 1m;
            return (x - min) / (max - min);
        }

        private decimal Clamp(decimal x, decimal lo, decimal hi)
        {
            if (x < lo) return lo;
            if (x > hi) return hi;
            return x;
        }

        // =====================================================================
        //                      HUMAN-LIKE FILTER HELPERS (TREND TF)
        // =====================================================================

        private bool Human_ShouldWaitShortInDangerZone(IReadOnlyList<Candle> candles, int idxClosed, decimal entry)
        {
            if (entry <= 0) return false;
            var (low, high) = Human_GetRecentRange(candles, idxClosed, HumanRecentRangeLookback);
            decimal range = high - low;
            if (low <= 0 || range <= 0) return false;

            decimal pos = (entry - low) / range;
            return pos >= 0m && pos < DangerZoneBottomRatio;
        }

        private bool Human_ShouldWaitLongInDangerZone(IReadOnlyList<Candle> candles, int idxClosed, decimal entry)
        {
            if (entry <= 0) return false;
            var (low, high) = Human_GetRecentRange(candles, idxClosed, HumanRecentRangeLookback);
            decimal range = high - low;
            if (high <= 0 || range <= 0) return false;

            decimal pos = (high - entry) / range;
            return pos >= 0m && pos < DangerZoneTopRatio;
        }

        private bool Human_BlockShortNearExtremum(
            IReadOnlyList<Candle> candles,
            IReadOnlyList<decimal> rsi,
            int idxClosed,
            decimal entry,
            bool isMajor)
        {
            if (entry <= 0) return false;

            var (low, high) = Human_GetRecentRange(candles, idxClosed, ExtremumTouchLookback);
            if (low <= 0 || high <= 0) return false;

            decimal eps = isMajor ? ExtremumTouchEpsMajor : ExtremumTouchEpsAlt;
            int touches = Human_CountTouchesLow(candles, idxClosed, ExtremumTouchLookback, low, eps);

            bool bullDiv = Human_HasBullishDivergenceNearLow(candles, rsi, idxClosed, DivergenceLookback, low, eps);

            decimal distToLow = (entry - low) / low;
            bool nearLow = distToLow >= 0m && distToLow < (isMajor ? MinDistFromRecentLowMajor : MinDistFromRecentLowAlt);

            return nearLow && (touches >= MinTouchesToBlock || bullDiv);
        }

        private bool Human_BlockLongNearExtremum(
            IReadOnlyList<Candle> candles,
            IReadOnlyList<decimal> rsi,
            int idxClosed,
            decimal entry,
            bool isMajor)
        {
            if (entry <= 0) return false;

            var (low, high) = Human_GetRecentRange(candles, idxClosed, ExtremumTouchLookback);
            if (low <= 0 || high <= 0) return false;

            decimal eps = isMajor ? ExtremumTouchEpsMajor : ExtremumTouchEpsAlt;
            int touches = Human_CountTouchesHigh(candles, idxClosed, ExtremumTouchLookback, high, eps);

            bool bearDiv = Human_HasBearishDivergenceNearHigh(candles, rsi, idxClosed, DivergenceLookback, high, eps);

            decimal distToHigh = (high - entry) / high;
            bool nearHigh = distToHigh >= 0m && distToHigh < (isMajor ? MinDistFromRecentHighMajor : MinDistFromRecentHighAlt);

            return nearHigh && (touches >= MinTouchesToBlock || bearDiv);
        }

        private (decimal low, decimal high) Human_GetRecentRange(IReadOnlyList<Candle> candles, int idxClosed, int lookback)
        {
            if (candles == null || candles.Count == 0) return (0m, 0m);
            int end = Math.Min(idxClosed, candles.Count - 1);
            int start = Math.Max(0, end - lookback + 1);

            decimal low = decimal.MaxValue;
            decimal high = 0m;

            for (int i = start; i <= end; i++)
            {
                low = Math.Min(low, candles[i].Low);
                high = Math.Max(high, candles[i].High);
            }

            if (low == decimal.MaxValue) low = 0m;
            return (low, high);
        }

        private int Human_CountTouchesLow(IReadOnlyList<Candle> candles, int idxClosed, int lookback, decimal low, decimal epsRatio)
        {
            int end = Math.Min(idxClosed, candles.Count - 1);
            int start = Math.Max(0, end - lookback + 1);

            int touches = 0;
            decimal eps = low * epsRatio;

            for (int i = start; i <= end; i++)
            {
                if (Math.Abs(candles[i].Low - low) <= eps)
                    touches++;
            }
            return touches;
        }

        private int Human_CountTouchesHigh(IReadOnlyList<Candle> candles, int idxClosed, int lookback, decimal high, decimal epsRatio)
        {
            int end = Math.Min(idxClosed, candles.Count - 1);
            int start = Math.Max(0, end - lookback + 1);

            int touches = 0;
            decimal eps = high * epsRatio;

            for (int i = start; i <= end; i++)
            {
                if (Math.Abs(candles[i].High - high) <= eps)
                    touches++;
            }
            return touches;
        }

        private bool Human_HasBullishDivergenceNearLow(
            IReadOnlyList<Candle> candles,
            IReadOnlyList<decimal> rsi,
            int idxClosed,
            int lookback,
            decimal recentLow,
            decimal epsRatio)
        {
            if (rsi == null || rsi.Count <= idxClosed) return false;

            int end = Math.Min(idxClosed, candles.Count - 1);
            int start = Math.Max(0, end - lookback + 1);
            if (end - start < 5) return false;

            int low1 = -1, low2 = -1;
            decimal v1 = decimal.MaxValue, v2 = decimal.MaxValue;

            for (int i = start; i <= end; i++)
            {
                decimal v = candles[i].Low;
                if (v < v1)
                {
                    v2 = v1; low2 = low1;
                    v1 = v; low1 = i;
                }
                else if (v < v2)
                {
                    v2 = v; low2 = i;
                }
            }

            if (low1 < 0 || low2 < 0) return false;

            decimal eps = recentLow * epsRatio;
            bool near = Math.Abs(v1 - recentLow) <= eps || Math.Abs(v2 - recentLow) <= eps;
            if (!near) return false;

            decimal price1 = candles[low1].Low;
            decimal price2 = candles[low2].Low;

            decimal rsi1 = rsi[low1];
            decimal rsi2 = rsi[low2];

            if (low2 > low1)
            {
                (low1, low2) = (low2, low1);
                (price1, price2) = (price2, price1);
                (rsi1, rsi2) = (rsi2, rsi1);
            }

            bool lowerLow = price2 < price1;
            bool higherRsi = rsi2 > rsi1 + MinRsiDivergenceGap;

            return lowerLow && higherRsi;
        }

        private bool Human_HasBearishDivergenceNearHigh(
            IReadOnlyList<Candle> candles,
            IReadOnlyList<decimal> rsi,
            int idxClosed,
            int lookback,
            decimal recentHigh,
            decimal epsRatio)
        {
            if (rsi == null || rsi.Count <= idxClosed) return false;

            int end = Math.Min(idxClosed, candles.Count - 1);
            int start = Math.Max(0, end - lookback + 1);
            if (end - start < 5) return false;

            int hi1 = -1, hi2 = -1;
            decimal v1 = 0m, v2 = 0m;

            for (int i = start; i <= end; i++)
            {
                decimal v = candles[i].High;
                if (v > v1)
                {
                    v2 = v1; hi2 = hi1;
                    v1 = v; hi1 = i;
                }
                else if (v > v2)
                {
                    v2 = v; hi2 = i;
                }
            }

            if (hi1 < 0 || hi2 < 0) return false;

            decimal eps = recentHigh * epsRatio;
            bool near = Math.Abs(v1 - recentHigh) <= eps || Math.Abs(v2 - recentHigh) <= eps;
            if (!near) return false;

            decimal price1 = candles[hi1].High;
            decimal price2 = candles[hi2].High;

            decimal rsi1 = rsi[hi1];
            decimal rsi2 = rsi[hi2];

            if (hi2 > hi1)
            {
                (hi1, hi2) = (hi2, hi1);
                (price1, price2) = (price2, price1);
                (rsi1, rsi2) = (rsi2, rsi1);
            }

            bool higherHigh = price2 > price1;
            bool lowerRsi = rsi2 < rsi1 - MinRsiDivergenceGap;

            return higherHigh && lowerRsi;
        }
    }
}
