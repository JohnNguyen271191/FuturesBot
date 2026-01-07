using System;
using System.Collections.Generic;
using FuturesBot.Config;
using FuturesBot.IServices;
using FuturesBot.Models;
using FuturesBot.Services;
using static FuturesBot.Utils.EnumTypesHelper;

namespace FuturesBot.Strategies
{
    /// <summary>
    /// SpotStrategy V2 Dynamic (TF-agnostic) - long-only.
    /// - 3 entry types: A Retest, B Continuation, C Break&Hold
    /// - ALL parameters are dynamic and scale with timeframe minutes
    /// - Uses last CLOSED candle (Count-2)
    /// Returns:
    /// - Long  = Entry
    /// - Short = Exit suggestion (OMS should treat as Exit for spot; ignore if no position)
    /// </summary>
    public sealed class SpotStrategyV2Dynamic : ISpotTradingStrategy
    {
        private readonly IndicatorService _indicators;

        public SpotStrategyV2Dynamic(IndicatorService indicators)
        {
            _indicators = indicators;
        }

        // Base reference TF = 5m (từ bản cũ)
        private const int BaseTfMin = 5;

        private const int EmaFast = 34;
        private const int EmaSlow = 89;
        private const int EmaLong = 200;
        private const int RsiPeriod = 14;
        private const int AtrPeriod = 14;
        private const int VolMaPeriod = 20;

        // ==== Base tunables (for 5m) ====
        private const int MinBarsEntryBase = 200;
        private const int MinBarsTrendBase = 120;

        private const decimal MaxDistanceFromEma34Base = 0.0035m; // 0.35%
        private const decimal ImpulseBodyToRangeMaxBase = 0.75m;
        private const decimal ImpulseRangeAtrMultBase = 1.2m;

        private const decimal EntryVolMinFactorBase = 0.60m;
        private const decimal BreakVolMinFactorBase = 0.90m;

        // Type A (Retest)
        private const int RetestLookbackBarsBase = 8;
        private const decimal RetestTouchBandBase = 0.0010m;   // 0.10%
        private const decimal RetestReclaimBufBase = 0.0003m;  // 0.03%
        private const decimal RsiMinTypeABase = 45m;

        // Type B (Continuation / base break)
        private const int BaseLookbackBarsBase = 6;
        private const decimal BaseMaxRangeAtrBase = 0.85m;
        private const decimal BaseMinLowAboveEma34Base = 0.0012m; // minLow must be ABOVE EMA34 by 0.12%
        private const decimal RsiMinTypeBBase = 42m;

        // Type C (Break & Hold)
        private const int SwingHighLookbackBarsBase = 40;
        private const int HoldConfirmBarsMaxBase = 3;
        private const decimal BreakBufferBase = 0.0005m;      // 0.05%
        private const decimal HoldBelowBufferBase = 0.0005m;  // 0.05%
        private const decimal RsiMinTypeCBase = 48m;

        // Exit
        private const decimal ExitRsiWeakBase = 44m;
        private const decimal ExitEmaBreakTolBase = 0.0004m;

