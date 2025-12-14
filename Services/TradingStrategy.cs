using FuturesBot.Config;
using FuturesBot.IServices;
using FuturesBot.Models;
using FuturesBot.Utils;
using static FuturesBot.Utils.EnumTypesHelper;

namespace FuturesBot.Services
{
    /// <summary>
    /// Trading Strategy chuyên nghiệp:
    /// - Xác nhận Uptrend/Downtrend bằng EMA34/EMA89 trên H1 + M15
    /// - Khi đã có trend:
    ///     + LONG: chờ giá retest EMA gần nhất phía dưới (34/89/200) + rejection + momentum
    ///     + SHORT: chờ giá retest EMA gần nhất phía trên (34/89/200) + rejection + momentum
    /// - Momentum MACD + RSI (mềm hơn để không bỏ lỡ quá nhiều kèo đẹp)
    /// - Entry offset để tránh vào đúng đỉnh/đáy (giảm rủi ro)
    /// - SL theo EMA gần nhất (dynamic) + buffer (có thể kết hợp swing)
    /// - Khi đang có vị thế:
    ///       LONG  => nếu đóng dưới EMA dynamic M15 phía dưới giá (kèm tolerance) → EXIT LONG
    ///       SHORT => nếu đóng trên EMA dynamic M15 phía trên giá (kèm tolerance) → EXIT SHORT
    /// - Sideway scalp:
    ///       + Dùng khi H1 có bias rõ nhưng M15 chưa align / đang sideway quanh EMA
    ///       + Chỉ trade theo hướng bias H1 (H1 down chỉ short, H1 up chỉ long)
    /// - Tối ưu BTC/ETH vs Altcoin (coinInfo.IsMajor):
    ///       + IsMajor = true (BTC/ETH): cho phép trend trade + sideway scalp,
    ///         RR trend riêng (RiskRewardMajor), RR sideway (RiskRewardSidewayMajor).
    ///       + IsMajor = false (Altcoin): chỉ trend trade, thêm filter Volume / EMA slope H1,
    ///         bỏ sideway scalp, RR trend dùng RiskReward.
    /// - V2 bổ sung:
    ///       + Filter SIDEWAY mạnh (EMA34 & EMA89 dính nhau + EMA34 gần như đi ngang).
    ///       + Altcoin gặp sideway H1/M15 → NO TRADE để né nhiễu.
    /// - V3 bổ sung:
    ///       + Market Structure (Lower-High / Higher-Low) để khóa LONG/SHORT khi cấu trúc báo ngược.
    /// </summary>
    public class TradingStrategy(IndicatorService indicators) : IStrategyService
    {
        private readonly IndicatorService _indicators = indicators;

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

        // ========================= NEW: BTC vs ALT CONFIG ====================

        // Volume M15 ước lượng USDT = Close * Volume tối thiểu (ngưỡng mềm)
        private const decimal MinMajorVolumeUsd15m = 2_000_000m; // BTC/ETH
        private const decimal MinAltVolumeUsd15m = 600_000m;     // Altcoin

        // dùng median để tránh giờ thấp điểm làm NO TRADE liên tục
        private const int VolumeMedianLookback = 40;
        private const decimal MinVolumeVsMedianRatioMajor = 0.55m; // >=55% median vẫn cho trade
        private const decimal MinVolumeVsMedianRatioAlt = 0.65m;   // alt cần khỏe hơn

        // Độ dốc EMA34 H1 tối thiểu cho Altcoin (tránh alt sideway)
        private const int EmaSlopeLookbackH1 = 3;
        private const decimal MinAltEmaSlopeH1 = 0.003m; // 0.3%

        // SIDEWAY FILTER (FIX: bớt over-trigger)
        private const int SidewaySlopeLookback = 10;
        private const decimal SidewayEmaDistThreshold = 0.0015m; // 0.15%
        private const decimal SidewaySlopeThreshold = 0.002m;    // 0.2%
        private const int SidewayConfirmBars = 3;                // require sideway true liên tục 3 bar

