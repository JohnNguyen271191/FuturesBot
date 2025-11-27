using FuturesBot.Config;
using FuturesBot.IServices;
using FuturesBot.Models;
using FuturesBot.Utils;
using static FuturesBot.Utils.EnumTypesHelper;

namespace FuturesBot.Services
{
    /// <summary>
    /// Chiến lược:
    /// - Trend filter: H1 + M15 với EMA34/EMA89
    /// - Entry: M15 retest EMA34/EMA89 + rejection + volume + MACD + RSI
    /// - Entry không lấy đúng last15.Close mà offset 1 chút để tránh vào đỉnh/đáy.
    /// - SL: swing high/low + buffer
    /// - TP: 1.5R
    /// 
    /// ExtremeUp/ExtremeDump chỉ giúp nới lỏng filter, không auto vào lệnh.
    /// </summary>
    public class TradingStrategy(IndicatorService indicators) : IStrategyService
    {
        private readonly IndicatorService _indicators = indicators;

        // ===== Config =====
        private const int MinBars = 120;
        private const int SwingLookback = 5;
        private const int PullbackVolumeLookback = 3;

        private const decimal EmaRetestBand = 0.002m;        // ±0.2% quanh EMA xem như "chạm"
        private const decimal BreakoutBand = 0.001m;         // 0.1% để xác nhận breakout
        private const decimal StopBufferPercent = 0.005m;    // 0.5% buffer tránh quét râu
        private const decimal RiskReward = 1.5m;             // TP = 1.5R

        private const decimal RsiBullThreshold = 55m;
        private const decimal RsiBearThreshold = 45m;
        private const decimal ExtremeRsiHigh = 75m;
        private const decimal ExtremeRsiLow = 30m;
        private const decimal ExtremeEmaBoost = 0.01m;       // 1% lệch EMA cho extreme

        // 🔥 Offset entry so với last close (0.1%)
        // Long:  entry = Close * (1 - EntryOffsetPercent)
        // Short: entry = Close * (1 + EntryOffsetPercent)
        private const decimal EntryOffsetPercent = 0.003m;   // 0.1%

        public TradeSignal GenerateSignal(
            IReadOnlyList<Candle> candles15m,
            IReadOnlyList<Candle> candles1h,
            Symbol symbol)
        {
            // ===== 0. Validate =====
            if (candles15m.Count < MinBars || candles1h.Count < MinBars)
                return new TradeSignal();

            int i15 = candles15m.Count - 1;
            int iH1 = candles1h.Count - 1;

            var last15 = candles15m[i15];
            var prev15 = candles15m[i15 - 1];
            var lastH1 = candles1h[iH1];

            // ===== 1. Indicators =====
            var ema34_15 = _indicators.Ema(candles15m, 34);
            var ema89_15 = _indicators.Ema(candles15m, 89);
            var ema34_h1 = _indicators.Ema(candles1h, 34);

            var rsi15 = _indicators.Rsi(candles15m, 6);
            var (macd15, sig15, _) = _indicators.Macd(candles15m, 5, 13, 5);

            // ===== 2. Trend & extreme detection =====
            bool upTrend =
                lastH1.Close > ema34_h1[iH1] &&
                last15.Close > ema34_15[i15] &&
                ema34_15[i15] > ema89_15[i15];

            bool downTrend =
                lastH1.Close < ema34_h1[iH1] &&
                last15.Close < ema34_15[i15] &&
                ema34_15[i15] < ema89_15[i15];

            bool extremeUp =
                last15.Close > ema34_15[i15] * (1 + ExtremeEmaBoost) &&
                macd15[i15] > sig15[i15] &&
                rsi15[i15] > ExtremeRsiHigh;

            bool extremeDump =
                last15.Close < ema34_15[i15] * (1 - ExtremeEmaBoost) &&
                macd15[i15] < sig15[i15] &&
                rsi15[i15] < ExtremeRsiLow;

            // Không trend rõ & không extreme => bỏ
            if (!upTrend && !downTrend && !extremeDump && !extremeUp)
                return new TradeSignal();

            // ===== 3. Volume filter =====
            decimal avgPullbackVol = PriceActionHelper.AverageVolume(candles15m, i15 - 1, PullbackVolumeLookback);
            decimal currentVol = last15.Volume;
            bool strongVolume = avgPullbackVol > 0 && currentVol >= avgPullbackVol;

            // Trong extremeUp/extremeDump vẫn cho trade dù volume yếu
            if (!strongVolume && !extremeUp && !extremeDump)
                return new TradeSignal();

            // ===== 4. LONG SETUP =====
            if (upTrend || extremeUp)
            {
                var longSignal = TryBuildLongSignal(
                    candles15m, ema34_15, ema89_15,
                    rsi15, macd15, sig15,
                    last15, prev15, symbol,
                    extremeUp);

                if (longSignal.Type != SignalType.None)
                    return longSignal;
            }

            // ===== 5. SHORT SETUP =====
            if (downTrend || extremeDump)
            {
                var shortSignal = TryBuildShortSignal(
                    candles15m, ema34_15, ema89_15,
                    rsi15, macd15, sig15,
                    last15, prev15, symbol,
                    extremeDump);

                if (shortSignal.Type != SignalType.None)
                    return shortSignal;
            }

            return new TradeSignal();
        }

