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
    /// - Dynamic parameters scale with MainTF minutes (1m/3m/5m/15m...)
    /// - Use last CLOSED candle (Count-2)
    /// Returns:
    /// - Long = Entry
    /// - Short = Exit suggestion (OMS may ignore if not in position)
    /// </summary>
    public sealed class SpotStrategy(IndicatorService indicators) : ISpotTradingStrategy
    {
        private readonly IndicatorService _indicators = indicators;

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

        // Type A
        private const int RetestLookbackBarsBase = 8;
        private const decimal RetestTouchBandBase = 0.0010m;
        private const decimal RetestReclaimBufBase = 0.0003m;
        private const decimal RsiMinTypeABase = 45m;

        // Type B
        private const int BaseLookbackBarsBase = 6;
        private const decimal BaseMaxRangeAtrBase = 0.85m;
        private const decimal BaseMinLowAboveEma34Base = 0.0012m;
        private const decimal RsiMinTypeBBase = 42m;

        // Type C
        private const int SwingHighLookbackBarsBase = 40;
        private const int HoldConfirmBarsMaxBase = 3;
        private const decimal BreakBufferBase = 0.0005m;
        private const decimal HoldBelowBufferBase = 0.0005m;
        private const decimal RsiMinTypeCBase = 48m;

        // Exit
        private const decimal ExitRsiWeakBase = 44m;
        private const decimal ExitEmaBreakTolBase = 0.0004m;

        public TradeSignal GenerateSignal(IReadOnlyList<Candle> candlesMain, IReadOnlyList<Candle> candlesTrend, CoinInfo coinInfo)
        {
            var symbol = coinInfo.Symbol;

            var tfMin = ParseTimeFrameMinutes(coinInfo.MainTimeFrame);
            var trendTfMin = ParseTimeFrameMinutes(coinInfo.TrendTimeFrame);

            // scale: nhỏ TF => scale > 1 (1m => 5x)
            var scale = GetScale(tfMin);

            // dynamic params
            int minBarsEntry = ClampInt((int)Math.Round(MinBarsEntryBase * scale), 120, 260); // giữ <= 210-260 để không fail vì fetch 210
            int minBarsTrend = ClampInt((int)Math.Round(MinBarsTrendBase * GetScale(trendTfMin)), 80, 200);

            int retestLookback = ClampInt((int)Math.Round(RetestLookbackBarsBase * scale), 8, 60);
            int baseLookback = ClampInt((int)Math.Round(BaseLookbackBarsBase * scale), 4, 50);
            int swingLookback = ClampInt((int)Math.Round(SwingHighLookbackBarsBase * scale), 30, 200);
            int holdBarsMax = ClampInt((int)Math.Round(HoldConfirmBarsMaxBase * scale), 2, 10);

            // allow more distance/looser volume on 1m
            decimal maxDistFromEma34 = ClampDec(MaxDistanceFromEma34Base * SqrtDec(scale), 0.0035m, 0.0100m);
            decimal impulseBodyToRangeMax = tfMin <= 1 ? 0.80m : ImpulseBodyToRangeMaxBase;
            decimal impulseRangeAtrMult = tfMin <= 1 ? 1.35m : ImpulseRangeAtrMultBase;

            decimal entryVolMinFactor = tfMin <= 1 ? 0.45m : EntryVolMinFactorBase;
            decimal breakVolMinFactor = tfMin <= 1 ? 0.75m : BreakVolMinFactorBase;

            decimal rsiMinA = tfMin <= 1 ? 43m : RsiMinTypeABase;
            decimal rsiMinB = tfMin <= 1 ? 40m : RsiMinTypeBBase;
            decimal rsiMinC = tfMin <= 1 ? 46m : RsiMinTypeCBase;

            decimal exitRsiWeak = tfMin <= 1 ? 42m : ExitRsiWeakBase;
            decimal exitEmaBreakTol = tfMin <= 1 ? 0.0006m : ExitEmaBreakTolBase;

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
            // Trend up: allow very slight slope down on 1m (noise)
            decimal slopeTol = tfMin <= 1 ? 0.0002m : 0m;
            bool trendUp = (t34 > t89) && (t34 >= t34Prev * (1m - slopeTol));
            bool entryBiasOk = e34 >= e89;
            bool trendOk = trendUp && entryBiasOk;

            if (!trendOk)
            {
                // Downtrend / bias fail: still allow "human-like" sweep-reversal long
                // (liquidity sweep -> reclaim -> momentum) to avoid the bot standing still on obvious rebounds.
                if (IsTypeD_SweepReversal(candlesMain, iE, rsi, atr, volMa, tfMin, scale))
                {
                    return new TradeSignal
                    {
                        Symbol = symbol,
                        Time = DateTime.UtcNow,
                        Type = SignalType.Long,
                        EntryPrice = close,
                        Reason = $"SpotV2D[D] SweepReversal | tf={coinInfo.MainTimeFrame} rsi={rsi:F1} atr={atr:F6} volUsd={lastVolUsd:0} volMA={volMa:0}"
                    };
                }

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

            // anti-chase dist from EMA34 (dynamic)
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

            // liquidity/volume trend gate
            if (coinInfo.MinVolumeUsdTrend > 0m)
            {
                var trendVolMa = ComputeVolUsdMa(candlesTrend, VolMaPeriod);
                if (trendVolMa > 0m && trendVolMa < coinInfo.MinVolumeUsdTrend)
                    return new TradeSignal { Type = SignalType.None, Symbol = symbol, Reason = "SpotV2D: low trend volume" };
            }

            // entry volume gate (dynamic)
            if (volMa > 0m && lastVolUsd < volMa * entryVolMinFactor)
                return new TradeSignal { Type = SignalType.None, Symbol = symbol, Reason = $"SpotV2D: low entry volume ({lastVolUsd:0} < {volMa * entryVolMinFactor:0})" };

            // =============== Entry priority A > B > C ===============
            if (IsTypeA_Retest(candlesMain, ema34E, iE, rsi, e34, close, open, retestLookback, RetestTouchBandBase, RetestReclaimBufBase, rsiMinA))
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

            if (IsTypeB_Continuation(candlesMain, ema34E, iE, atr, rsi, volMa, baseLookback, BaseMaxRangeAtrBase, BaseMinLowAboveEma34Base, rsiMinB, breakVolMinFactor))
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

            if (IsTypeC_BreakHold(candlesMain, ema34E, iE, atr, rsi, volMa, swingLookback, holdBarsMax, BreakBufferBase, HoldBelowBufferBase, rsiMinC, breakVolMinFactor))
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
                if (e[k].Low <= ek * (1m + touchBand)) { hadTouch = true; break; }
            }
            if (!hadTouch) return false;

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

            if (minLow < e34 * (1m - minLowAboveEma34)) return false;

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
            if (close <= swingHigh * (1m + breakBuffer)) return false;

            int startHold = Math.Max(1, i - holdBarsMax + 1);
            for (int k = startHold; k <= i; k++)
            {
                if (e[k].Close < swingHigh * (1m - holdBelowBuffer))
                    return false;
            }

            var e34 = ema34[i];
            if (e34 > 0m && close < e34) return false;

            var lastVolUsd = e[i].Volume * close;
            if (volMaUsd > 0m && lastVolUsd < volMaUsd * breakVolMinFactor) return false;

            return true;
        }

        // =========================
        // Type D (NEW): Sweep-Reversal (human-like)
        // - Allow in downtrend: sweep recent low -> reclaim -> momentum.
        // - Designed to avoid "bot đứng im" on obvious rebounds.
        // =========================
        private static bool IsTypeD_SweepReversal(
            IReadOnlyList<Candle> e,
            int iClose,
            decimal rsi,
            decimal atr,
            decimal volMaUsd,
            int tfMin,
            decimal scale)
        {
            // Need at least 3 closed candles
            if (e == null || iClose <= 3) return false;
            if (atr <= 0m) return false;

            // Use 2-candle pattern: (iClose-1) sweep, (iClose) reclaim
            int iSweep = iClose - 1;
            var sweep = e[iSweep];
            var reclaim = e[iClose];

            // Lookback for recent low (exclude sweep/reclaim candles)
            int lookback = ClampInt((int)Math.Round(18 * (double)scale), 10, 40);
            int end = Math.Max(1, iSweep - 1);
            int start = Math.Max(1, end - lookback);

            decimal ll = decimal.MaxValue;
            for (int k = start; k <= end; k++)
                ll = Math.Min(ll, e[k].Low);

            if (ll == decimal.MaxValue || ll <= 0m) return false;

            // Buffers: smaller on lower TF
            // PATCH (human-like): loosen reclaim slightly so the bot can take obvious V-reversal bottoms
            // (hand traders often enter on a shallow reclaim, not necessarily far above the swept low).
            decimal sweepBuf = ClampDec(0.0006m * SqrtDec(scale), 0.00035m, 0.0012m);
            decimal reclaimBuf = ClampDec(0.00035m * SqrtDec(scale), 0.00018m, 0.00085m);

            bool didSweep = sweep.Low < ll * (1m - sweepBuf);
            bool reclaimed = reclaim.Close > ll * (1m + reclaimBuf);
            bool bullishReclaim = reclaim.Close > reclaim.Open;

            // Avoid tiny doji reclaim (but allow a bit softer body vs. range so it triggers more often)
            bool bodyOk = GetBodyToRange(reclaim) >= ClampDec(0.40m - (0.05m * scale), 0.26m, 0.52m);

            // Momentum/health: require RSI recovery (looser on higher TF)
            decimal rsiMin = ClampDec(42m - (4m * scale), 36m, 43m);
            if (rsi < rsiMin) return false;

            // Volume: allow soft pass if volMa isn't available (and reduce strictness a bit)
            var close = reclaim.Close;
            var lastVolUsd = reclaim.Volume * close;
            if (volMaUsd > 0m && lastVolUsd < volMaUsd * 0.60m) return false;

            // Reject if reclaim candle is still dumping (range too large vs ATR)
            var range = reclaim.High - reclaim.Low;
            if (range > atr * 2.4m) return false;

            return didSweep && reclaimed && bullishReclaim && bodyOk;
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

            if (ShouldExitSoft(cE, cEPrev, e34, e89, rsi, exitRsiWeak, emaBreakTol))
                return true;

            return false;
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
            if (string.IsNullOrWhiteSpace(tf)) return 5;
            tf = tf.Trim().ToLowerInvariant();

            if (tf.EndsWith("m") && int.TryParse(tf[..^1], out var m)) return Math.Max(1, m);
            if (tf.EndsWith("h") && int.TryParse(tf[..^1], out var h)) return Math.Max(1, h * 60);

            // fallback
            return 5;
        }

        private static decimal GetScale(int tfMin)
        {
            if (tfMin <= 0) tfMin = BaseTfMin;
            // scale relative to 5m baseline
            return (decimal)BaseTfMin / tfMin;
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