        // ========================= MARKET STRUCTURE (V3) =========================
        private const int StructureSwingStrength = 3;
        private const int StructureMaxLookbackBars = 80;
        private const int StructureNeedSwings = 2;
        private const decimal StructureBreakToleranceMajor = 0.0020m; // 0.20%
        private const decimal StructureBreakToleranceAlt = 0.0030m;   // 0.30%

        // =====================================================================
        //                           ENTRY SIGNAL
        // =====================================================================

        public TradeSignal GenerateSignal(IReadOnlyList<Candle> candles15m, IReadOnlyList<Candle> candles1h, CoinInfo coinInfo)
        {
            if (candles15m == null || candles1h == null || candles15m.Count < MinBars || candles1h.Count < MinBars)
                return new TradeSignal();

            bool isMajor = coinInfo.IsMajor;

            // Dùng nến đã đóng: M15 & H1
            int i15 = candles15m.Count - 2;
            int iH1 = candles1h.Count - 2;
            if (i15 <= 0 || iH1 <= 0) return new TradeSignal();

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

            // Volume ước lượng trên M15 (nến đã đóng)
            decimal volUsd15 = last15.Close * last15.Volume;

            // ---- Soft volume filter (min + ratio vs median) ----
            decimal medianVolUsd = GetMedianVolUsd(candles15m, i15, VolumeMedianLookback);
            decimal ratioVsMedian = medianVolUsd > 0 ? (volUsd15 / medianVolUsd) : 1m;

            if (isMajor)
            {
                if (volUsd15 < MinMajorVolumeUsd15m && ratioVsMedian < MinVolumeVsMedianRatioMajor)
                {
                    return new TradeSignal
                    {
                        Type = SignalType.None,
                        Reason = $"{coinInfo.Symbol}: Volume M15 yếu ({volUsd15:F0} USDT, vsMedian={ratioVsMedian:P0}) → bỏ qua để tránh slippage.",
                        Coin = coinInfo.Symbol
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
                        Reason = $"{coinInfo.Symbol}: Altcoin volume M15 yếu ({volUsd15:F0} USDT, vsMedian={ratioVsMedian:P0}) → bỏ qua.",
                        Coin = coinInfo.Symbol
                    };
                }

                // ALT: EMA34 H1 phải có độ dốc đủ (tránh alt sideway rác)
                if (iH1 >= EmaSlopeLookbackH1)
                {
                    decimal ema34NowH1 = ema34_h1[iH1];
                    decimal ema34Prev = ema34_h1[iH1 - EmaSlopeLookbackH1];

                    if (ema34Prev > 0)
                    {
                        decimal slope = Math.Abs(ema34NowH1 - ema34Prev) / ema34Prev;
                        if (slope < MinAltEmaSlopeH1)
                        {
                            return new TradeSignal
                            {
                                Type = SignalType.None,
                                Reason = $"{coinInfo.Symbol}: Altcoin đang sideway, EMA34 H1 đi ngang (slope={slope:P2}) → chỉ trade khi trend rõ.",
                                Coin = coinInfo.Symbol
                            };
                        }
                    }
                }
            }

            // =================== SIDEWAY FILTER (FIXED) ===========================

            bool sideway15 = IsSidewayStrong(candles15m, ema34_15, ema89_15);
            bool sidewayH1 = IsSidewayStrong(candles1h, ema34_h1, ema89_h1);

            // Altcoin gặp sideway mạnh H1 hoặc M15 → bỏ qua hết
            if (!isMajor && (sideway15 || sidewayH1))
            {
                return new TradeSignal
                {
                    Type = SignalType.None,
                    Reason = $"{coinInfo.Symbol}: Altcoin SIDEWAY mạnh trên {(sidewayH1 ? "H1" : "M15")} → NO TRADE.",
                    Coin = coinInfo.Symbol
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
                    Reason = $"{coinInfo.Symbol}: Bỏ qua entry vì vừa có nến climax và giá quá xa EMA gần nhất → chờ retest EMA.",
                    Coin = coinInfo.Symbol
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

            bool preferShortByStructure = lowerHigh && !higherLow;
            bool preferLongByStructure = higherLow && !lowerHigh;

            bool blockLongByStructure = preferShortByStructure;
            bool blockShortByStructure = preferLongByStructure;

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
                        Coin = coinInfo.Symbol
                    };
                }
            }