        public TradeSignal GenerateSignal(IReadOnlyList<Candle> candlesMain, IReadOnlyList<Candle> candlesTrend, CoinInfo coinInfo)
        {
            var symbol = coinInfo.Symbol;

            var tfMin = ParseTimeFrameMinutes(coinInfo.MainTimeFrame);
            var trendTfMin = ParseTimeFrameMinutes(coinInfo.TrendTimeFrame);

            // factorTF: >1 for larger TF (15m/1h), <1 for smaller TF (1m/3m)
            // clamp so it doesn't go too extreme when switching TF
            var factorTf = GetFactorTf(tfMin);
            var factorTrendTf = GetFactorTf(trendTfMin);

            // dynamic params (bars)
            int minBarsEntry = ClampInt((int)Math.Round(MinBarsEntryBase * factorTf), 160, 340);
            int minBarsTrend = ClampInt((int)Math.Round(MinBarsTrendBase * factorTrendTf), 90, 260);

            int retestLookback = ClampInt((int)Math.Round(RetestLookbackBarsBase * factorTf), 6, 90);
            int baseLookback = ClampInt((int)Math.Round(BaseLookbackBarsBase * factorTf), 4, 70);
            int swingLookback = ClampInt((int)Math.Round(SwingHighLookbackBarsBase * factorTf), 30, 260);
            int holdBarsMax = ClampInt((int)Math.Round(HoldConfirmBarsMaxBase * factorTf), 2, 14);

            // dynamic params (percents / thresholds)
            // rule of thumb:
            // - Larger TF: allow wider bands/buffers (moves are larger)
            // - Smaller TF: bands shrink a bit, but never too tight due to clamps
            decimal maxDistFromEma34 = ClampDec(MaxDistanceFromEma34Base * factorTf, 0.0028m, 0.0120m);
            decimal impulseBodyToRangeMax = ClampDec(ImpulseBodyToRangeMaxBase + (factorTf - 1m) * 0.03m, 0.68m, 0.82m);
            decimal impulseRangeAtrMult = ClampDec(ImpulseRangeAtrMultBase + (factorTf - 1m) * 0.08m, 1.05m, 1.60m);

            // Volume gates:
            // - Smaller TF is noisier => require a bit more volume
            // - Larger TF is smoother => can accept a bit less
            decimal entryVolMinFactor = ClampDec(EntryVolMinFactorBase / factorTf, 0.35m, 0.85m);
            decimal breakVolMinFactor = ClampDec(BreakVolMinFactorBase / factorTf, 0.55m, 1.10m);

            // Type A dynamic
            decimal retestTouchBand = ClampDec(RetestTouchBandBase * factorTf, 0.0006m, 0.0030m);
            decimal retestReclaimBuf = ClampDec(RetestReclaimBufBase * factorTf, 0.0002m, 0.0012m);
            decimal rsiMinA = ClampDec(RsiMinTypeABase + (factorTf - 1m) * 2.0m, 40m, 55m);

            // Type B dynamic
            decimal baseMaxRangeAtr = ClampDec(BaseMaxRangeAtrBase + (factorTf - 1m) * 0.05m, 0.70m, 1.15m);
            decimal minLowAboveEma34 = ClampDec(BaseMinLowAboveEma34Base * factorTf, 0.0006m, 0.0040m);
            decimal rsiMinB = ClampDec(RsiMinTypeBBase + (factorTf - 1m) * 2.0m, 38m, 54m);

            // Type C dynamic
            decimal breakBuffer = ClampDec(BreakBufferBase * factorTf, 0.0003m, 0.0025m);
            decimal holdBelowBuffer = ClampDec(HoldBelowBufferBase * factorTf, 0.0003m, 0.0030m);
            decimal rsiMinC = ClampDec(RsiMinTypeCBase + (factorTf - 1m) * 2.5m, 42m, 58m);

            // Exit dynamic
            decimal exitRsiWeak = ClampDec(ExitRsiWeakBase + (factorTf - 1m) * 1.5m, 38m, 55m);
            decimal exitEmaBreakTol = ClampDec(ExitEmaBreakTolBase * factorTf, 0.00025m, 0.00120m);

            if (candlesMain == null || candlesMain.Count < minBarsEntry)
                return new TradeSignal { Type = SignalType.None, Symbol = symbol, Reason = $"SpotV2D: not enough entry bars ({candlesMain?.Count ?? 0}<{minBarsEntry})" };

            if (candlesTrend == null || candlesTrend.Count < minBarsTrend)
                return new TradeSignal { Type = SignalType.None, Symbol = symbol, Reason = $"SpotV2D: not enough trend bars ({candlesTrend?.Count ?? 0}<{minBarsTrend})" };

            int iE = candlesMain.Count - 2;
            int iE1 = iE - 1;
            if (iE1 < 2)
                return new TradeSignal { Type = SignalType.None, Symbol = symbol, Reason = "SpotV2D: not enough closed entry bars" };

            int iT = candlesTrend.Count - 2;
            int iT1 = iT - 1;
            if (iT1 < 2)
                return new TradeSignal { Type = SignalType.None, Symbol = symbol, Reason = "SpotV2D: not enough closed trend bars" };

            var ema34E = _indicators.Ema(candlesMain, EmaFast);
            var ema89E = _indicators.Ema(candlesMain, EmaSlow);
            var ema200E = _indicators.Ema(candlesMain, EmaLong);
            var rsiE = _indicators.Rsi(candlesMain, RsiPeriod);

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
            var e34Prev = ema34E[iE1];
            var e89 = ema89E[iE];
            var e200 = ema200E[iE];
            var rsi = rsiE[iE];

            var t34 = ema34T[iT];
            var t89 = ema89T[iT];
            var t34Prev = ema34T[iT1];

            decimal atr = ComputeAtr(candlesMain, AtrPeriod);
            decimal volMa = ComputeVolUsdMa(candlesMain, VolMaPeriod);
            decimal lastVolUsd = cE.Volume * close;
            decimal lastBodyToRange = GetBodyToRange(cE);

            // =============== 0) Common gates ===============
            // Trend up gate (Trend TF is the boss)
            // allow a tiny slope noise depending on TF (dynamic)
            decimal slopeTol = ClampDec(0.00015m / factorTrendTf, 0.00003m, 0.00025m);
            bool trendUp = (t34 > 0m && t89 > 0m) &&
                           (t34 > t89) &&
                           (t34 >= t34Prev * (1m - slopeTol));

            if (!trendUp)
            {
                if (ShouldExitOnTrendBreak(cE, cEPrev, e34, e89, rsi, t34, t89, exitRsiWeak, exitEmaBreakTol))
                {
                    return new TradeSignal
                    {
                        Symbol = symbol,
                        Time = DateTime.UtcNow,
                        Type = SignalType.Short,
                        Reason = $"SpotV2D: EXIT trendBreak | tf={coinInfo.MainTimeFrame} rsi={rsi:F1} e34={e34:0.##} e89={e89:0.##} t34={t34:0.##} t89={t89:0.##}"
                    };
                }

                return new TradeSignal { Type = SignalType.None, Symbol = symbol, Reason = "SpotV2D: trend gate" };
            }

            // anti-chase distance from EMA34 (dynamic)
            if (e34 > 0m)
            {
                var dist = Math.Abs(close - e34) / e34;
                if (dist > maxDistFromEma34)
                    return new TradeSignal { Type = SignalType.None, Symbol = symbol, Reason = $"SpotV2D: too far from EMA34 (dist={dist:P3} > {maxDistFromEma34:P3})" };
            }

            // impulse chase filter (dynamic)
            if (atr > 0m)
            {
                var range = (high - low);
                if (range > 0m && lastBodyToRange >= impulseBodyToRangeMax && range >= atr * impulseRangeAtrMult)
                    return new TradeSignal { Type = SignalType.None, Symbol = symbol, Reason = "SpotV2D: impulse chase filter" };
            }

            // liquidity/volume trend gate (trend TF)
            if (coinInfo.MinVolumeUsdTrend > 0m)
            {
                var trendVolMa = ComputeVolUsdMa(candlesTrend, VolMaPeriod);
                if (trendVolMa > 0m && trendVolMa < coinInfo.MinVolumeUsdTrend)
                    return new TradeSignal { Type = SignalType.None, Symbol = symbol, Reason = "SpotV2D: low trend volume" };
            }

            // entry volume gate (dynamic)
            if (volMa > 0m && lastVolUsd < volMa * entryVolMinFactor)
                return new TradeSignal { Type = SignalType.None, Symbol = symbol, Reason = $"SpotV2D: low entry volume ({lastVolUsd:0} < {volMa * entryVolMinFactor:0})" };

            // ============================
            // Entry priority A > B > C
            // ============================
            // Entry bias logic (dynamic, per-type):
            // - Type A can enter earlier in transition if EMA34 is rising and price reclaims EMA34
            // - Type B/C require stronger confirmation (EMA34 >= EMA89)
            bool entryBiasStrong = e34 >= e89;
            bool ema34Rising = (e34 > 0m && e34Prev > 0m && e34 >= e34Prev);

            // Type A (Retest): allow transition entry
            bool allowTypeA = entryBiasStrong || (ema34Rising && close >= e34);

            if (allowTypeA && IsTypeA_Retest(
                    candlesMain, ema34E, iE, rsi, e34, close, open,
                    retestLookback, retestTouchBand, retestReclaimBuf, rsiMinA))
            {
                return new TradeSignal
                {
                    Symbol = symbol,
                    Time = DateTime.UtcNow,
                    Type = SignalType.Long,
                    EntryPrice = close,
                    Reason = $"SpotV2D[A] EMA Retest+Reclaim | tf={coinInfo.MainTimeFrame} rsi={rsi:F1} volUsd={lastVolUsd:0} volMA={volMa:0}"
                };
            }

            // Type B (Continuation): require stronger bias
            if (entryBiasStrong && IsTypeB_Continuation(
                    candlesMain, ema34E, iE, atr, rsi, volMa,
                    baseLookback, baseMaxRangeAtr, minLowAboveEma34, rsiMinB, breakVolMinFactor))
            {
                return new TradeSignal
                {
                    Symbol = symbol,
                    Time = DateTime.UtcNow,
                    Type = SignalType.Long,
                    EntryPrice = close,
                    Reason = $"SpotV2D[B] BaseBreak Continuation | tf={coinInfo.MainTimeFrame} rsi={rsi:F1} atr={atr:F6} volUsd={lastVolUsd:0} volMA={volMa:0}"
                };
            }

            // Type C (Break&Hold): require stronger bias
            if (entryBiasStrong && IsTypeC_BreakHold(
                    candlesMain, ema34E, iE, atr, rsi, volMa,
                    swingLookback, holdBarsMax, breakBuffer, holdBelowBuffer, rsiMinC, breakVolMinFactor))
            {
                return new TradeSignal
                {
                    Symbol = symbol,
                    Time = DateTime.UtcNow,
                    Type = SignalType.Long,
                    EntryPrice = close,
                    Reason = $"SpotV2D[C] Break&Hold | tf={coinInfo.MainTimeFrame} rsi={rsi:F1} atr={atr:F6} volUsd={lastVolUsd:0} volMA={volMa:0}"
                };
            }

            // Exit soft suggestion
            if (ShouldExitSoft(cE, cEPrev, e34, e89, rsi, exitRsiWeak, exitEmaBreakTol))
            {
                return new TradeSignal
                {
                    Symbol = symbol,
                    Time = DateTime.UtcNow,
                    Type = SignalType.Short,
                    Reason = $"SpotV2D: EXIT soft | tf={coinInfo.MainTimeFrame} rsi={rsi:F1} close<EMA34/89"
                };
            }

            return new TradeSignal { Type = SignalType.None, Symbol = symbol };
        }

