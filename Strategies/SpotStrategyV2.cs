using System;
using System.Collections.Generic;
using System.Linq;
using FuturesBot.Config;
using FuturesBot.IServices;
using FuturesBot.Models;
using FuturesBot.Services;
using static FuturesBot.Utils.EnumTypesHelper;

namespace FuturesBot.Strategies
{
    /// <summary>
    /// SpotStrategy V2 (Dynamic TF) - long-only.
    ///
    /// Goals:
    /// - Vô kèo ổn định across TF: 1m/3m/5m/15m...
    /// - 3 entry types:
    ///   A) EMA Retest (winrate)
    ///   B) Shallow Pullback / Continuation (nhiều kèo)
    ///   C) Break & Hold (breakout có hold giảm trap)
    ///
    /// Notes:
    /// - Use last CLOSED candle (Count-2).
    /// - candlesTrend is required (Trend TF).
    /// - Strategy returns SignalType.Long for entry, SignalType.Short for exit (OMS interpret as SELL/exit).
    /// </summary>
    public sealed class SpotStrategyV2 : ISpotTradingStrategy
    {
        private readonly IndicatorService _indicators;

        public SpotStrategyV2(IndicatorService indicators)
        {
            _indicators = indicators;
        }

        // =========================
        // Fixed indicator periods
        // =========================
        private const int EmaFast = 34;
        private const int EmaSlow = 89;
        private const int EmaLong = 200;
        private const int RsiPeriod = 14;

        private const int AtrPeriod = 14;
        private const int VolMaPeriod = 20;

        // =========================
        // Exit baselines (slightly dynamic later)
        // =========================
        private const decimal ExitRsiWeakBase = 44m;
        private const decimal ExitEmaBreakTolBase = 0.0004m; // 0.04%

