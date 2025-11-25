using FuturesBot.IServices;
using FuturesBot.Models;
using FuturesBot.Utils;
using static FuturesBot.Utils.EnumTypesHelper;

namespace FuturesBot.Services
{
    public class FifteenMinutesStrategy(IndicatorService indicators) : IStrategyService
    {
        private readonly IndicatorService _indicators = indicators;

        public TradeSignal GenerateSignal(
        IReadOnlyList<Candle> candles15m,
        IReadOnlyList<Candle> candles1h)
        {
            if (candles15m.Count < 120 || candles1h.Count < 120)
                return new TradeSignal();

            int i15 = candles15m.Count - 1;
            int iH1 = candles1h.Count - 1;

            var ema34_15 = _indicators.Ema(candles15m, 34);
            var ema89_15 = _indicators.Ema(candles15m, 89);
            var ema200_15 = _indicators.Ema(candles15m, 200);

            var ema34_h1 = _indicators.Ema(candles1h, 34);

            var rsi15 = _indicators.Rsi(candles15m, 6);
            var (macd15, sig15, _) = _indicators.Macd(candles15m, 5, 13, 5);

            var last15 = candles15m[i15];
            var prev15 = candles15m[i15 - 1];
            var lastH1 = candles1h[iH1];

            // ===== 1. Trend filter H1 + M15 =====
            bool upTrend =
                lastH1.Close > ema34_h1[iH1] &&
                last15.Close > ema34_15[i15];
                //&& ema34_15[i15] > ema89_15[i15];

            bool downTrend =
                lastH1.Close < ema34_h1[iH1] &&
                last15.Close < ema34_15[i15];
                //&& ema34_15[i15] < ema89_15[i15];

           bool extremeDump =
    last15.Close < ema34_15[i15] * 0.995m &&   // gãy xa dưới EMA34
    macd15[i15] < sig15[i15] &&
    rsi15[i15] < 30; 

            if (!upTrend && !downTrend && !extremeDump)
                return new TradeSignal(); // sideway -> bỏ

            // ===== 2. Volume pattern (nến setup phải vol mạnh hơn pullback) =====
            decimal avgPullbackVol = PriceActionHelper.AverageVolume(candles15m, i15 - 1, 3);
            decimal currentVol = last15.Volume;

            bool strongVolume = avgPullbackVol > 0 && currentVol >= avgPullbackVol * 0.7m;
            if (!strongVolume && !extremeDump)
                return new TradeSignal();

            // ===== 3. LONG SETUP (sau breakout, bắt buộc có retest) =====
            if (upTrend)
            {
                // (3.1) Xác nhận đã có breakout trước đó:
                // Ít nhất 1–2 nến trước đã đóng trên EMA34 (không phải vừa mới cross)
                bool wasAboveEma34Recently = candles15m[i15 - 1].Close >= ema34_15[i15 - 1] * 1.002m; //&& candles15m[i15 - 1].Close > ema34_15[i15 - 1];

                if (!wasAboveEma34Recently)
                    goto ShortPart; // tránh long ngay cây breakout đầu tiên

                // (3.2) Retest EMA34/EMA89: nến hiện tại phải "chạm xuống EMA rồi bật lên"
                bool retestEma = last15.Low <= ema34_15[i15] * 1.003m || last15.Low <= ema89_15[i15] * 1.003m;

                bool bullishReject =
                    last15.Close > last15.Open &&          // nến xanh
                    last15.Close > ema34_15[i15];          // đóng lại phía trên EMA34

                // (3.3) Momentum: MACD + RSI
                bool macdCrossUp = macd15[i15] > sig15[i15]; //&& macd15[i15 - 1] <= sig15[i15 - 1];

                bool rsiBull = rsi15[i15] > 55 && rsi15[i15] > rsi15[i15 - 1];

                if (retestEma && bullishReject && macdCrossUp && rsiBull)
                {
                    decimal entry = last15.Close;

                    // SL anti-hunt: đáy swing - buffer
                    decimal swingLow = PriceActionHelper.FindSwingLow(candles15m, i15, lookback: 5);
                    decimal buffer = entry * 0.0005m; // 0.05%
                    decimal sl = Math.Round(swingLow - buffer, 3);

                    if (sl <= 0 || sl >= entry)
                        return new TradeSignal();

                    decimal risk = entry - sl;
                    decimal tp = Math.Round(entry + risk * 1.5m, 3);   // TP = 1.5R

                    return new TradeSignal
                    {
                        Type = SignalType.Long,
                        EntryPrice = entry,
                        StopLoss = sl,
                        TakeProfit = tp,
                        Reason = "H1 uptrend, breakout xong retest EMA + strong vol + MACD up + RSI>50"
                    };
                }
            }

        ShortPart: // label nhảy xuống khi không đủ điều kiện long

            // ===== 4. SHORT SETUP (sau breakout, bắt buộc có retest) =====
            if (downTrend || extremeDump)
            {
                // (4.1) Xác nhận đã có breakout trước đó:
                // Ít nhất 1–2 nến trước đã đóng dưới EMA34
                bool wasBelowEma34Recently = candles15m[i15 - 1].Close <= ema34_15[i15 - 1] * 1.0002m; //&& candles15m[i15 - 1].Close < ema34_15[i15 - 1];

                if (!wasBelowEma34Recently && !extremeDump)
                    return new TradeSignal(); // tránh short ngay cây breakout đầu tiên

                // (4.2) Retest EMA34/EMA89: nến hiện tại phải "chạm lên EMA rồi bị đạp xuống"
                bool retestEma = last15.High >= ema34_15[i15] * 0.997m || last15.High >= ema89_15[i15] * 0.997m;

                bool bearishReject =
                    last15.Close < last15.Open &&         // nến đỏ
                    last15.Close < ema34_15[i15];         // đóng lại dưới EMA34

                // (4.3) Momentum: MACD + RSI
                bool macdCrossDown = macd15[i15] < sig15[i15]; //&& macd15[i15 - 1] >= sig15[i15 - 1];

                bool rsiBear = rsi15[i15] < 45 && rsi15[i15] < rsi15[i15 - 1];

                if ((retestEma && bearishReject && macdCrossDown && rsiBear) || extremeDump)
                {
                    decimal entry = last15.Close;

                    // SL anti-hunt: đỉnh swing + buffer
                    decimal swingHigh = PriceActionHelper.FindSwingHigh(candles15m, i15, lookback: 5);
                    decimal buffer = entry * 0.0005m; // 0.05%
                    decimal sl = Math.Round(swingHigh + buffer, 3);

                    if (sl <= entry)
                        return new TradeSignal();

                    decimal risk = sl - entry;
                    decimal tp = Math.Round(entry - risk * 1.5m, 3); // TP = 1.5R

                    return new TradeSignal
                    {
                        Type = SignalType.Short,
                        EntryPrice = entry,
                        StopLoss = sl,
                        TakeProfit = tp,
                        Reason = "H1 downtrend, breakout xong retest EMA + strong vol + MACD down + RSI<50"
                    };
                }
            }

            return new TradeSignal();
        }
    }
}