            // =================== (FIX) Pullback volume filter ===================
            // Pullback thường volume thấp. Không chặn cứng nữa.
            // Nếu muốn filter nhẹ: yêu cầu lastVol >= 0.7*avgVol khi không có rejection.
            decimal avgVol = PriceActionHelper.AverageVolume(candles15m, i15 - 1, PullbackVolumeLookback);
            bool volumeOkSoft = avgVol <= 0 || last15.Volume >= avgVol * 0.7m;

            // =================== BUILD LONG / SHORT (THEO TREND MẠNH) =========

            if (upTrend && !blockLongByStructure)
            {
                var longSignal = BuildLong(
                    candles15m,
                    ema34_15,
                    ema89_15,
                    ema200_15,
                    rsi15,
                    macd15,
                    sig15,
                    last15,
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
                    Coin = coinInfo.Symbol
                };
            }

            if (downTrend && !blockShortByStructure)
            {
                var shortSignal = BuildShort(
                    candles15m,
                    ema34_15,
                    ema89_15,
                    ema200_15,
                    rsi15,
                    macd15,
                    sig15,
                    last15,
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
                    Coin = coinInfo.Symbol
                };
            }

            return new TradeSignal();
        }

        // =====================================================================
        //                              LONG (DYNAMIC EMA)
        // =====================================================================

        private TradeSignal BuildLong(
            IReadOnlyList<Candle> candles15m,
            IReadOnlyList<decimal> ema34_15,
            IReadOnlyList<decimal> ema89_15,
            IReadOnlyList<decimal> ema200_15,
            IReadOnlyList<decimal> rsi15,
            IReadOnlyList<decimal> macd15,
            IReadOnlyList<decimal> sig15,
            Candle last15,
            CoinInfo coinInfo,
            decimal riskRewardTrend,
            bool volumeOkSoft)
        {
            int i15 = candles15m.Count - 2;
            if (i15 <= 0) return new TradeSignal();

            decimal ema34 = ema34_15[i15];
            decimal ema89 = ema89_15[i15];
            decimal ema200 = ema200_15[i15];

            // 1) SUPPORT dynamic (EMA dưới giá)
            var supports = new List<decimal>();
            if (ema34 > 0 && ema34 < last15.Close) supports.Add(ema34);
            if (ema89 > 0 && ema89 < last15.Close) supports.Add(ema89);
            if (ema200 > 0 && ema200 < last15.Close) supports.Add(ema200);

            if (supports.Count == 0)
            {
                return new TradeSignal
                {
                    Type = SignalType.None,
                    Reason = $"{coinInfo.Symbol}: Uptrend nhưng không có EMA hỗ trợ dưới giá để retest.",
                    Coin = coinInfo.Symbol
                };
            }

            decimal nearestSupport = supports.Max();

            // 2) RETEST support
            bool touchSupport =
                nearestSupport > 0m &&
                last15.Low <= nearestSupport * (1 + EmaRetestBand) &&
                last15.Low >= nearestSupport * (1 - EmaRetestBand);

            // 3) rejection
            bool reject =
                nearestSupport > 0m &&
                last15.Close > last15.Open &&
                last15.Low < nearestSupport &&
                last15.Close > nearestSupport;

            // 4) momentum mềm
            bool macdCrossUp = macd15[i15] > sig15[i15] && macd15[i15 - 1] <= sig15[i15 - 1];
            bool rsiBull = rsi15[i15] > RsiBullThreshold && rsi15[i15] >= rsi15[i15 - 1];
            bool rsiBullSoft = rsi15[i15] > 50m && rsi15[i15] >= rsi15[i15 - 1];
            bool macdUpOrFlat = macd15[i15] >= macd15[i15 - 1];

            bool momentum =
                (macdCrossUp && rsiBull) ||
                (rsiBull && macd15[i15] > 0) ||
                (rsiBullSoft && macdUpOrFlat);

            // Pullback volume: nếu volume yếu thì bắt buộc phải có rejection rõ
            bool ok = touchSupport && (reject || (momentum && volumeOkSoft));
            if (!ok)
            {
                return new TradeSignal
                {
                    Type = SignalType.None,
                    Reason = $"{coinInfo.Symbol}: Long chưa đạt (touch={touchSupport}, reject={reject}, momentum={momentum}, volOk={volumeOkSoft}).",
                    Coin = coinInfo.Symbol
                };
            }

            // 5) ENTRY & SL
            decimal anchor = nearestSupport;
            decimal entry = anchor * (1m + EntryOffsetPercent);

            // SL base theo EMA
            decimal slByEma = anchor * (1m - AnchorSlBufferPercent);

            // OPTION: SL theo swing low (an toàn hơn)
            decimal sl = slByEma;
            if (UseSwingForTrendStop)
            {
                decimal swingLow = PriceActionHelper.FindSwingLow(candles15m, i15, SwingLookback);
                if (swingLow > 0)
                {
                    decimal slBySwing = swingLow * (1m - SwingStopExtraBufferPercent);
                    // chọn cái "thấp hơn" để an toàn (nhưng vẫn phải hợp lý)
                    sl = Math.Min(slByEma, slBySwing);
                }
            }

            if (sl >= entry || sl <= 0)
            {
                return new TradeSignal
                {
                    Type = SignalType.None,
                    Reason = $"{coinInfo.Symbol}: SL invalid cho long (entry={entry:F4}, sl={sl:F4}).",
                    Coin = coinInfo.Symbol
                };
            }

            decimal risk = entry - sl;
            decimal tp = entry + risk * riskRewardTrend;

            return new TradeSignal
            {
                Type = SignalType.Long,
                EntryPrice = entry,
                StopLoss = sl,
                TakeProfit = tp,
                Reason = $"{coinInfo.Symbol}: LONG – retest EMA support({nearestSupport:F6}) + (rejection/momentum) + SL dynamic (EMA/swing).",
                Coin = coinInfo.Symbol
            };
        }

