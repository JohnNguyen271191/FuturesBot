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
    /// - Momentum MACD + RSI
    /// - Entry offset để tránh vào đúng đỉnh/đáy (giảm rủi ro)
    /// - SL theo swing + buffer (ưu tiên sâu hơn quanh EMA89)
    /// - Khi đang có vị thế:
    ///       LONG  => nếu đóng dưới EMA34 M15 => EXIT LONG
    ///       SHORT => nếu đóng trên EMA34 M15 => EXIT SHORT
    /// - Sideway scalp:
    ///       + Dùng khi H1 có bias rõ nhưng M15 chưa align / đang sideway quanh EMA
    ///       + Chỉ trade theo hướng bias H1 (H1 down chỉ short, H1 up chỉ long)
    /// - Tối ưu BTC/ETH vs Altcoin (symbol.IsMajor):
    ///       + IsMajor = true (BTC/ETH): cho phép trend trade + sideway scalp,
    ///         RR trend riêng (RiskRewardMajor), RR sideway (RiskRewardSidewayMajor).
    ///       + IsMajor = false (Altcoin): chỉ trend trade, thêm filter Volume / EMA slope H1,
    ///         bỏ sideway scalp, RR trend dùng RiskReward.
    /// </summary>
    public class TradingStrategy(IndicatorService indicators) : IStrategyService
    {
        private readonly IndicatorService _indicators = indicators;

        // ========================= CONFIG ==============================

        private const int MinBars = 120;
        private const int SwingLookback = 5;
        private const int PullbackVolumeLookback = 5;

        private const decimal EmaRetestBand = 0.002m;        // ±0.2%
        private const decimal StopBufferPercent = 0.005m;    // 0.5%

        // RR default cho Altcoin (trend)
        private const decimal RiskReward = 1.5m;             // TP = SL * 1.5

        // RR cho sideway scalp (cơ bản)
        private const decimal RiskRewardSideway = 1m;        // TP = SL * 1 cho scalp nhanh

        // RR cho Major (BTC/ETH)
        private const decimal RiskRewardMajor = 2.0m;        // RR trend cho BTC/ETH
        private const decimal RiskRewardSidewayMajor = 1.0m; // RR sideway cho BTC/ETH

        private const decimal RsiBullThreshold = 55m;
        private const decimal RsiBearThreshold = 45m;
        private const decimal ExtremeRsiHigh = 75m;
        private const decimal ExtremeRsiLow = 30m;
        private const decimal ExtremeEmaBoost = 0.01m;       // 1%

        // Entry offset để tránh đỉnh/đáy
        private const decimal EntryOffsetPercent = 0.003m;          // 0.3% cho trend
        private const decimal EntryOffsetPercentForScal = 0.001m;   // 0.1% cho scalp

        // SL an toàn quanh EMA89 (0.3% xa hơn EMA89)
        private const decimal Ema89StopExtraPercent = 0.003m;

        // Nến climax + overextended xa EMA gần nhất (tránh vừa vào là đảo)
        private const int ClimaxLookback = 20;
        private const decimal ClimaxBodyMultiplier = 1.8m;
        private const decimal ClimaxVolumeMultiplier = 1.5m;
        private const decimal OverextendedFromEmaPercent = 0.01m; // 1% xa EMA gần nhất

        // ========================= NEW: BTC vs ALT CONFIG ====================

        // Volume M15 (ước lượng USDT = Close * Volume) tối thiểu cho trade
        private const decimal MinMajorVolumeUsd15m = 20_000_000m; // BTC
        private const decimal MinAltVolumeUsd15m = 3_000_000m;    // Altcoin

        // Độ dốc EMA34 H1 tối thiểu cho Altcoin (tránh alt sideway)
        private const int EmaSlopeLookbackH1 = 3;
        private const decimal MinAltEmaSlopeH1 = 0.003m; // 0.3%

        // =====================================================================
        //                           EXIT SIGNAL
        // =====================================================================

        /// <summary>
        /// Exit: nếu đang LONG và đóng dưới EMA dynamic bên dưới giá → ExitLong
        ///        nếu đang SHORT và đóng trên EMA dynamic bên trên giá → ExitShort
        /// </summary>
        public TradeSignal GenerateExitSignal(
            IReadOnlyList<Candle> candles15m,
            bool hasLongPosition,
            bool hasShortPosition,
            Symbol symbol)
        {
            if (candles15m == null || candles15m.Count < 200)
                return new TradeSignal();

            int i15 = candles15m.Count - 1;
            var last15 = candles15m[i15];

            // Tính EMA 34 / 89 / 200 M15
            var ema34_15 = _indicators.Ema(candles15m, 34);
            var ema89_15 = _indicators.Ema(candles15m, 89);
            var ema200_15 = _indicators.Ema(candles15m, 200);

            decimal ema34Now = ema34_15[i15];
            decimal ema89Now = ema89_15[i15];
            decimal ema200Now = ema200_15[i15];

            // ===== LONG: dùng EMA gần nhất bên dưới giá =====
            if (hasLongPosition)
            {
                var supports = new List<decimal>();

                if (ema34Now <= last15.Close) supports.Add(ema34Now);
                if (ema89Now <= last15.Close) supports.Add(ema89Now);
                if (ema200Now <= last15.Close) supports.Add(ema200Now);

                decimal? dynamicSupportEma = null;
                if (supports.Count > 0)
                {
                    // EMA gần nhất bên dưới giá (max trong số <= price)
                    dynamicSupportEma = supports.Max();
                }

                if (dynamicSupportEma.HasValue && last15.Close < dynamicSupportEma.Value)
                {
                    return new TradeSignal
                    {
                        Type = SignalType.CloseLong,
                        Reason =
                            $"{symbol.Coin}: Đang LONG, giá đóng dưới EMA{DetectEmaPeriod(dynamicSupportEma.Value, ema34Now, ema89Now, ema200Now)} M15 (dynamic support) → Exit để bảo vệ vốn.",
                        Coin = symbol.Coin
                    };
                }
            }

            // ===== SHORT: dùng EMA gần nhất bên trên giá =====
            if (hasShortPosition)
            {
                var resistances = new List<decimal>();

                if (ema34Now >= last15.Close) resistances.Add(ema34Now);
                if (ema89Now >= last15.Close) resistances.Add(ema89Now);
                if (ema200Now >= last15.Close) resistances.Add(ema200Now);

                decimal? dynamicResistEma = null;
                if (resistances.Count > 0)
                {
                    // EMA gần nhất bên trên giá (min trong số >= price)
                    dynamicResistEma = resistances.Min();
                }

                if (dynamicResistEma.HasValue && last15.Close > dynamicResistEma.Value)
                {
                    return new TradeSignal
                    {
                        Type = SignalType.CloseShort,
                        Reason =
                            $"{symbol.Coin}: Đang SHORT, giá đóng trên EMA{DetectEmaPeriod(dynamicResistEma.Value, ema34Now, ema89Now, ema200Now)} M15 (dynamic resistance) → Exit để tránh đảo trend.",
                        Coin = symbol.Coin
                    };
                }
            }

            return new TradeSignal();
        }

        // =====================================================================
        //                           ENTRY SIGNAL
        // =====================================================================

        public TradeSignal GenerateSignal(
            IReadOnlyList<Candle> candles15m,
            IReadOnlyList<Candle> candles1h,
            Symbol symbol)
        {
            if (candles15m.Count < MinBars || candles1h.Count < MinBars)
                return new TradeSignal();

            int i15 = candles15m.Count - 1;
            int iH1 = candles1h.Count - 1;

            var last15 = candles15m[i15];
            var prev15 = candles15m[i15 - 1];
            var lastH1 = candles1h[iH1];

            // --- Indicators ---
            var ema34_15 = _indicators.Ema(candles15m, 34);
            var ema89_15 = _indicators.Ema(candles15m, 89);
            var ema200_15 = _indicators.Ema(candles15m, 200);

            var ema34_h1 = _indicators.Ema(candles1h, 34);
            var ema89_h1 = _indicators.Ema(candles1h, 89);
            var ema200_h1 = _indicators.Ema(candles1h, 200);

            var rsi15 = _indicators.Rsi(candles15m, 6);
            var (macd15, sig15, _) = _indicators.Macd(candles15m, 5, 13, 5);

            decimal ema34_15Now = ema34_15[i15];
            decimal ema89_15Now = ema89_15[i15];
            decimal ema200_15Now = ema200_15[i15];

            // =================== NEW: BTC vs ALT PROFILE =======================

            bool isMajor = symbol.IsMajor; // BTC/ETH = true, Alt = false

            // RR theo profile
            decimal rrTrend = isMajor ? RiskRewardMajor : RiskReward;
            decimal rrSideway = isMajor ? RiskRewardSidewayMajor : RiskRewardSideway;
            bool allowSideway = isMajor; // chỉ Major được sideway scalp

            // Volume ước lượng trên M15
            decimal volUsd15 = last15.Close * last15.Volume;

            if (isMajor)
            {
                // Major coin mà vol M15 quá thấp (hiếm khi xảy ra) thì bỏ
                if (volUsd15 < MinMajorVolumeUsd15m)
                {
                    return new TradeSignal
                    {
                        Type = SignalType.Info,
                        Reason =
                            $"{symbol.Coin}: Volume M15 quá thấp ({volUsd15:F0} USDT) → bỏ qua để tránh slippage.",
                        Coin = symbol.Coin
                    };
                }
            }
            else
            {
                // ALT: kiểm tra thanh khoản M15
                if (volUsd15 < MinAltVolumeUsd15m)
                {
                    return new TradeSignal
                    {
                        Type = SignalType.Info,
                        Reason =
                            $"{symbol.Coin}: Altcoin volume M15 yếu ({volUsd15:F0} USDT) → bỏ qua, tránh bị pump-dump/slippage.",
                        Coin = symbol.Coin
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
                                Type = SignalType.Info,
                                Reason =
                                    $"{symbol.Coin}: Altcoin đang sideway, EMA34 H1 gần như đi ngang (slope={slope:P2}) → chỉ trade khi trend rõ để tránh nhiễu.",
                                Coin = symbol.Coin
                            };
                        }
                    }
                }
            }

            // ================= FILTER: CLIMAX + OVEREXTENDED = NO TRADE ========

            bool climaxDanger =
                IsClimaxAwayFromEma(candles15m, i15, ema34_15Now, ema89_15Now, ema200_15Now) ||
                IsClimaxAwayFromEma(candles15m, i15 - 1, ema34_15Now, ema89_15Now, ema200_15Now);

            if (climaxDanger)
            {
                return new TradeSignal
                {
                    Type = SignalType.Info,
                    Reason =
                        $"{symbol.Coin}: Bỏ qua entry vì vừa có nến climax và giá đang quá xa EMA gần nhất (đu đỉnh/đu đáy, chờ retest EMA rồi mới trade).",
                    Coin = symbol.Coin
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

            // Extreme cases (vẫn giữ để tham khảo – KHÔNG dùng để cho phép entry đu trend)
            bool extremeUp =
                last15.Close > ema34_15Now * (1 + ExtremeEmaBoost) &&
                macd15[i15] > sig15[i15] &&
                rsi15[i15] > ExtremeRsiHigh;

            bool extremeDump =
                last15.Close < ema34_15Now * (1 - ExtremeEmaBoost) &&
                macd15[i15] < sig15[i15] &&
                rsi15[i15] < ExtremeRsiLow;

            // =================== SIDEWAY / PULLBACK SCALP ======================
            //
            // - Major (BTC/ETH): cho phép sideway scalp.
            // - Altcoin: KHÔNG sideway scalp nữa, chỉ cho trade khi upTrend/downTrend rõ.
            // ===================================================================

            if (!extremeUp && !extremeDump)
            {
                bool m15PullbackDown =
                    h1BiasDown &&
                    ema34_15Now <= last15.Close &&
                    last15.Close <= ema89_15Now;   // giá nằm giữa EMA34-EMA89 trong H1 downtrend

                bool m15PullbackUp =
                    h1BiasUp &&
                    ema89_15Now <= last15.Close &&
                    last15.Close <= ema34_15Now;   // giá nằm giữa EMA89-EMA34 trong H1 uptrend

                bool shouldTrySideway =
                    (!upTrend && !downTrend) || m15PullbackDown || m15PullbackUp;

                // ======= Major: cho phép sideway scalp =======
                if (allowSideway && shouldTrySideway)
                {
                    var sidewaySignal = BuildSidewayScalp(
                        candles15m,
                        ema34_15,
                        ema89_15,
                        ema200_15,
                        rsi15,
                        macd15,
                        sig15,
                        last15,
                        symbol,
                        h1BiasUp,
                        h1BiasDown,
                        rrSideway);

                    if (sidewaySignal.Type != SignalType.None)
                        return sidewaySignal;
                }
                // ======= Altcoin: nếu chỉ sideway/pullback mà chưa có trend rõ → bỏ qua =======
                else if (!isMajor && shouldTrySideway && !upTrend && !downTrend)
                {
                    return new TradeSignal
                    {
                        Type = SignalType.Info,
                        Reason =
                            $"{symbol.Coin}: Altcoin đang sideway/pullback, H1+M15 chưa align trend rõ → bỏ qua, không scalp để tránh nhiễu.",
                        Coin = symbol.Coin
                    };
                }
            }

            // =================== VOLUME LỌC PULLBACK =====================

            decimal avgVol = PriceActionHelper.AverageVolume(candles15m, i15 - 1, PullbackVolumeLookback);
            bool strongVolume = avgVol > 0 && last15.Volume >= avgVol;

            // Nếu volume quá yếu và không phải case extreme thì bỏ qua
            if (!strongVolume && !extremeUp && !extremeDump)
                return new TradeSignal();

            // =================== BUILD LONG / SHORT (THEO TREND MẠNH) =========

            // --- LONG ---
            if (upTrend /* không dùng extremeUp để cho phép đu trend */)
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
                    prev15,
                    symbol,
                    rrTrend);

                if (longSignal.Type != SignalType.None)
                    return longSignal;
            }

            // --- SHORT ---
            if (downTrend /* không dùng extremeDump */)
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
                    prev15,
                    symbol,
                    rrTrend);

                if (shortSignal.Type != SignalType.None)
                    return shortSignal;
            }

            return new TradeSignal();
        }

        // =====================================================================
        //                              LONG
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
            Candle prev15,
            Symbol symbol,
            decimal riskRewardTrend)
        {
            int i15 = candles15m.Count - 1;

            decimal ema34 = ema34_15[i15];
            decimal ema89 = ema89_15[i15];
            decimal ema200 = ema200_15[i15];

            // 1. XÁC ĐỊNH HỖ TRỢ GẦN NHẤT (EMA 34/89/200 ở dưới giá)
            var supports = new List<decimal>();

            if (ema34 < last15.Close)
                supports.Add(ema34);

            if (ema89 > 0 && ema89 < last15.Close)
                supports.Add(ema89);

            if (ema200 > 0 && ema200 < last15.Close)
                supports.Add(ema200);

            decimal nearestSupport = supports.Count > 0
                ? supports.Max()
                : 0m;

            // Nếu không có hỗ trợ hợp lệ thì bỏ
            if (nearestSupport <= 0m)
            {
                return new TradeSignal
                {
                    Type = SignalType.Info,
                    Reason = $"{symbol.Coin}: Uptrend nhưng không có EMA hỗ trợ gần dưới giá để retest.",
                    Coin = symbol.Coin
                };
            }

            // 2. RETEST HỖ TRỢ (giá chạm band quanh EMA gần nhất)
            bool touchSupport = nearestSupport > 0m &&
                                last15.Low <= nearestSupport * (1 + EmaRetestBand) &&
                                last15.Low >= nearestSupport * (1 - EmaRetestBand);

            // 3. NẾN REJECTION (đuôi dưới chọc EMA, thân xanh đóng trên EMA)
            bool reject = nearestSupport > 0m &&
                          last15.Close > last15.Open &&
                          last15.Low < nearestSupport &&
                          last15.Close > nearestSupport;

            // 4. MOMENTUM MACD + RSI
            bool macdCrossUp = macd15[i15] > sig15[i15] && macd15[i15 - 1] <= sig15[i15 - 1];
            bool rsiBull = rsi15[i15] > RsiBullThreshold && rsi15[i15] >= rsi15[i15 - 1];

            bool momentum =
                (macdCrossUp && rsiBull) ||
                (rsiBull && macd15[i15] > 0);

            bool ok = touchSupport && reject && momentum; // BẮT BUỘC có retest EMA

            if (!ok)
            {
                return new TradeSignal
                {
                    Type = SignalType.Info,
                    Reason =
                        $"{symbol.Coin}: H1 Uptrend nhưng setup long chưa đạt (touch={touchSupport}, reject={reject}, momentum={momentum}).",
                    Coin = symbol.Coin
                };
            }

            // 5. ENTRY OFFSET – đặt entry thấp hơn close 1 chút để tránh đu đỉnh
            decimal rawEntry = last15.Close * (1 - EntryOffsetPercent);

            // 6. SL & TP – dùng swing low + EMA89 để tránh bị quét
            decimal swingLow = PriceActionHelper.FindSwingLow(candles15m, i15, SwingLookback);

            decimal entry = rawEntry;
            if (entry <= swingLow)
                entry = (last15.Close + swingLow) / 2;

            // SL candidate 1: dưới swingLow 0.5% của entry
            decimal slFromSwing = swingLow - entry * StopBufferPercent;

            // SL candidate 2: dưới EMA89 0.3% (nếu EMA89 nằm dưới entry)
            decimal slFromEma89 = 0m;
            if (ema89 > 0 && ema89 < entry)
            {
                slFromEma89 = ema89 * (1 - Ema89StopExtraPercent);
            }

            // Chọn SL sâu hơn trong các candidate (giá nhỏ hơn → sâu hơn)
            var slCandidates = new List<decimal>();
            if (slFromSwing > 0) slCandidates.Add(slFromSwing);
            if (slFromEma89 > 0) slCandidates.Add(slFromEma89);

            if (slCandidates.Count == 0)
            {
                return new TradeSignal
                {
                    Type = SignalType.Info,
                    Reason = $"{symbol.Coin}: Không tìm được SL hợp lệ cho long.",
                    Coin = symbol.Coin
                };
            }

            decimal sl = slCandidates.Min();

            if (sl >= entry || sl <= 0)
            {
                return new TradeSignal
                {
                    Type = SignalType.None,
                    Reason = $"{symbol.Coin}: SL invalid cho long.",
                    Coin = symbol.Coin
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
                Reason =
                    $"{symbol.Coin}: LONG – trend up + retest EMA hỗ trợ gần nhất ({nearestSupport:F6}) + rejection + momentum + entryOffset (SL dựa trên swing/EMA89).",
                Coin = symbol.Coin
            };
        }

        // =====================================================================
        //                              SHORT
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
            Candle prev15,
            Symbol symbol,
            decimal riskRewardTrend)
        {
            int i15 = candles15m.Count - 1;

            decimal ema34 = ema34_15[i15];
            decimal ema89 = ema89_15[i15];
            decimal ema200 = ema200_15[i15];

            // 1. XÁC ĐỊNH KHÁNG CỰ GẦN NHẤT (EMA 34/89/200 ở trên giá)
            var resistances = new List<decimal>();

            if (ema34 > last15.Close)
                resistances.Add(ema34);

            if (ema89 > last15.Close)
                resistances.Add(ema89);

            if (ema200 > last15.Close)
                resistances.Add(ema200);

            decimal nearestResistance = resistances.Count > 0
                ? resistances.Min()
                : 0m;

            if (nearestResistance <= 0m)
            {
                return new TradeSignal
                {
                    Type = SignalType.Info,
                    Reason = $"{symbol.Coin}: Downtrend nhưng không có EMA kháng cự gần trên giá để retest.",
                    Coin = symbol.Coin
                };
            }

            // 2. RETEST KHÁNG CỰ
            bool retest = nearestResistance > 0m &&
                          last15.High >= nearestResistance * (1 - EmaRetestBand) &&
                          last15.High <= nearestResistance * (1 + EmaRetestBand);

            // 3. NẾN REJECTION (đuôi trên chọc EMA, thân đỏ đóng dưới EMA)
            bool reject = nearestResistance > 0m &&
                          last15.Close < last15.Open &&
                          last15.High > nearestResistance &&
                          last15.Close < nearestResistance;

            // 4. MOMENTUM MACD + RSI
            bool macdCrossDown = macd15[i15] < sig15[i15] && macd15[i15 - 1] >= sig15[i15 - 1];
            bool rsiBear = rsi15[i15] < RsiBearThreshold && rsi15[i15] <= rsi15[i15 - 1];

            bool momentum =
                (macdCrossDown && rsiBear) ||
                (rsiBear && macd15[i15] < 0);

            bool ok = retest && reject && momentum;  // BẮT BUỘC retest EMA

            if (!ok)
            {
                return new TradeSignal
                {
                    Type = SignalType.Info,
                    Reason =
                        $"{symbol.Coin}: H1 Downtrend nhưng setup short chưa đạt (retest={retest}, reject={reject}, momentum={momentum}).",
                    Coin = symbol.Coin
                };
            }

            // 5. ENTRY OFFSET – đặt entry cao hơn close để tránh đu đáy nến
            decimal rawEntry = last15.Close * (1 + EntryOffsetPercent);

            // 6. SL & TP – dùng swing high + EMA89 để tránh bị quét
            decimal swingHigh = PriceActionHelper.FindSwingHigh(candles15m, i15, SwingLookback);

            decimal entry = rawEntry;
            if (entry >= swingHigh)
                entry = (last15.Close + swingHigh) / 2;

            // SL candidate 1: trên swingHigh 0.5% của entry
            decimal slFromSwing = swingHigh + entry * StopBufferPercent;

            // SL candidate 2: trên EMA89 0.3% (nếu EMA89 nằm trên entry)
            decimal slFromEma89 = 0m;
            if (ema89 > entry)
            {
                slFromEma89 = ema89 * (1 + Ema89StopExtraPercent);
            }

            // Chọn SL xa hơn trong các candidate (giá lớn hơn → xa hơn với lệnh short)
            var slCandidates = new List<decimal>();
            if (slFromSwing > 0) slCandidates.Add(slFromSwing);
            if (slFromEma89 > 0) slCandidates.Add(slFromEma89);

            if (slCandidates.Count == 0)
            {
                return new TradeSignal
                {
                    Type = SignalType.Info,
                    Reason = $"{symbol.Coin}: Không tìm được SL hợp lệ cho short.",
                    Coin = symbol.Coin
                };
            }

            decimal sl = slCandidates.Max();

            if (sl <= entry)
            {
                return new TradeSignal
                {
                    Type = SignalType.Info,
                    Reason = $"{symbol.Coin}: SL invalid cho short.",
                    Coin = symbol.Coin
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
                Reason =
                    $"{symbol.Coin}: SHORT – trend down + retest EMA kháng cự gần nhất ({nearestResistance:F6}) + rejection + momentum + entryOffset (SL dựa trên swing/EMA89).",
                Coin = symbol.Coin
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
            Symbol symbol,
            bool h1BiasUp,
            bool h1BiasDown,
            decimal riskRewardSideway)
        {
            int i15 = candles15m.Count - 1;

            decimal ema34 = ema34_15[i15];
            decimal ema89 = ema89_15[i15];
            decimal ema200 = ema200_15[i15];

            // 1) BIAS EMA 15M (không dùng giá)
            bool shortBias15 =
                ema34 <= ema89 &&
                ema34 <= ema200;

            bool longBias15 =
                ema34 >= ema89 &&
                ema34 >= ema200;

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
                    Type = SignalType.Info,
                    Reason =
                        $"{symbol.Coin}: SIDEWAY – không có bias rõ (ema34={ema34:F2}, ema89={ema89:F2}, ema200={ema200:F2}).",
                    Coin = symbol.Coin
                };
            }

            // ========================== SCALP SHORT ===========================
            if (shortBias)
            {
                // Kháng cự gần nhất phía trên giá: ưu tiên EMA89, rồi tới EMA200
                var resistances = new List<decimal>();
                if (ema89 > last15.Close) resistances.Add(ema89);
                if (ema200 > last15.Close) resistances.Add(ema200);

                if (resistances.Count == 0)
                    return new TradeSignal();

                decimal nearestRes = resistances.Min();

                // Giá phải chạm vùng quanh kháng cự
                bool touchRes =
                    last15.High >= nearestRes * (1 - EmaRetestBand) &&
                    last15.High <= nearestRes * (1 + EmaRetestBand);

                // Nến rejection: râu trên chọc qua EMA, thân nằm dưới EMA
                bool bearBody = last15.Close <= last15.Open;
                bool reject =
                    last15.High > nearestRes &&
                    last15.Close < nearestRes &&
                    bearBody;

                // Momentum đảo chiều: RSI cao + bắt đầu gãy, MACD cong/đứng
                bool rsiHigh = rsi15[i15] >= 55m;
                bool rsiTurnDown =
                    rsi15[i15] <= rsi15[i15 - 1];

                bool macdDownOrFlat =
                    macd15[i15] <= macd15[i15 - 1];

                bool momentum = rsiHigh && (rsiTurnDown || macdDownOrFlat);

                if (!(touchRes && reject && momentum))
                    return new TradeSignal();

                decimal rawEntry = last15.Close * (1 + EntryOffsetPercentForScal);

                decimal swingHigh = PriceActionHelper.FindSwingHigh(candles15m, i15, SwingLookback);

                decimal entry = rawEntry;
                if (entry >= swingHigh)
                    entry = (last15.Close + swingHigh) / 2;

                decimal sl = swingHigh + entry * StopBufferPercent;

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
                    Reason =
                        $"{symbol.Coin}: SIDEWAY SCALP SHORT – bias down (H1) + retest EMA (near={nearestRes:F2}) + rejection + RSI/MACD quay đầu.",
                    Coin = symbol.Coin
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
                bool reject =
                    last15.Low < nearestSup &&
                    last15.Close > nearestSup &&
                    bullBody;

                bool rsiHighEnough = rsi15[i15] >= 45m;
                bool rsiTurnUp =
                    rsi15[i15] >= rsi15[i15 - 1];

                bool macdUpOrFlat =
                    macd15[i15] >= macd15[i15 - 1];

                bool momentum = rsiHighEnough && (rsiTurnUp || macdUpOrFlat);

                if (!(touchSup && reject && momentum))
                    return new TradeSignal();

                decimal rawEntry = last15.Close * (1 - EntryOffsetPercentForScal);

                decimal swingLow = PriceActionHelper.FindSwingLow(candles15m, i15, SwingLookback);

                decimal entry = rawEntry;
                if (entry <= swingLow)
                    entry = (last15.Close + swingLow) / 2;

                decimal sl = swingLow - entry * StopBufferPercent;

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
                    Reason =
                        $"{symbol.Coin}: SIDEWAY SCALP LONG – bias up (H1) + retest EMA (near={nearestSup:F2}) + rejection + RSI/MACD quay đầu.",
                    Coin = symbol.Coin
                };
            }
        }

        // =====================================================================
        //                     HELPERS: CLIMAX / EMA / SYMBOL
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

        // Nến climax + giá đang xa EMA gần nhất => overextended, không trade
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

        private int DetectEmaPeriod(decimal target, decimal ema34, decimal ema89, decimal ema200)
        {
            var diff34 = Math.Abs(target - ema34);
            var diff89 = Math.Abs(target - ema89);
            var diff200 = Math.Abs(target - ema200);

            var min = Math.Min(diff34, Math.Min(diff89, diff200));

            if (min == diff34) return 34;
            if (min == diff89) return 89;
            return 200;
        }
    }
}