        public TradeSignal GenerateSignal(IReadOnlyList<Candle> candlesMain, IReadOnlyList<Candle> candlesTrend, CoinInfo coinInfo)
        {
            var symbol = coinInfo.Symbol;

            int tfMin = ParseIntervalMinutesSafe(coinInfo.MainTimeFrame);
            var p = ParamSet.For(tfMin);

            if (candlesMain == null || candlesMain.Count < p.MinBarsEntry)
                return new TradeSignal { Type = SignalType.None, Symbol = symbol, Reason = $"SpotV2: not enough entry bars (need={p.MinBarsEntry}, tf={tfMin}m)" };

            if (candlesTrend == null || candlesTrend.Count < p.MinBarsTrend)
                return new TradeSignal { Type = SignalType.None, Symbol = symbol, Reason = $"SpotV2: not enough trend bars (need={p.MinBarsTrend})" };

            int iE = candlesMain.Count - 2;   // last closed entry candle
            int iE1 = iE - 1;
            if (iE1 < 2)
                return new TradeSignal { Type = SignalType.None, Symbol = symbol, Reason = "SpotV2: not enough closed entry bars" };

            int iT = candlesTrend.Count - 2;  // last closed trend candle
            int iT1 = iT - 1;
            if (iT1 < 2)
                return new TradeSignal { Type = SignalType.None, Symbol = symbol, Reason = "SpotV2: not enough closed trend bars" };

            // ===== Indicators (Entry TF) =====
            var ema34E = _indicators.Ema(candlesMain, EmaFast);
            var ema89E = _indicators.Ema(candlesMain, EmaSlow);
            var ema200E = _indicators.Ema(candlesMain, EmaLong);
            var rsiE = _indicators.Rsi(candlesMain, RsiPeriod);

            // ===== Indicators (Trend TF) =====
            var ema34T = _indicators.Ema(candlesTrend, EmaFast);
            var ema89T = _indicators.Ema(candlesTrend, EmaSlow);

            var cE = candlesMain[iE];
            var cEPrev = candlesMain[iE1];
            var cT = candlesTrend[iT];

            var close = cE.Close;
            var open = cE.Open;
            var high = cE.High;
            var low = cE.Low;

            var e34 = ema34E[iE];
            var e89 = ema89E[iE];
            var e200 = ema200E[iE];
            var rsi = rsiE[iE];

            var t34 = ema34T[iT];
            var t89 = ema89T[iT];
            var t34Prev = ema34T[iT1];

            // ===== helpers =====
            decimal atr = ComputeAtr(candlesMain, AtrPeriod);
            decimal volMa = ComputeVolUsdMa(candlesMain, VolMaPeriod);
            decimal lastVolUsd = cE.Volume * close;
            decimal lastBodyToRange = GetBodyToRange(cE);

            // =========================
            // 0) Common Gates (dynamic)
            // =========================

            // G1: Trend alignment + slope
            bool trendUp = (t34 > t89) && (t34 >= t34Prev);
            bool entryBiasOk = e34 >= e89; // light bias
            bool trendOk = trendUp && entryBiasOk;

            if (!trendOk)
            {
                // Exit condition when trend breaks strongly
                if (ShouldExitOnTrendBreak(
                        cE, cEPrev,
                        e34, e89,
                        rsi,
                        t34, t89,
                        exitRsiWeak: p.ExitRsiWeak,
                        exitEmaTol: p.ExitEmaBreakTol))
                {
                    return new TradeSignal
                    {
                        Symbol = symbol,
                        Time = DateTime.UtcNow,
                        Type = SignalType.Short,
                        Reason = $"SpotV2: EXIT trendBreak | tf={tfMin}m rsi={rsi:F1} e34={e34:0.##} e89={e89:0.##} t34={t34:0.##} t89={t89:0.##}"
                    };
                }

                return new TradeSignal { Type = SignalType.None, Symbol = symbol, Reason = $"SpotV2: trend gate (tf={tfMin}m)" };
            }

            // G2: anti-chase (hybrid: pct + ATR)
            if (e34 > 0m)
            {
                var distPct = Math.Abs(close - e34) / e34;
                if (distPct > p.MaxDistanceFromEma34Pct)
                    return new TradeSignal { Type = SignalType.None, Symbol = symbol, Reason = $"SpotV2: too far from EMA34 (dist={distPct:P2} > {p.MaxDistanceFromEma34Pct:P2}) tf={tfMin}m" };
            }

            // Extra chase filter using ATR (helps 1m)
            if (atr > 0m && e34 > 0m)
            {
                var distAbs = Math.Abs(close - e34);
                if (distAbs >= atr * p.MaxDistanceFromEma34AtrMult)
                    return new TradeSignal { Type = SignalType.None, Symbol = symbol, Reason = $"SpotV2: too far from EMA34 by ATR (dist={distAbs:0.####} >= {p.MaxDistanceFromEma34AtrMult:0.##}*ATR) tf={tfMin}m" };
            }

            // Impulse chase filter (dynamic)
            if (atr > 0m)
            {
                var range = (high - low);
                if (range > 0m && lastBodyToRange >= p.ImpulseBodyToRangeMax && range >= atr * p.ImpulseRangeAtrMult)
                    return new TradeSignal { Type = SignalType.None, Symbol = symbol, Reason = $"SpotV2: impulse chase filter (tf={tfMin}m)" };
            }

            // G3: liquidity/volume (dynamic soften for lower TF)
            if (coinInfo.MinVolumeUsdTrend > 0m)
            {
                var trendVolMa = ComputeVolUsdMa(candlesTrend, VolMaPeriod);
                if (trendVolMa > 0m && trendVolMa < coinInfo.MinVolumeUsdTrend)
                    return new TradeSignal { Type = SignalType.None, Symbol = symbol, Reason = $"SpotV2: low trend volume (tf={tfMin}m)" };
            }

            if (volMa > 0m && lastVolUsd < volMa * p.EntryVolMinFactor)
                return new TradeSignal { Type = SignalType.None, Symbol = symbol, Reason = $"SpotV2: low entry volume (v={lastVolUsd:0} < {p.EntryVolMinFactor:0.##}*vMA) tf={tfMin}m" };

            // =========================
            // Entry Priority: A > B > C
            // =========================

            // Type A: EMA retest
            if (IsTypeA_Retest(candlesMain, ema34E, iE, rsi, e34, close, open, p))
            {
                return new TradeSignal
                {
                    Symbol = symbol,
                    Time = DateTime.UtcNow,
                    Type = SignalType.Long,
                    EntryPrice = close,
                    Reason = $"SpotV2[A] EMA Retest+Reclaim | tf={tfMin}m rsi={rsi:F1} volUsd={lastVolUsd:0} volMA={volMa:0}"
                };
            }

            // Type B: Continuation base break
            if (IsTypeB_Continuation(candlesMain, ema34E, iE, atr, rsi, volMa, p))
            {
                return new TradeSignal
                {
                    Symbol = symbol,
                    Time = DateTime.UtcNow,
                    Type = SignalType.Long,
                    EntryPrice = close,
                    Reason = $"SpotV2[B] BaseBreak Continuation | tf={tfMin}m rsi={rsi:F1} atr={atr:F6} volUsd={lastVolUsd:0} volMA={volMa:0}"
                };
            }

            // Type C: Break & Hold
            if (IsTypeC_BreakHold(candlesMain, ema34E, iE, atr, rsi, volMa, p))
            {
                return new TradeSignal
                {
                    Symbol = symbol,
                    Time = DateTime.UtcNow,
                    Type = SignalType.Long,
                    EntryPrice = close,
                    Reason = $"SpotV2[C] Break&Hold | tf={tfMin}m rsi={rsi:F1} atr={atr:F6} volUsd={lastVolUsd:0} volMA={volMa:0}"
                };
            }

            // Exit soft condition (when in position OMS decides; strategy still emits Short)
            if (ShouldExitSoft(cE, cEPrev, e34, e89, rsi, p.ExitRsiWeak, p.ExitEmaBreakTol))
            {
                return new TradeSignal
                {
                    Symbol = symbol,
                    Time = DateTime.UtcNow,
                    Type = SignalType.Short,
                    Reason = $"SpotV2: EXIT soft | tf={tfMin}m rsi={rsi:F1} close<EMA34/89"
                };
            }

            return new TradeSignal { Type = SignalType.None, Symbol = symbol, Reason = $"SpotV2: no-signal (tf={tfMin}m)" };
        }

