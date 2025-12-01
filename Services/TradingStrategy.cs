using FuturesBot.Config;
using FuturesBot.IServices;
using FuturesBot.Models;
using FuturesBot.Utils;
using static FuturesBot.Utils.EnumTypesHelper;

namespace FuturesBot.Services
{
    public class TradingStrategy(IndicatorService indicators) : IStrategyService
    {
        private readonly IndicatorService _indicators = indicators;

        private const int MinBars = 120;
        private const int SwingLookback = 5;
        private const int PullbackVolumeLookback = 5;

        private const decimal EmaRetestBand = 0.002m;
        private const decimal StopBufferPercent = 0.005m;
        private const decimal RiskReward = 1.5m;
        private const decimal RiskRewardSideway = 1m;

        private const decimal RsiBullThreshold = 55m;
        private const decimal RsiBearThreshold = 45m;
        private const decimal ExtremeRsiHigh = 75m;
        private const decimal ExtremeRsiLow = 30m;
        private const decimal ExtremeEmaBoost = 0.01m;

        private const decimal EntryOffsetPercent = 0.003m;
        private const decimal Ema89StopExtraPercent = 0.003m;

        private const int ClimaxLookback = 20;
        private const decimal ClimaxBodyMultiplier = 1.8m;
        private const decimal ClimaxVolumeMultiplier = 1.5m;
        private const decimal OverextendedFromEmaPercent = 0.01m;

        // ================= EXIT ==================
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

            if (hasLongPosition && last15.Close < ema34Now)
            {
                return new TradeSignal
                {
                    Type = SignalType.CloseLong,
                    Reason = $"{symbol.Coin}: Đóng dưới EMA34 → đóng LONG",
                    Coin = symbol.Coin
                };
            }

            if (hasShortPosition && last15.Close > ema34Now)
            {
                return new TradeSignal
                {
                    Type = SignalType.CloseShort,
                    Reason = $"{symbol.Coin}: Đóng trên EMA34 → đóng SHORT",
                    Coin = symbol.Coin
                };
            }

            return new TradeSignal();
        }

        // ================= ENTRY ==================
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

            var ema34_15 = _indicators.Ema(candles15m, 34);
            var ema89_15 = _indicators.Ema(candles15m, 89);
            var ema200_15 = _indicators.Ema(candles15m, 200);

            var ema34_h1 = _indicators.Ema(candles1h, 34);
            var ema89_h1 = _indicators.Ema(candles1h, 89);

            var rsi15 = _indicators.Rsi(candles15m, 6);
            var (macd15, sig15, _) =
                _indicators.Macd(candles15m, 5, 13, 5);

            decimal ema34_15Now = ema34_15[i15];
            decimal ema89_15Now = ema89_15[i15];
            decimal ema200_15Now = ema200_15[i15];

            // ==== Trend H1 (chỉ dựa EMA, không dựa CLOSE) ====
            bool h1BiasUp = ema34_h1[iH1] > ema89_h1[iH1];
            bool h1BiasDown = ema34_h1[iH1] < ema89_h1[iH1];

            // ==== Trend chuẩn H1 + M15 (chỉ dùng cho entry mạnh) ====
            bool upTrend =
                h1BiasUp &&
                last15.Close > ema34_15Now &&
                ema34_15Now > ema89_15Now;

            bool downTrend =
                h1BiasDown &&
                last15.Close < ema34_15Now &&
                ema34_15Now < ema89_15Now;

            // ==== Extreme filter ====
            bool extremeUp =
                last15.Close > ema34_15Now * (1 + ExtremeEmaBoost) &&
                macd15[i15] > sig15[i15] &&
                rsi15[i15] > ExtremeRsiHigh;

            bool extremeDump =
                last15.Close < ema34_15Now * (1 - ExtremeEmaBoost) &&
                macd15[i15] < sig15[i15] &&
                rsi15[i15] < ExtremeRsiLow;

            // ==== Climax filter ====
            bool climax =
                IsClimaxAwayFromEma(candles15m, i15, ema34_15Now, ema89_15Now, ema200_15Now) ||
                IsClimaxAwayFromEma(candles15m, i15 - 1, ema34_15Now, ema89_15Now, ema200_15Now);

            if (climax)
                return new TradeSignal();

            // ==== Không align trend → sideway scalp ====
            if (!upTrend && !downTrend && !extremeUp && !extremeDump)
            {
                var sideway = BuildSidewayScalp(
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
                    h1BiasDown);

                if (sideway.Type != SignalType.None)
                    return sideway;

                return new TradeSignal();
            }

            // ==== Volume check ====
            decimal avgVol = PriceActionHelper.AverageVolume(
                candles15m, i15 - 1, PullbackVolumeLookback);

            bool strongVolume = avgVol > 0 &&
                                last15.Volume >= avgVol;