        // =========================
        // Entry type implementations (dynamic)
        // =========================

        private static bool IsTypeA_Retest(
            IReadOnlyList<Candle> e,
            decimal[] ema34,
            int i,
            decimal rsi,
            decimal e34,
            decimal close,
            decimal open,
            int lookback,
            decimal touchBand,
            decimal reclaimBuf,
            decimal rsiMin)
        {
            if (rsi < rsiMin) return false;
            if (e34 <= 0m) return false;

            int start = Math.Max(1, i - lookback);
            bool hadTouch = false;

            for (int k = i; k >= start; k--)
            {
                var ek = ema34[k];
                if (ek <= 0m) continue;

                // touch band around EMA34
                if (e[k].Low <= ek * (1m + touchBand))
                {
                    hadTouch = true;
                    break;
                }
            }

            if (!hadTouch) return false;

            // reclaim above EMA34 with buffer
            if (close < e34 * (1m + reclaimBuf)) return false;

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
            int baseLookback,
            decimal baseMaxRangeAtr,
            decimal minLowAboveEma34,
            decimal rsiMin,
            decimal breakVolMinFactor)
        {
            if (rsi < rsiMin) return false;
            if (atr <= 0m) return false;

            int end = i;
            int start = Math.Max(1, end - baseLookback + 1);
            if (end - start + 1 < 4) return false;

            decimal maxHigh = decimal.MinValue;
            decimal minLow = decimal.MaxValue;

            for (int k = start; k <= end; k++)
            {
                maxHigh = Math.Max(maxHigh, e[k].High);
                minLow = Math.Min(minLow, e[k].Low);
            }

            decimal boxRange = maxHigh - minLow;
            if (boxRange <= 0m) return false;
            if (boxRange > atr * baseMaxRangeAtr) return false;

            decimal e34 = ema34[i];
            if (e34 <= 0m) return false;

            // IMPORTANT FIX:
            // minLow must be ABOVE EMA34 by at least minLowAboveEma34 (not below)
            if (minLow < e34 * (1m + minLowAboveEma34)) return false;

            var close = e[i].Close;
            if (close <= maxHigh) return false;

            var lastVolUsd = e[i].Volume * close;
            if (volMaUsd > 0m && lastVolUsd < volMaUsd * breakVolMinFactor) return false;

            return true;
        }