        // =========================
        // Entry type implementations (dynamic params)
        // =========================

        private static bool IsTypeA_Retest(
            IReadOnlyList<Candle> e,
            decimal[] ema34,
            int i,
            decimal rsi,
            decimal e34,
            decimal close,
            decimal open,
            ParamSet p)
        {
            if (rsi < p.RsiMinTypeA) return false;
            if (e34 <= 0m) return false;

            // retest in last N bars: low touches EMA34*(1+band)
            int start = Math.Max(1, i - p.RetestLookbackBars);
            bool hadTouch = false;

            for (int k = i; k >= start; k--)
            {
                var ek = ema34[k];
                if (ek <= 0m) continue;

                // Allow touch noise band
                if (e[k].Low <= ek * (1m + p.RetestTouchBand))
                {
                    hadTouch = true;
                    break;
                }
            }
            if (!hadTouch) return false;

            bool reclaim = close >= e34 * (1m + p.RetestReclaimBuf);
            if (!reclaim) return false;

            // confirm: bullish or higher close
            bool bullish = close > open;
            bool confirm = bullish || close > e[i - 1].Close;

            return confirm;
        }

        private static bool IsTypeB_Continuation(
            IReadOnlyList<Candle> e,
            decimal[] ema34,
            int i,
            decimal atr,
            decimal rsi,
            decimal volMaUsd,
            ParamSet p)
        {
            if (rsi < p.RsiMinTypeB) return false;
            if (atr <= 0m) return false;

            int end = i; // last closed
            int start = Math.Max(1, end - p.BaseLookbackBars + 1);
            if (end - start + 1 < p.BaseMinBarsInBox) return false;

            decimal maxHigh = decimal.MinValue;
            decimal minLow = decimal.MaxValue;

            for (int k = start; k <= end; k++)
            {
                maxHigh = Math.Max(maxHigh, e[k].High);
                minLow = Math.Min(minLow, e[k].Low);
            }

            decimal boxRange = maxHigh - minLow;
            if (boxRange <= 0m) return false;
            if (boxRange > atr * p.BaseMaxRangeAtr) return false;

            // base should be above EMA34 (allow tiny pierce)
            decimal e34 = ema34[i];
            if (e34 <= 0m) return false;
            if (minLow < e34 * (1m - p.BaseMinLowAboveEma34)) return false;

            // trigger: close breaks above box (optionally require small buffer)
            var close = e[i].Close;
            if (close <= maxHigh * (1m + p.BaseBreakBuffer)) return false;

            // volume confirm
            var lastVolUsd = e[i].Volume * close;
            if (volMaUsd > 0m && lastVolUsd < volMaUsd * p.BreakVolMinFactor) return false;

            return true;
        }