            //if (!strongVolume && !extremeUp && !extremeDump)
            //    return new TradeSignal();
            // ==== BUILD LONG ====
            if (upTrend)
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
                    symbol);

                if (longSignal.Type != SignalType.None)
                    return longSignal;
            }

            // ==== BUILD SHORT ====
            if (downTrend)
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
                    symbol);

                if (shortSignal.Type != SignalType.None)
                    return shortSignal;
            }

            return new TradeSignal();
        }

        // ================= LONG ==================
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
            Symbol symbol)
        {
            int i15 = candles15m.Count - 1;

            decimal ema34 = ema34_15[i15];
            decimal ema89 = ema89_15[i15];
            decimal ema200 = ema200_15[i15];

            var supports = new List<decimal>();

            if (ema34 < last15.Close) supports.Add(ema34);
            if (ema89 < last15.Close) supports.Add(ema89);
            if (ema200 < last15.Close) supports.Add(ema200);

            if (supports.Count == 0)
                return new TradeSignal();

            decimal nearest = supports.Max();

            bool touch =
                last15.Low <= nearest * (1 + EmaRetestBand) &&
                last15.Low >= nearest * (1 - EmaRetestBand);

            bool reject =
                last15.Close > last15.Open &&
                last15.Low < nearest &&
                last15.Close > nearest;

            bool macdUp =
                macd15[i15] > sig15[i15] &&
                macd15[i15 - 1] <= sig15[i15 - 1];

            bool rsiUp =
                rsi15[i15] > RsiBullThreshold &&
                rsi15[i15] >= rsi15[i15 - 1];

            bool ok = touch && reject && (macdUp || rsiUp);

            if (!ok)
                return new TradeSignal();

            decimal entry = last15.Close * (1 - EntryOffsetPercent);
            decimal swingLow = PriceActionHelper.FindSwingLow(candles15m, i15, SwingLookback);

            decimal sl = Math.Min(
                swingLow - entry * StopBufferPercent,
                ema89 * (1 - Ema89StopExtraPercent));

            if (sl >= entry)
                return new TradeSignal();

            decimal tp = entry + (entry - sl) * RiskReward;

            return new TradeSignal
            {
                Type = SignalType.Long,
                EntryPrice = entry,
                StopLoss = sl,
                TakeProfit = tp,
                Reason = $"{symbol.Coin}: LONG TREND"
            };
        }

        // ================= SHORT ==================
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
            Symbol symbol)
        {
            int i15 = candles15m.Count - 1;

            decimal ema34 = ema34_15[i15];
            decimal ema89 = ema89_15[i15];
            decimal ema200 = ema200_15[i15];

            var resistances = new List<decimal>();

            if (ema34 > last15.Close) resistances.Add(ema34);
            if (ema89 > last15.Close) resistances.Add(ema89);
            if (ema200 > last15.Close) resistances.Add(ema200);

            if (resistances.Count == 0)
                return new TradeSignal();

            decimal nearest = resistances.Min();

            bool touch =
                last15.High >= nearest * (1 - EmaRetestBand) &&
                last15.High <= nearest * (1 + EmaRetestBand);

            bool reject =
                last15.Close < last15.Open &&
                last15.High > nearest &&
                last15.Close < nearest;

            bool macdDown =
                macd15[i15] < sig15[i15] &&
                macd15[i15 - 1] >= sig15[i15 - 1];

            bool rsiDown =
                rsi15[i15] < RsiBearThreshold &&
                rsi15[i15] <= rsi15[i15 - 1];

            bool ok = touch && reject && (macdDown || rsiDown);

            if (!ok) return new TradeSignal();

            decimal entry = last15.Close * (1 + EntryOffsetPercent);

            decimal swingHigh = PriceActionHelper.FindSwingHigh(candles15m, i15, SwingLookback);

            decimal sl = Math.Max(
                swingHigh + entry * StopBufferPercent,
                ema89 * (1 + Ema89StopExtraPercent));

            if (sl <= entry)
                return new TradeSignal();

            decimal tp = entry - (sl - entry) * RiskReward;

            return new TradeSignal
            {
                Type = SignalType.Short,
                EntryPrice = entry,
                StopLoss = sl,
                TakeProfit = tp,
                Reason = $"{symbol.Coin}: SHORT TREND"
            };
        }
        // ================= SIDEWAY SCALP ==================
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
            bool h1BiasDown)
        {
            int i15 = candles15m.Count - 1;

            decimal ema34 = ema34_15[i15];
            decimal ema89 = ema89_15[i15];
            decimal ema200 = ema200_15[i15];

            // ===== BIAS 15M chỉ dựa vào thứ tự EMA =====
            bool shortBias15 =
                ema34 <= ema89 &&
                ema34 <= ema200;

            bool longBias15 =
                ema34 >= ema89 &&
                ema34 >= ema200;

            // ===== ƯU TIÊN BIAS THEO H1 =====
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
                    Reason = $"{symbol.Coin}: SIDEWAY – không có bias rõ (ema34={ema34:F2}, ema89={ema89:F2}, ema200={ema200:F2}).",
                    Coin = symbol.Coin
                };
            }

            // =========== SCALP SHORT =============
            if (shortBias)
            {
                var res = new List<decimal>();
                if (ema89 > last15.Close) res.Add(ema89);
                if (ema200 > last15.Close) res.Add(ema200);

                if (res.Count == 0) return new TradeSignal();

                decimal nearest = res.Min();

                bool touch =
                    last15.High >= nearest * (1 - EmaRetestBand) &&
                    last15.High <= nearest * (1 + EmaRetestBand);

                bool reject =
                    last15.Close < last15.Open &&
                    last15.High > nearest &&
                    last15.Close < nearest;

                bool rsiDown =
                    rsi15[i15 - 1] >= 50 &&
                    rsi15[i15] <= rsi15[i15 - 1];

                bool macdDown =
                    macd15[i15] <= macd15[i15 - 1];

                bool momentum = rsiDown && macdDown;

                if (!(touch && reject && momentum))
                    return new TradeSignal();

                decimal entry = last15.Close * (1 + EntryOffsetPercent);
                decimal swingHigh = PriceActionHelper.FindSwingHigh(candles15m, i15, SwingLookback);
                decimal sl = swingHigh + entry * StopBufferPercent;

                if (sl <= entry) return new TradeSignal();

                decimal tp = entry - (sl - entry) * RiskRewardSideway;

                return new TradeSignal
                {
                    Type = SignalType.Short,
                    EntryPrice = entry,
                    StopLoss = sl,
                    TakeProfit = tp,
                    Reason = $"{symbol.Coin}: SIDEWAY SCALP SHORT"
                };
            }

            // =========== SCALP LONG =============
            {
                var sup = new List<decimal>();
                if (ema89 < last15.Close) sup.Add(ema89);
                if (ema200 < last15.Close) sup.Add(ema200);

                if (sup.Count == 0) return new TradeSignal();

                decimal nearest = sup.Max();

                bool touch =
                    last15.Low <= nearest * (1 + EmaRetestBand) &&
                    last15.Low >= nearest * (1 - EmaRetestBand);

                bool reject =
                    last15.Close > last15.Open &&
                    last15.Low < nearest &&
                    last15.Close > nearest;

                bool rsiUp =
                    rsi15[i15 - 1] <= 50 &&
                    rsi15[i15] >= rsi15[i15 - 1];

                bool macdUp =
                    macd15[i15] >= macd15[i15 - 1];

                bool momentum = rsiUp && macdUp;

                if (!(touch && reject && momentum))
                    return new TradeSignal();

                decimal entry = last15.Close * (1 - EntryOffsetPercent);
                decimal swingLow = PriceActionHelper.FindSwingLow(candles15m, i15, SwingLookback);
                decimal sl = swingLow - entry * StopBufferPercent;

                if (sl >= entry) return new TradeSignal();

                decimal tp = entry + (entry - sl) * RiskRewardSideway;

                return new TradeSignal
                {
                    Type = SignalType.Long,
                    EntryPrice = entry,
                    StopLoss = sl,
                    TakeProfit = tp,
                    Reason = $"{symbol.Coin}: SIDEWAY SCALP LONG"
                };
            }
        }
        private bool IsClimaxCandle(IReadOnlyList<Candle> candles, int index)
        {
            if (index <= 0 || index >= candles.Count) return false;

            int start = Math.Max(0, index - ClimaxLookback);
            int end = index;
            int count = end - start;
            if (count <= 3) return false;

            decimal sumBody = 0;
            decimal sumVol = 0;

            for (int i = start; i < end; i++)
            {
                sumBody += Math.Abs(candles[i].Close - candles[i].Open);
                sumVol += candles[i].Volume;
            }

            decimal avgBody = sumBody / count;
            decimal avgVol = sumVol / count;

            var last = candles[index];
            decimal body = Math.Abs(last.Close - last.Open);
            decimal vol = last.Volume;

            bool bigBody = body >= avgBody * ClimaxBodyMultiplier;
            bool bigVol = vol >= avgVol * ClimaxVolumeMultiplier;

            return bigBody && bigVol;
        }

        private decimal GetNearestEma(decimal price, decimal e34, decimal e89, decimal e200)
        {
            var list = new List<decimal>();
            if (e34 > 0) list.Add(e34);
            if (e89 > 0) list.Add(e89);
            if (e200 > 0) list.Add(e200);

            decimal nearest = list[0];
            decimal dist = Math.Abs(price - nearest);

            foreach (var x in list)
            {
                decimal d = Math.Abs(price - x);
                if (d < dist)
                {
                    nearest = x;
                    dist = d;
                }
            }

            return nearest;
        }

        private bool IsClimaxAwayFromEma(
            IReadOnlyList<Candle> candles,
            int index,
            decimal e34,
            decimal e89,
            decimal e200)
        {
            if (index <= 0 || index >= candles.Count) return false;
            if (!IsClimaxCandle(candles, index)) return false;

            decimal nearest = GetNearestEma(candles[index].Close, e34, e89, e200);
            decimal dist = Math.Abs(candles[index].Close - nearest) / nearest;

            return dist >= OverextendedFromEmaPercent;
        }
    }
}