        private static bool IsTypeC_BreakHold(
            IReadOnlyList<Candle> e,
            decimal[] ema34,
            int i,
            decimal atr,
            decimal rsi,
            decimal volMaUsd,
            int swingLookback,
            int holdBarsMax,
            decimal breakBuffer,
            decimal holdBelowBuffer,
            decimal rsiMin,
            decimal breakVolMinFactor)
        {
            if (rsi < rsiMin) return false;
            if (atr <= 0m) return false;

            int endSwing = Math.Max(1, i - 1);
            int startSwing = Math.Max(1, endSwing - swingLookback);

            decimal swingHigh = decimal.MinValue;
            for (int k = startSwing; k <= endSwing; k++)
                swingHigh = Math.Max(swingHigh, e[k].High);

            if (swingHigh <= 0m || swingHigh == decimal.MinValue) return false;

            var close = e[i].Close;

            // break above swing high with buffer
            if (close <= swingHigh * (1m + breakBuffer)) return false;

            // hold: last N closes must not lose swing high by buffer
            int startHold = Math.Max(1, i - holdBarsMax + 1);
            for (int k = startHold; k <= i; k++)
            {
                if (e[k].Close < swingHigh * (1m - holdBelowBuffer))
                    return false;
            }

            // stay above EMA34 (basic trend alignment)
            var e34 = ema34[i];
            if (e34 > 0m && close < e34) return false;

            // break volume confirm
            var lastVolUsd = e[i].Volume * close;
            if (volMaUsd > 0m && lastVolUsd < volMaUsd * breakVolMinFactor) return false;

            return true;
        }