        private static bool IsTypeC_BreakHold(
            IReadOnlyList<Candle> e,
            decimal[] ema34,
            int i,
            decimal atr,
            decimal rsi,
            decimal volMaUsd,
            ParamSet p)
        {
            if (rsi < p.RsiMinTypeC) return false;
            if (atr <= 0m) return false;

            int endSwing = Math.Max(1, i - 1);
            int startSwing = Math.Max(1, endSwing - p.SwingHighLookbackBars);

            decimal swingHigh = decimal.MinValue;
            for (int k = startSwing; k <= endSwing; k++)
                swingHigh = Math.Max(swingHigh, e[k].High);

            if (swingHigh <= 0m || swingHigh == decimal.MinValue) return false;

            var close = e[i].Close;
            if (close <= swingHigh * (1m + p.BreakBuffer)) return false;

            // hold confirm: within last 1..N closed bars, none closes below swingHigh*(1-holdBuf)
            int startHold = Math.Max(1, i - p.HoldConfirmBarsMax + 1);
            for (int k = startHold; k <= i; k++)
            {
                if (e[k].Close < swingHigh * (1m - p.HoldBelowBuffer))
                    return false;
            }

            // still above EMA34 (soft)
            var e34 = ema34[i];
            if (e34 > 0m && close < e34 * (1m - p.HoldEma34Tol))
                return false;

            // volume confirm
            var lastVolUsd = e[i].Volume * close;
            if (volMaUsd > 0m && lastVolUsd < volMaUsd * p.BreakVolMinFactor) return false;

            return true;
        }

        // =========================
        // Exit helpers
        // =========================

        private static bool ShouldExitSoft(Candle c0, Candle c1, decimal ema34, decimal ema89, decimal rsi, decimal exitRsiWeak, decimal exitEmaTol)
        {
            if (ema34 <= 0m || ema89 <= 0m) return false;

            bool closeBelow34Hard = c0.Close < ema34 * (1m - exitEmaTol);
            bool closeBelow89Hard = c0.Close < ema89 * (1m - exitEmaTol);
            bool twoClosesBelow34 = (c0.Close < ema34) && (c1.Close < ema34);

            if ((closeBelow34Hard && rsi <= exitRsiWeak) || closeBelow89Hard || twoClosesBelow34)
                return true;

            return false;
        }

        private static bool ShouldExitOnTrendBreak(Candle cE, Candle cEPrev, decimal e34, decimal e89, decimal rsi, decimal t34, decimal t89, decimal exitRsiWeak, decimal exitEmaTol)
        {
            bool trendBroken = (t34 > 0m && t89 > 0m && t34 < t89);
            if (!trendBroken) return false;

            // require some weakness on entry TF too
            if (ShouldExitSoft(cE, cEPrev, e34, e89, rsi, exitRsiWeak, exitEmaTol))
                return true;

            return false;
        }

        // =========================
        // Math helpers
        // =========================

        private static decimal ComputeAtr(IReadOnlyList<Candle> candles, int period)
        {
            if (candles == null || candles.Count < period + 2) return 0m;

            int end = candles.Count - 2; // last closed
            int start = Math.Max(1, end - period + 1);

            decimal sum = 0m;
            int n = 0;

            for (int i = start; i <= end; i++)
            {
                var c = candles[i];
                var prev = candles[i - 1];

                var tr = Math.Max(
                    c.High - c.Low,
                    Math.Max(Math.Abs(c.High - prev.Close), Math.Abs(c.Low - prev.Close))
                );

                sum += tr;
                n++;
            }

            return n > 0 ? sum / n : 0m;
        }

