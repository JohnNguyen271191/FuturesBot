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
    /// SpotStrategy V2 (3 entry types, rule rõ ràng) - long-only.
    ///
    /// Goals:
    /// - Vô nhiều kèo hơn nhưng vẫn giữ winrate: dùng TrendTF alignment + anti-chase + volume gates.
    /// - 3 entry types:
    ///   A) EMA Retest (winrate cao)
    ///   B) Shallow Pullback / Continuation (vô nhiều kèo)
    ///   C) Break & Hold (breakout có confirm hold để giảm trap)
    ///
    /// Notes:
    /// - Use last CLOSED candle (Count-2).
    /// - candlesTrend is required (Trend TF).
    /// - Strategy returns SignalType.Long for entry, SignalType.Short for exit.
    ///   Spot OMS interprets Short as SELL / exit.
    /// </summary>
    public sealed class SpotStrategyV2 : ISpotTradingStrategy
    {
        private readonly IndicatorService _indicators;

        public SpotStrategyV2(IndicatorService indicators)
        {
            _indicators = indicators;
        }

        // =========================
        // Tunables (reasonable defaults)
        // =========================
        private const int MinBarsEntry = 160;
        private const int MinBarsTrend = 120;

        private const int EmaFast = 34;
        private const int EmaSlow = 89;
        private const int EmaLong = 200;
        private const int RsiPeriod = 14;

        private const int AtrPeriod = 14;
        private const int VolMaPeriod = 20;

        // --- Gates ---
        private const decimal MaxDistanceFromEma34 = 0.0035m; // 0.35%
        private const decimal ImpulseBodyToRangeMax = 0.75m;
        private const decimal ImpulseRangeAtrMult = 1.2m;

        // --- Volume gates ---
        private const decimal EntryVolMinFactor = 0.60m; // >= 0.6 * volMA
        private const decimal BreakVolMinFactor = 0.90m; // for type B/C

        // --- Type A: Retest ---
        private const int RetestLookbackBars = 8;
        private const decimal RetestTouchBand = 0.0010m;    // +0.10% (allow noise)
        private const decimal RetestReclaimBuf = 0.0003m;   // +0.03%
        private const decimal RsiMinTypeA = 45m;

        // --- Type B: Continuation base ---
        private const int BaseLookbackBars = 6;            // 4-6
        private const decimal BaseMaxRangeAtr = 0.85m;
        private const decimal BaseMinLowAboveEma34 = 0.0012m; // allow slight pierce: -0.12%
        private const decimal RsiMinTypeB = 42m;

        // --- Type C: Break & Hold ---
        private const int SwingHighLookbackBars = 40;
        private const int HoldConfirmBarsMax = 3;
        private const decimal BreakBuffer = 0.0005m;       // +0.05%
        private const decimal HoldBelowBuffer = 0.0005m;   // -0.05%
        private const decimal RsiMinTypeC = 48m;

        // --- Exit ---
        private const decimal ExitRsiWeak = 44m;
        private const decimal ExitEmaBreakTol = 0.0004m; // 0.04%

        public TradeSignal GenerateSignal(IReadOnlyList<Candle> candlesMain, IReadOnlyList<Candle> candlesTrend, CoinInfo coinInfo)
        {
            var symbol = coinInfo.Symbol;

            if (candlesMain == null || candlesMain.Count < MinBarsEntry)
                return new TradeSignal { Type = SignalType.None, Symbol = symbol, Reason = "SpotV2: not enough entry bars" };

            if (candlesTrend == null || candlesTrend.Count < MinBarsTrend)
                return new TradeSignal { Type = SignalType.None, Symbol = symbol, Reason = "SpotV2: not enough trend bars" };

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
            // 0) Common Gates
            // =========================
            // G1: Trend alignment + slope
            bool trendUp = (t34 > t89) && (t34 >= t34Prev);
            bool entryBiasOk = e34 >= e89; // light bias
            bool trendOk = trendUp && entryBiasOk;

            if (!trendOk)
            {
                // Exit condition when trend breaks strongly
                if (ShouldExitOnTrendBreak(cE, cEPrev, e34, e89, rsi, t34, t89))
                {
                    return new TradeSignal
                    {
                        Symbol = symbol,
                        Time = DateTime.UtcNow,
                        Type = SignalType.Short,
                        Reason = $"SpotV2: EXIT trendBreak | rsi={rsi:F1} e34={e34:0.##} e89={e89:0.##} t34={t34:0.##} t89={t89:0.##}"
                    };
                }

                return new TradeSignal { Type = SignalType.None, Symbol = symbol, Reason = "SpotV2: trend gate" };
            }

            // G2: anti-chase
            if (e34 > 0m)
            {
                var dist = Math.Abs(close - e34) / e34;
                if (dist > MaxDistanceFromEma34)
                    return new TradeSignal { Type = SignalType.None, Symbol = symbol, Reason = "SpotV2: too far from EMA34" };
            }

            if (atr > 0m)
            {
                var range = (high - low);
                if (range > 0m && lastBodyToRange >= ImpulseBodyToRangeMax && range >= atr * ImpulseRangeAtrMult)
                    return new TradeSignal { Type = SignalType.None, Symbol = symbol, Reason = "SpotV2: impulse chase filter" };
            }

            // G3: liquidity/volume
            if (coinInfo.MinVolumeUsdTrend > 0m)
            {
                var trendVolMa = ComputeVolUsdMa(candlesTrend, VolMaPeriod);
                if (trendVolMa > 0m && trendVolMa < coinInfo.MinVolumeUsdTrend)
                    return new TradeSignal { Type = SignalType.None, Symbol = symbol, Reason = "SpotV2: low trend volume" };
            }

            if (volMa > 0m && lastVolUsd < volMa * EntryVolMinFactor)
                return new TradeSignal { Type = SignalType.None, Symbol = symbol, Reason = "SpotV2: low entry volume" };

            // =========================
            // Entry Priority: A > B > C
            // =========================
            // Type A: EMA retest
            if (IsTypeA_Retest(candlesMain, ema34E, iE, rsi, e34, close, open, coinInfo))
            {
                return new TradeSignal
                {
                    Symbol = symbol,
                    Time = DateTime.UtcNow,
                    Type = SignalType.Long,
                    EntryPrice = close,
                    Reason = $"SpotV2[A] EMA Retest+Reclaim | rsi={rsi:F1} volUsd={lastVolUsd:0} volMA={volMa:0} trendUp=Y"
                };
            }

            // Type B: Continuation base break
            if (IsTypeB_Continuation(candlesMain, ema34E, iE, atr, rsi, volMa))
            {
                return new TradeSignal
                {
                    Symbol = symbol,
                    Time = DateTime.UtcNow,
                    Type = SignalType.Long,
                    EntryPrice = close,
                    Reason = $"SpotV2[B] BaseBreak Continuation | rsi={rsi:F1} atr={atr:F6} volUsd={lastVolUsd:0} volMA={volMa:0}"
                };
            }

            // Type C: Break & Hold
            if (IsTypeC_BreakHold(candlesMain, ema34E, iE, atr, rsi, volMa))
            {
                return new TradeSignal
                {
                    Symbol = symbol,
                    Time = DateTime.UtcNow,
                    Type = SignalType.Long,
                    EntryPrice = close,
                    Reason = $"SpotV2[C] Break&Hold | rsi={rsi:F1} atr={atr:F6} volUsd={lastVolUsd:0} volMA={volMa:0}"
                };
            }

            // Exit soft condition (when in position OMS decides; strategy still emits Short)
            if (ShouldExitSoft(cE, cEPrev, e34, e89, rsi))
            {
                return new TradeSignal
                {
                    Symbol = symbol,
                    Time = DateTime.UtcNow,
                    Type = SignalType.Short,
                    Reason = $"SpotV2: EXIT soft | rsi={rsi:F1} close<EMA34/89"
                };
            }

            return new TradeSignal { Type = SignalType.None, Symbol = symbol };
        }

        // =========================
        // Entry type implementations
        // =========================

        private static bool IsTypeA_Retest(
            IReadOnlyList<Candle> e,
            decimal[] ema34,
            int i,
            decimal rsi,
            decimal e34,
            decimal close,
            decimal open,
            CoinInfo coin)
        {
            if (rsi < RsiMinTypeA) return false;
            if (e34 <= 0m) return false;

            // retest in last N bars: low touches EMA34*(1+band)
            int start = Math.Max(1, i - RetestLookbackBars);
            bool hadTouch = false;
            for (int k = i; k >= start; k--)
            {
                var ek = ema34[k];
                if (ek <= 0m) continue;
                if (e[k].Low <= ek * (1m + RetestTouchBand)) { hadTouch = true; break; }
            }
            if (!hadTouch) return false;

            bool reclaim = close >= e34 * (1m + RetestReclaimBuf);
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
            decimal volMaUsd)
        {
            if (rsi < RsiMinTypeB) return false;
            if (atr <= 0m) return false;

            int end = i; // last closed
            int start = Math.Max(1, end - BaseLookbackBars + 1);
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
            if (boxRange > atr * BaseMaxRangeAtr) return false;

            // base should be above EMA34 (allow tiny pierce)
            decimal e34 = ema34[i];
            if (e34 <= 0m) return false;
            if (minLow < e34 * (1m - BaseMinLowAboveEma34)) return false;

            // trigger: close breaks above box
            var close = e[i].Close;
            if (close <= maxHigh) return false;

            // volume confirm
            var lastVolUsd = e[i].Volume * close;
            if (volMaUsd > 0m && lastVolUsd < volMaUsd * BreakVolMinFactor) return false;

            return true;
        }

        private static bool IsTypeC_BreakHold(
            IReadOnlyList<Candle> e,
            decimal[] ema34,
            int i,
            decimal atr,
            decimal rsi,
            decimal volMaUsd)
        {
            if (rsi < RsiMinTypeC) return false;
            if (atr <= 0m) return false;

            // swing high in lookback excluding recent hold window
            int endSwing = Math.Max(1, i - 1);
            int startSwing = Math.Max(1, endSwing - SwingHighLookbackBars);

            decimal swingHigh = decimal.MinValue;
            for (int k = startSwing; k <= endSwing; k++)
                swingHigh = Math.Max(swingHigh, e[k].High);

            if (swingHigh <= 0m || swingHigh == decimal.MinValue) return false;

            // break condition
            var close = e[i].Close;
            if (close <= swingHigh * (1m + BreakBuffer)) return false;

            // hold confirm: within last 1..3 closed bars, none closes below swingHigh*(1-holdBuf)
            int startHold = Math.Max(1, i - HoldConfirmBarsMax + 1);
            for (int k = startHold; k <= i; k++)
            {
                if (e[k].Close < swingHigh * (1m - HoldBelowBuffer))
                    return false;
            }

            // still above EMA34
            var e34 = ema34[i];
            if (e34 > 0m && close < e34) return false;

            // volume confirm
            var lastVolUsd = e[i].Volume * close;
            if (volMaUsd > 0m && lastVolUsd < volMaUsd * BreakVolMinFactor) return false;

            return true;
        }

        // =========================
        // Exit helpers
        // =========================

        private static bool ShouldExitSoft(Candle c0, Candle c1, decimal ema34, decimal ema89, decimal rsi)
        {
            if (ema34 <= 0m || ema89 <= 0m) return false;
            bool closeBelow34 = c0.Close < ema34 * (1m - ExitEmaBreakTol);
            bool closeBelow89 = c0.Close < ema89 * (1m - ExitEmaBreakTol);
            bool twoClosesBelow34 = (c0.Close < ema34) && (c1.Close < ema34);

            if ((closeBelow34 && rsi <= ExitRsiWeak) || closeBelow89 || twoClosesBelow34)
                return true;

            return false;
        }

        private static bool ShouldExitOnTrendBreak(Candle cE, Candle cEPrev, decimal e34, decimal e89, decimal rsi, decimal t34, decimal t89)
        {
            bool trendBroken = (t34 > 0m && t89 > 0m && t34 < t89);
            if (!trendBroken) return false;

            // require some weakness on entry TF too
            if (ShouldExitSoft(cE, cEPrev, e34, e89, rsi))
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
    }
}