        // ================== LONG ==================

        private TradeSignal TryBuildLongSignal(
            IReadOnlyList<Candle> candles15m,
            IReadOnlyList<decimal> ema34_15,
            IReadOnlyList<decimal> ema89_15,
            IReadOnlyList<decimal> rsi15,
            IReadOnlyList<decimal> macd15,
            IReadOnlyList<decimal> sig15,
            Candle last15,
            Candle prev15,
            Symbol symbol,
            bool extremeUp)
        {
            int i15 = candles15m.Count - 1;

            // (1) Xác nhận đã breakout EMA34 trước đó
            bool brokeAboveEma34Recently =
                prev15.Close >= ema34_15[i15 - 1] * (1 + BreakoutBand);

            if (!brokeAboveEma34Recently && !extremeUp)
            {
                return new TradeSignal
                {
                    Type = SignalType.Info,
                    Reason = $"{symbol.Coin} : uptrend nhưng chưa có breakout rõ trước đó."
                };
            }

            // (2) Retest EMA34/EMA89: Low chạm band quanh EMA và Close trở lại trên EMA34
            decimal ema34Now = ema34_15[i15];
            decimal ema89Now = ema89_15[i15];

            bool retestEma34 =
                last15.Low <= ema34Now * (1 + EmaRetestBand) &&
                last15.Low >= ema34Now * (1 - EmaRetestBand);

            bool retestEma89 =
                last15.Low <= ema89Now * (1 + EmaRetestBand) &&
                last15.Low >= ema89Now * (1 - EmaRetestBand);

            bool retestEma = retestEma34 || retestEma89;

            // (3) Price action: nến xanh + đóng trên EMA34 (rejection)
            bool bullishReject =
                last15.Close > last15.Open &&
                last15.Close > ema34Now;

            // (4) Momentum: MACD + RSI
            bool macdCrossUp =
                macd15[i15] > sig15[i15] &&
                macd15[i15 - 1] <= sig15[i15 - 1];

            bool rsiBull =
                rsi15[i15] > RsiBullThreshold &&
                rsi15[i15] > rsi15[i15 - 1];

            // Cho extremeUp nới lỏng điều kiện MACD (chỉ cần MACD > 0)
            bool momentumOk =
                (macdCrossUp && rsiBull) ||
                (rsiBull && macd15[i15] > 0) ||
                (extremeUp && rsiBull);

            bool shouldLong = retestEma && bullishReject && momentumOk;

            if (!shouldLong)
            {
                return new TradeSignal
                {
                    Type = SignalType.Info,
                    Reason = $"{symbol.Coin} : uptrend nhưng không đủ setup long (retestEma={retestEma}, bullishReject={bullishReject}, momentumOk={momentumOk}, extremeUp={extremeUp})."
                };
            }

            // (5) Tính Entry/SL/TP
            // Entry: thấp hơn Close một chút để tránh vào đỉnh nến setup
            decimal rawEntry = last15.Close * (1 - EntryOffsetPercent);

            decimal swingLow = PriceActionHelper.FindSwingLow(candles15m, i15, lookback: SwingLookback);
            decimal buffer = rawEntry * StopBufferPercent;
            decimal sl = swingLow - buffer;

            // Nếu offset làm entry nằm dưới swingLow quá sâu -> fallback về giữa Close & swingLow
            decimal entry = rawEntry;
            if (entry <= swingLow)
                entry = (last15.Close + swingLow) / 2;

            // Recalc SL theo entry mới
            buffer = entry * StopBufferPercent;
            sl = swingLow - buffer;

            if (sl <= 0 || sl >= entry)
            {
                return new TradeSignal
                {
                    Type = SignalType.Info,
                    Reason = $"{symbol.Coin} : uptrend nhưng SL invalid (sl={sl}, entry={entry})."
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
                Reason = $"{symbol.Coin} : LONG uptrend – breakout trước đó, retest EMA + bullish rejection + momentum OK (entry offset dưới Close)."
            };
        }

        // ================== SHORT ==================

        private TradeSignal TryBuildShortSignal(
            IReadOnlyList<Candle> candles15m,
            IReadOnlyList<decimal> ema34_15,
            IReadOnlyList<decimal> ema89_15,
            IReadOnlyList<decimal> rsi15,
            IReadOnlyList<decimal> macd15,
            IReadOnlyList<decimal> sig15,
            Candle last15,
            Candle prev15,
            Symbol symbol,
            bool extremeDump)
        {
            int i15 = candles15m.Count - 1;

            // (1) Breakout xuống EMA34
            bool brokeBelowEma34Recently =
                prev15.Close <= ema34_15[i15 - 1] * (1 - BreakoutBand);

            if (!brokeBelowEma34Recently && !extremeDump)
            {
                return new TradeSignal
                {
                    Type = SignalType.Info,
                    Reason = $"{symbol.Coin} : downtrend nhưng chưa có breakout rõ trước đó."
                };
            }

            // (2) Retest EMA34/89 từ dưới lên
            decimal ema34Now = ema34_15[i15];
            decimal ema89Now = ema89_15[i15];

            bool retestEma34 =
                last15.High >= ema34Now * (1 - EmaRetestBand) &&
                last15.High <= ema34Now * (1 + EmaRetestBand);

            bool retestEma89 =
                last15.High >= ema89Now * (1 - EmaRetestBand) &&
                last15.High <= ema89Now * (1 + EmaRetestBand);

            bool retestEma = retestEma34 || retestEma89;

            // (3) Price action: nến đỏ + đóng dưới EMA34
            bool bearishReject =
                last15.Close < last15.Open &&
                last15.Close < ema34Now;

            // (4) Momentum: MACD + RSI
            bool macdCrossDown =
                macd15[i15] < sig15[i15] &&
                macd15[i15 - 1] >= sig15[i15 - 1];

            bool rsiBear =
                rsi15[i15] < RsiBearThreshold &&
                rsi15[i15] < rsi15[i15 - 1];

            bool momentumOk =
                (macdCrossDown && rsiBear) ||
                (rsiBear && macd15[i15] < 0) ||
                (extremeDump && rsiBear);

            bool shouldShort = retestEma && bearishReject && momentumOk;

            if (!shouldShort)
            {
                return new TradeSignal
                {
                    Type = SignalType.Info,
                    Reason = $"{symbol.Coin} : downtrend nhưng không đủ setup short (retestEma={retestEma}, bearishReject={bearishReject}, momentumOk={momentumOk}, extremeDump={extremeDump})."
                };
            }

            // (5) Tính Entry/SL/TP
            // Entry: cao hơn Close một chút để tránh vào đáy nến setup
            decimal rawEntry = last15.Close * (1 + EntryOffsetPercent);

            decimal swingHigh = PriceActionHelper.FindSwingHigh(candles15m, i15, lookback: SwingLookback);
            decimal buffer = rawEntry * StopBufferPercent;
            decimal sl = swingHigh + buffer;

            // Nếu offset làm entry nằm trên swingHigh quá xa -> fallback về giữa Close & swingHigh
            decimal entry = rawEntry;
            if (entry >= swingHigh)
                entry = (last15.Close + swingHigh) / 2;

            // Recalc SL theo entry mới
            buffer = entry * StopBufferPercent;
            sl = swingHigh + buffer;

            if (sl <= entry)
            {
                return new TradeSignal
                {
                    Type = SignalType.Info,
                    Reason = $"{symbol.Coin} : downtrend nhưng SL invalid (sl={sl}, entry={entry})."
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
                Reason = $"{symbol.Coin} : SHORT downtrend – breakout trước đó, retest EMA + bearish rejection + momentum OK (entry offset trên Close)."
            };
        }
    }
}