        private static decimal ComputeVolUsdMa(IReadOnlyList<Candle> candles, int period)
        {
            if (candles == null || candles.Count < period + 2) return 0m;

            int end = candles.Count - 2;
            int start = Math.Max(0, end - period + 1);

            decimal sum = 0m;
            int n = 0;

            for (int i = start; i <= end; i++)
            {
                sum += candles[i].Volume * candles[i].Close;
                n++;
            }

            return n > 0 ? sum / n : 0m;
        }

        private static decimal GetBodyToRange(Candle c)
        {
            decimal range = c.High - c.Low;
            if (range <= 0m) return 0m;
            decimal body = Math.Abs(c.Close - c.Open);
            return body / range;
        }

        // =========================
        // Dynamic parameters
        // =========================

        private readonly struct ParamSet
        {
            public int TfMin { get; init; }

            public int MinBarsEntry { get; init; }
            public int MinBarsTrend { get; init; }

            public decimal MaxDistanceFromEma34Pct { get; init; }
            public decimal MaxDistanceFromEma34AtrMult { get; init; }

            public decimal ImpulseBodyToRangeMax { get; init; }
            public decimal ImpulseRangeAtrMult { get; init; }

            public decimal EntryVolMinFactor { get; init; }
            public decimal BreakVolMinFactor { get; init; }

            public int RetestLookbackBars { get; init; }
            public decimal RetestTouchBand { get; init; }
            public decimal RetestReclaimBuf { get; init; }
            public decimal RsiMinTypeA { get; init; }

            public int BaseLookbackBars { get; init; }
            public int BaseMinBarsInBox { get; init; }
            public decimal BaseMaxRangeAtr { get; init; }
            public decimal BaseMinLowAboveEma34 { get; init; }
            public decimal BaseBreakBuffer { get; init; }
            public decimal RsiMinTypeB { get; init; }

            public int SwingHighLookbackBars { get; init; }
            public int HoldConfirmBarsMax { get; init; }
            public decimal BreakBuffer { get; init; }
            public decimal HoldBelowBuffer { get; init; }
            public decimal HoldEma34Tol { get; init; }
            public decimal RsiMinTypeC { get; init; }

            public decimal ExitRsiWeak { get; init; }
            public decimal ExitEmaBreakTol { get; init; }

            public static ParamSet For(int tfMin)
            {
                // Baseline designed for 5m
                // We scale for 1m (looser) and for higher TF (slightly tighter).
                tfMin = tfMin <= 0 ? 5 : tfMin;

                // scale factor relative to 5m
                // tf=1 => rel=0.2 (needs looser & shorter lookbacks)
                // tf=15 => rel=3.0 (needs longer lookbacks, can be stricter)
                decimal rel = tfMin / 5m;

                // Lookbacks: inverse-ish for lower TF (shorter), longer for higher TF
                int ScaleLookback(int baseVal, int minVal, int maxVal)
                {
                    // use sqrt to avoid over-scaling
                    double s = Math.Sqrt((double)rel);
                    int v = (int)Math.Round(baseVal * s);
                    if (v < minVal) v = minVal;
                    if (v > maxVal) v = maxVal;
                    return v;
                }

                // Distances: for lower TF, allow larger percentage/ATR distance
                decimal ScaleLoose(decimal baseVal, decimal minVal, decimal maxVal)
                {
                    // looser when tf smaller: multiply by sqrt(5/tf)
                    decimal k = (decimal)Math.Sqrt((double)(5m / tfMin));
                    decimal v = baseVal * k;
                    return Clamp(v, minVal, maxVal);
                }

                // RSI mins: for lower TF, allow slightly lower
                decimal ScaleRsiMin(decimal baseVal, decimal minVal, decimal maxVal)
                {
                    // tf smaller => subtract a bit
                    decimal adj = (tfMin <= 1 ? 4m : tfMin <= 3 ? 2m : tfMin >= 15 ? -1m : 0m);
                    return Clamp(baseVal - adj, minVal, maxVal);
                }

                // Volume gates: for lower TF, reduce min factor a bit
                decimal ScaleVolFactor(decimal baseVal, decimal minVal, decimal maxVal)
                {
                    decimal v = baseVal;
                    if (tfMin <= 1) v -= 0.10m;
                    else if (tfMin <= 3) v -= 0.05m;
                    else if (tfMin >= 15) v += 0.03m;

                    return Clamp(v, minVal, maxVal);
                }

                // MinBars: lower TF needs fewer bars; higher TF needs enough but not crazy
                int minBarsEntry = (tfMin <= 1) ? 260 : (tfMin <= 3) ? 220 : (tfMin <= 5) ? 180 : 160;
                int minBarsTrend = (tfMin <= 1) ? 200 : (tfMin <= 3) ? 160 : 120;

                // Exit tolerances
                decimal exitTol = Clamp(ExitEmaBreakTolBase * (tfMin <= 1 ? 1.25m : tfMin <= 3 ? 1.10m : 1.0m), 0.00035m, 0.00070m);
                decimal exitRsi = Clamp(ExitRsiWeakBase - (tfMin <= 1 ? 1m : 0m), 40m, 46m);

                return new ParamSet
                {
                    TfMin = tfMin,

                    MinBarsEntry = minBarsEntry,
                    MinBarsTrend = minBarsTrend,

                    // Anti-chase
                    MaxDistanceFromEma34Pct = ScaleLoose(0.0035m, 0.0025m, 0.0080m),
                    MaxDistanceFromEma34AtrMult = ScaleLoose(1.10m, 0.90m, 2.20m),

                    // Impulse
                    ImpulseBodyToRangeMax = Clamp(0.75m + (tfMin <= 1 ? 0.06m : tfMin <= 3 ? 0.03m : 0m), 0.72m, 0.85m),
                    ImpulseRangeAtrMult = Clamp(1.20m - (tfMin <= 1 ? 0.10m : tfMin <= 3 ? 0.05m : 0m), 1.00m, 1.30m),

                    // Volume
                    EntryVolMinFactor = ScaleVolFactor(0.60m, 0.40m, 0.75m),
                    BreakVolMinFactor = ScaleVolFactor(0.90m, 0.60m, 1.05m),

                    // Type A
                    RetestLookbackBars = ScaleLookback(8, 5, 14),
                    RetestTouchBand = ScaleLoose(0.0010m, 0.0008m, 0.0018m),
                    RetestReclaimBuf = ScaleLoose(0.0003m, 0.0002m, 0.0008m),
                    RsiMinTypeA = ScaleRsiMin(45m, 38m, 50m),

                    // Type B
                    BaseLookbackBars = ScaleLookback(6, 4, 12),
                    BaseMinBarsInBox = (tfMin <= 1) ? 3 : 4,
                    BaseMaxRangeAtr = Clamp(0.85m + (tfMin <= 1 ? 0.15m : tfMin <= 3 ? 0.08m : 0m), 0.80m, 1.15m),
                    BaseMinLowAboveEma34 = ScaleLoose(0.0012m, 0.0008m, 0.0025m),
                    BaseBreakBuffer = ScaleLoose(0.0000m, 0.0000m, 0.0006m), // small buffer for noise
                    RsiMinTypeB = ScaleRsiMin(42m, 36m, 48m),

                    // Type C
                    SwingHighLookbackBars = ScaleLookback(40, 24, 90),
                    HoldConfirmBarsMax = (tfMin <= 1) ? 2 : 3,
                    BreakBuffer = ScaleLoose(0.0005m, 0.0003m, 0.0012m),
                    HoldBelowBuffer = ScaleLoose(0.0005m, 0.0003m, 0.0012m),
                    HoldEma34Tol = ScaleLoose(0.0000m, 0.0000m, 0.0008m),
                    RsiMinTypeC = ScaleRsiMin(48m, 40m, 55m),

                    // Exit dynamic
                    ExitRsiWeak = exitRsi,
                    ExitEmaBreakTol = exitTol
                };
            }
        }

        private static decimal Clamp(decimal v, decimal min, decimal max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        private static int ParseIntervalMinutesSafe(string? frameTime)
        {
            if (string.IsNullOrWhiteSpace(frameTime)) return 5;

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

            return 5;
        }
    }
}