        // =========================
        // Exit helpers (dynamic)
        // =========================
        private static bool ShouldExitSoft(Candle c0, Candle c1, decimal ema34, decimal ema89, decimal rsi, decimal exitRsiWeak, decimal emaBreakTol)
        {
            if (ema34 <= 0m || ema89 <= 0m) return false;

            bool closeBelow34 = c0.Close < ema34 * (1m - emaBreakTol);
            bool closeBelow89 = c0.Close < ema89 * (1m - emaBreakTol);
            bool twoClosesBelow34 = (c0.Close < ema34) && (c1.Close < ema34);

            if ((closeBelow34 && rsi <= exitRsiWeak) || closeBelow89 || twoClosesBelow34)
                return true;

            return false;
        }

        private static bool ShouldExitOnTrendBreak(Candle cE, Candle cEPrev, decimal e34, decimal e89, decimal rsi, decimal t34, decimal t89, decimal exitRsiWeak, decimal emaBreakTol)
        {
            bool trendBroken = (t34 > 0m && t89 > 0m && t34 < t89);
            if (!trendBroken) return false;

            return ShouldExitSoft(cE, cEPrev, e34, e89, rsi, exitRsiWeak, emaBreakTol);
        }

        // =========================
        // Math helpers
        // =========================
        private static decimal ComputeAtr(IReadOnlyList<Candle> candles, int period)
        {
            if (candles == null || candles.Count < period + 2) return 0m;
            int end = candles.Count - 2;
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

        private static int ParseTimeFrameMinutes(string? tf)
        {
            if (string.IsNullOrWhiteSpace(tf)) return BaseTfMin;
            tf = tf.Trim().ToLowerInvariant();

            if (tf.EndsWith("m") && int.TryParse(tf[..^1], out var m)) return Math.Max(1, m);
            if (tf.EndsWith("h") && int.TryParse(tf[..^1], out var h)) return Math.Max(1, h * 60);

            return BaseTfMin;
        }

        /// <summary>
        /// factorTF: sqrt(tfMin / BaseTfMin) clamped
        /// - 1m  => ~0.447 -> clamp to >=0.75 (avoid too tight)
        /// - 5m  => 1.0
        /// - 15m => ~1.732
        /// - 1h  => ~3.464 -> clamp to <=2.50 (avoid too loose)
        /// </summary>
        private static decimal GetFactorTf(int tfMin)
        {
            if (tfMin <= 0) tfMin = BaseTfMin;
            var raw = SqrtDec((decimal)tfMin / BaseTfMin);
            return ClampDec(raw, 0.75m, 2.50m);
        }

        private static decimal SqrtDec(decimal x)
        {
            if (x <= 0m) return 0m;
            return (decimal)Math.Sqrt((double)x);
        }

        private static int ClampInt(int v, int min, int max) => Math.Min(max, Math.Max(min, v));
        private static decimal ClampDec(decimal v, decimal min, decimal max) => Math.Min(max, Math.Max(min, v));
    }
}