        // =====================================================================
        //                              SHORT (DYNAMIC EMA)
        // =====================================================================

        private TradeSignal BuildShort(
            IReadOnlyList<Candle> candles15m,
            IReadOnlyList<decimal> ema34_15,
            IReadOnlyList<decimal> ema89_15,
            IReadOnlyList<decimal> ema200_15,
            IReadOnlyList<decimal> rsi15,
            IReadOnlyList<decimal> macd15,
            IReadOnlyList<decimal> sig15,
            Candle last15,
            CoinInfo coinInfo,
            decimal riskRewardTrend,
            bool volumeOkSoft)
        {
            int i15 = candles15m.Count - 2;
            if (i15 <= 0) return new TradeSignal();

            decimal ema34 = ema34_15[i15];
            decimal ema89 = ema89_15[i15];
            decimal ema200 = ema200_15[i15];

            // 1) RESISTANCE dynamic (EMA trên giá)
            var resistances = new List<decimal>();
            if (ema34 > 0 && ema34 > last15.Close) resistances.Add(ema34);
            if (ema89 > 0 && ema89 > last15.Close) resistances.Add(ema89);
            if (ema200 > 0 && ema200 > last15.Close) resistances.Add(ema200);

            if (resistances.Count == 0)
            {
                return new TradeSignal
                {
                    Type = SignalType.None,
                    Reason = $"{coinInfo.Symbol}: Downtrend nhưng không có EMA kháng cự trên giá để retest.",
                    Coin = coinInfo.Symbol
                };
            }

            decimal nearestResistance = resistances.Min();

            // 2) retest
            bool retest =
                nearestResistance > 0m &&
                last15.High >= nearestResistance * (1 - EmaRetestBand) &&
                last15.High <= nearestResistance * (1 + EmaRetestBand);

            // 3) rejection
            bool reject =
                nearestResistance > 0m &&
                last15.Close < last15.Open &&
                last15.High > nearestResistance &&
                last15.Close < nearestResistance;

            // 4) momentum mềm
            bool macdCrossDown = macd15[i15] < sig15[i15] && macd15[i15 - 1] >= sig15[i15 - 1];
            bool rsiBear = rsi15[i15] < RsiBearThreshold && rsi15[i15] <= rsi15[i15 - 1];
            bool rsiBearSoft = rsi15[i15] < 50m && rsi15[i15] <= rsi15[i15 - 1];
            bool macdDownOrFlat = macd15[i15] <= macd15[i15 - 1];

            bool momentum =
                (macdCrossDown && rsiBear) ||
                (rsiBear && macd15[i15] < 0) ||
                (rsiBearSoft && macdDownOrFlat);

            bool ok = retest && (reject || (momentum && volumeOkSoft));
            if (!ok)
            {
                return new TradeSignal
                {
                    Type = SignalType.None,
                    Reason = $"{coinInfo.Symbol}: Short chưa đạt (retest={retest}, reject={reject}, momentum={momentum}, volOk={volumeOkSoft}).",
                    Coin = coinInfo.Symbol
                };
            }

            // 5) ENTRY & SL
            decimal anchor = nearestResistance;
            decimal entry = anchor * (1m - EntryOffsetPercent);

            decimal slByEma = anchor * (1m + AnchorSlBufferPercent);

            decimal sl = slByEma;
            if (UseSwingForTrendStop)
            {
                decimal swingHigh = PriceActionHelper.FindSwingHigh(candles15m, i15, SwingLookback);
                if (swingHigh > 0)
                {
                    decimal slBySwing = swingHigh * (1m + SwingStopExtraBufferPercent);
                    sl = Math.Max(slByEma, slBySwing); // chọn cao hơn để an toàn
                }
            }

            if (sl <= entry)
            {
                return new TradeSignal
                {
                    Type = SignalType.None,
                    Reason = $"{coinInfo.Symbol}: SL invalid cho short (entry={entry:F4}, sl={sl:F4}).",
                    Coin = coinInfo.Symbol
                };
            }

            decimal risk = sl - entry;
            decimal tp = entry - risk * riskRewardTrend;

            return new TradeSignal
            {
                Type = SignalType.Short,
                EntryPrice = entry,
                StopLoss = sl,
                TakeProfit = tp,
                Reason = $"{coinInfo.Symbol}: SHORT – retest EMA resistance({nearestResistance:F6}) + (rejection/momentum) + SL dynamic (EMA/swing).",
                Coin = coinInfo.Symbol
            };
        }

