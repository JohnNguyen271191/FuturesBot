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
    /// - SL theo swing + buffer (ưu tiên sâu hơn quanh EMA89), TP = 1.5R
    /// - Khi đang có vị thế:
    ///       LONG  => nếu đóng dưới EMA34 M15 => EXIT LONG
    ///       SHORT => nếu đóng trên EMA34 M15 => EXIT SHORT
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
        private const decimal RiskReward = 1.5m;             // TP = SL * 1.5
        private const decimal RiskRewardSideway = 1m;        // TP = SL * 1 cho scalp nhanh

        private const decimal RsiBullThreshold = 55m;
        private const decimal RsiBearThreshold = 45m;
        private const decimal ExtremeRsiHigh = 75m;
        private const decimal ExtremeRsiLow = 30m;
        private const decimal ExtremeEmaBoost = 0.01m;       // 1%

        // Entry offset để tránh đỉnh/đáy
        private const decimal EntryOffsetPercent = 0.003m;   // 0.3%

        // SL an toàn quanh EMA89 (0.3% xa hơn EMA89)
        private const decimal Ema89StopExtraPercent = 0.003m;


        // =====================================================================
        //                           EXIT SIGNAL
        // =====================================================================

        /// <summary>
        /// Exit: nếu đang LONG và đóng dưới EMA34 → ExitLong
        ///        nếu đang SHORT và đóng trên EMA34 → ExitShort
        /// </summary>
        public TradeSignal GenerateExitSignal(
            IReadOnlyList<Candle> candles15m,
            bool hasLongPosition,
            bool hasShortPosition,
            Symbol symbol)
        {
            if (candles15m == null || candles15m.Count < 40)
                return new TradeSignal();

            int i15 = candles15m.Count - 1;
            var last15 = candles15m[i15];

            var ema34_15 = _indicators.Ema(candles15m, 34);
            decimal ema34Now = ema34_15[i15];

            // LONG → giá đóng dưới EMA34 => EXIT
            if (hasLongPosition && last15.Close < ema34Now)
            {
                return new TradeSignal
                {
                    Type = SignalType.CloseLong,
                    Reason = $"{symbol.Coin}: Đang LONG nhưng giá đóng dưới EMA34 M15 → Exit để bảo vệ vốn."
                };
            }

            // SHORT → giá đóng trên EMA34 => EXIT
            if (hasShortPosition && last15.Close > ema34Now)
            {
                return new TradeSignal
                {
                    Type = SignalType.CloseShort,
                    Reason = $"{symbol.Coin}: Đang SHORT nhưng giá đóng trên EMA34 M15 → Exit để tránh đảo trend."
                };
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

            // =================== XÁC NHẬN TREND =========================

            bool upTrend =
                lastH1.Close > ema34_h1[iH1] &&
                ema34_h1[iH1] > ema89_h1[iH1] &&
                last15.Close > ema34_15[i15] &&
                ema34_15[i15] > ema89_15[i15];

            bool downTrend =
                lastH1.Close < ema34_h1[iH1] &&
                ema34_h1[iH1] < ema89_h1[iH1] &&
                last15.Close < ema34_15[i15] &&
                ema34_15[i15] < ema89_15[i15];

            // Extreme cases (đu trend mạnh)
            bool extremeUp =
                last15.Close > ema34_15[i15] * (1 + ExtremeEmaBoost) &&
                macd15[i15] > sig15[i15] &&
                rsi15[i15] > ExtremeRsiHigh;

            bool extremeDump =
                last15.Close < ema34_15[i15] * (1 - ExtremeEmaBoost) &&
                macd15[i15] < sig15[i15] &&
                rsi15[i15] < ExtremeRsiLow;

            // Không có trend, không extreme -> thử kèo sideway scalp, không có thì đứng im
            if (!upTrend && !downTrend && !extremeUp && !extremeDump)
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
                    symbol);

                if (sidewaySignal.Type != SignalType.None)
                    return sidewaySignal;

                return new TradeSignal();
            }

            // =================== VOLUME LỌC PULLBACK =====================

            decimal avgVol = PriceActionHelper.AverageVolume(candles15m, i15 - 1, PullbackVolumeLookback);
            bool strongVolume = avgVol > 0 && last15.Volume >= avgVol;

            // Nếu volume quá yếu và không phải case extreme thì bỏ qua
            if (!strongVolume && !extremeUp && !extremeDump)
                return new TradeSignal();

            // =================== BUILD LONG / SHORT ======================

            // --- LONG ---
            if (upTrend || extremeUp)
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
                    extremeUp);

                if (longSignal.Type != SignalType.None)
                    return longSignal;
            }

            // --- SHORT ---
            if (downTrend || extremeDump)
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
                    extremeDump);

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
            bool extremeUp)
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

            // Nếu không có hỗ trợ hợp lệ và không phải case extreme thì bỏ
            if (nearestSupport <= 0m && !extremeUp)
            {
                return new TradeSignal
                {
                    Type = SignalType.Info,
                    Reason = $"{symbol.Coin}: Uptrend nhưng không có EMA hỗ trợ gần dưới giá để retest."
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
                (rsiBull && macd15[i15] > 0) ||
                (extremeUp && rsiBull);

            bool ok = (touchSupport && reject && momentum) || (extremeUp && momentum); // cho phép đu extreme khi rất mạnh

            if (!ok)
            {
                return new TradeSignal
                {
                    Type = SignalType.Info,
                    Reason = $"{symbol.Coin}: H1 Uptrend nhưng setup long chưa đạt (touch={touchSupport}, reject={reject}, momentum={momentum})."
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
                    Reason = $"{symbol.Coin}: Không tìm được SL hợp lệ cho long."
                };
            }

            decimal sl = slCandidates.Min();

            if (sl >= entry || sl <= 0)
            {
                return new TradeSignal
                {
                    Type = SignalType.Info,
                    Reason = $"{symbol.Coin}: SL invalid cho long."
                };
            }

            decimal risk = entry - sl;
            decimal tp = entry + risk * RiskReward;

            return new TradeSignal
            {
                Type = SignalType.Long,
                EntryPrice = entry,
                StopLoss = sl,
                TakeProfit = tp,
                Reason = $"{symbol.Coin}: LONG – trend up + retest EMA hỗ trợ gần nhất ({nearestSupport:F6}) + rejection + momentum + entryOffset (SL dựa trên swing/EMA89)."
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
            bool extremeDump)
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

            if (nearestResistance <= 0m && !extremeDump)
            {
                return new TradeSignal
                {
                    Type = SignalType.Info,
                    Reason = $"{symbol.Coin}: Downtrend nhưng không có EMA kháng cự gần trên giá để retest."
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
                (rsiBear && macd15[i15] < 0) ||
                (extremeDump && rsiBear);

            bool ok = (retest && reject && momentum) || (extremeDump && momentum); // cho phép đu extreme

            if (!ok)
            {
                return new TradeSignal
                {
                    Type = SignalType.Info,
                    Reason = $"{symbol.Coin}: H1 Downtrend nhưng setup short chưa đạt (retest={retest}, reject={reject}, momentum={momentum})."
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
                    Reason = $"{symbol.Coin}: Không tìm được SL hợp lệ cho short."
                };
            }

            decimal sl = slCandidates.Max();

            if (sl <= entry)
            {
                return new TradeSignal
                {
                    Type = SignalType.Info,
                    Reason = $"{symbol.Coin}: SL invalid cho short."
                };
            }

            decimal risk = sl - entry;
            decimal tp = entry - risk * RiskReward;

            return new TradeSignal
            {
                Type = SignalType.Short,
                EntryPrice = entry,
                StopLoss = sl,
                TakeProfit = tp,
                Reason = $"{symbol.Coin}: SHORT – trend down + retest EMA kháng cự gần nhất ({nearestResistance:F6}) + rejection + momentum + entryOffset (SL dựa trên swing/EMA89)."
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
            Symbol symbol)
        {
            int i15 = candles15m.Count - 1;

            decimal ema34 = ema34_15[i15];
            decimal ema89 = ema89_15[i15];
            decimal ema200 = ema200_15[i15];

            // 1) XÁC ĐỊNH BIAS SIDEWAY THEO EMA 15M + VỊ TRÍ GIÁ
            //    - Short bias: giá dưới EMA34, EMA34 thấp nhất -> cấu trúc hơi down
            //    - Long bias : giá trên EMA34, EMA34 cao nhất -> cấu trúc hơi up

            bool shortBias =
                last15.Close < ema34 &&
                ema34 <= ema89 &&
                ema34 <= ema200;

            bool longBias =
                last15.Close > ema34 &&
                ema34 >= ema89 &&
                ema34 >= ema200;

            if (!shortBias && !longBias)
            {
                return new TradeSignal
                {
                    Type = SignalType.Info,
                    Reason = $"{symbol.Coin}: SIDEWAY – không có bias rõ (ema34={ema34:F2}, ema89={ema89:F2}, ema200={ema200:F2})."
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
                    return new TradeSignal(); // không có kháng cự để bám

                decimal nearestRes = resistances.Min();

                // Giá phải chạm vùng quanh kháng cự
                bool touchRes =
                    last15.High >= nearestRes * (1 - EmaRetestBand) &&
                    last15.High <= nearestRes * (1 + EmaRetestBand);

                // Nến rejection: râu trên chọc qua EMA, thân nằm dưới EMA, cho phép đỏ hoặc doji hơi đỏ
                bool bearBody = last15.Close <= last15.Open; // close <= open
                bool reject =
                    last15.High > nearestRes &&
                    last15.Close < nearestRes &&
                    bearBody;

                // Momentum đảo chiều: RSI trước đó đã hơi cao rồi gãy xuống, MACD cong xuống
                bool rsiTurnDown =
                    rsi15[i15 - 1] >= 50m &&      // trước đó hơi nóng
                    rsi15[i15] <= rsi15[i15 - 1];

                bool macdTurnDown =
                    macd15[i15] <= macd15[i15 - 1];

                bool momentum = rsiTurnDown && macdTurnDown;

                if (!(touchRes && reject && momentum))
                    return new TradeSignal();

                // ENTRY: đặt cao hơn close 1 chút để không đu đáy nến
                decimal rawEntry = last15.Close * (1 + EntryOffsetPercent);

                // SL/TP: scalp nhanh, RR = 1R
                decimal swingHigh = PriceActionHelper.FindSwingHigh(candles15m, i15, SwingLookback);

                decimal entry = rawEntry;
                if (entry >= swingHigh)
                    entry = (last15.Close + swingHigh) / 2;

                decimal sl = swingHigh + entry * StopBufferPercent;

                if (sl <= entry)
                    return new TradeSignal();

                decimal risk = sl - entry;
                decimal tp = entry - risk * RiskRewardSideway; // RR=1

                return new TradeSignal
                {
                    Type = SignalType.Short,
                    EntryPrice = entry,
                    StopLoss = sl,
                    TakeProfit = tp,
                    Reason = $"{symbol.Coin}: SIDEWAY SCALP SHORT – bias down 15M, retest EMA (near={nearestRes:F2}) + rejection + RSI/MACD quay đầu."
                };
            }

            // ========================== SCALP LONG ============================
            // Hỗ trợ gần nhất phía dưới giá: ưu tiên EMA89, rồi tới EMA200
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

                // Nến rejection: râu dưới xuyên EMA, thân đóng trên EMA, cho phép xanh hoặc doji hơi xanh
                bool bullBody = last15.Close >= last15.Open;
                bool reject =
                    last15.Low < nearestSup &&
                    last15.Close > nearestSup &&
                    bullBody;

                // Momentum đảo chiều lên: RSI trước đó hơi thấp rồi bật lên, MACD cong lên
                bool rsiTurnUp =
                    rsi15[i15 - 1] <= 50m &&
                    rsi15[i15] >= rsi15[i15 - 1];

                bool macdTurnUp =
                    macd15[i15] >= macd15[i15 - 1];

                bool momentum = rsiTurnUp && macdTurnUp;

                if (!(touchSup && reject && momentum))
                    return new TradeSignal();

                decimal rawEntry = last15.Close * (1 - EntryOffsetPercent);

                decimal swingLow = PriceActionHelper.FindSwingLow(candles15m, i15, SwingLookback);

                decimal entry = rawEntry;
                if (entry <= swingLow)
                    entry = (last15.Close + swingLow) / 2;

                decimal sl = swingLow - entry * StopBufferPercent;

                if (sl >= entry || sl <= 0)
                    return new TradeSignal();

                decimal risk = entry - sl;
                decimal tp = entry + risk * RiskRewardSideway;

                return new TradeSignal
                {
                    Type = SignalType.Long,
                    EntryPrice = entry,
                    StopLoss = sl,
                    TakeProfit = tp,
                    Reason = $"{symbol.Coin}: SIDEWAY SCALP LONG – bias up 15M, retest EMA (near={nearestSup:F2}) + rejection + RSI/MACD quay đầu."
                };
            }
        }
    }
}