        // =====================================================================
        //                        SIDEWAY SCALP (15M)
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

            // 1) BIAS EMA 15M
            bool shortBias15 = ema34 <= ema89 && ema34 <= ema200;
            bool longBias15 = ema34 >= ema89 && ema34 >= ema200;

            // 2) Ưu tiên bias theo H1
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
                    Reason = $"{coinInfo.Symbol}: SIDEWAY – không có bias rõ (ema34={ema34:F2}, ema89={ema89:F2}, ema200={ema200:F2}).",
                    Coin = coinInfo.Symbol
                };
            }

            // ========================== SCALP SHORT ===========================
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
                    Reason = $"{coinInfo.Symbol}: SIDEWAY SCALP SHORT – bias down + retest EMA (near={nearestRes:F2}) + rejection + RSI/MACD quay đầu.",
                    Coin = coinInfo.Symbol
                };
            }

            // ========================== SCALP LONG ============================
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
                    Reason = $"{coinInfo.Symbol}: SIDEWAY SCALP LONG – bias up + retest EMA (near={nearestSup:F2}) + rejection + RSI/MACD quay đầu.",
                    Coin = coinInfo.Symbol
                };
            }
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

        /// <summary>
        /// SIDEWAY mạnh khi đồng thời:
        /// - EMA34 và EMA89 rất gần nhau
        /// - EMA34 slope nhỏ
        /// Và phải xảy ra liên tục SidewayConfirmBars cây để tránh false-positive.
        /// </summary>
        private bool IsSidewayStrong(IReadOnlyList<Candle> candles, IReadOnlyList<decimal> ema34, IReadOnlyList<decimal> ema89)
        {
            if (candles.Count < SidewaySlopeLookback + SidewayConfirmBars + 5)
                return false;

            int idx = candles.Count - 2; // nến đã đóng

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
    }
}
