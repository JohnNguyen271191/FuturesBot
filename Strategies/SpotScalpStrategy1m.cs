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
    /// Spot 1m scalp strategy (BTC/ETH majors by default).
    ///
    /// UPDATED (Retest-based, not hard cross):
    /// - Long-only entries (SignalType.Long)
    /// - Entry uses "nearest retest" of EMA34 within recent lookback + reclaim confirmation
    /// - Optional light trend bias using EMA34 vs EMA89 to avoid dirty chop
    /// - Exit via SignalType.Short (Spot OMS interprets as SELL / exit)
    ///
    /// NOTES:
    /// - Uses last CLOSED candle (Count - 2)
    /// - Designed for 1m noise: tolerant retest band + softer RSI gate
    /// </summary>
    public sealed class SpotScalpStrategy1m : ISpotTradingStrategy
    {
        private readonly IndicatorService _indicators;

        public SpotScalpStrategy1m(IndicatorService indicators)
        {
            _indicators = indicators;
        }

        // --- Tuneables (keep conservative; OMS also has defaults/rescue) ---
        private const int MinBars = 120;

        // Core indicators
        private const int EmaFastPeriod = 34;
        private const int EmaSlowPeriod = 89;
        private const int RsiPeriod = 14;

        // Retest logic
        private const int RetestLookbackBars = 6;          // search retest in last N bars
        private const decimal RetestBandPercent = 0.0010m; // 0.10% band around EMA34
        private const decimal ReclaimBufferPercent = 0.0002m; // 0.02% reclaim buffer

        // Signal gates (softened to reduce missed trades)
        private const decimal RsiLongMin = 50m;
        private const decimal RsiExitMax = 48m;

        // Default TP/SL for 1m spot (ratio)
        private const decimal DefaultTp = 0.0045m; // +0.45%
        private const decimal DefaultSl = 0.0030m; // -0.30%

        public TradeSignal GenerateSignal(IReadOnlyList<Candle> candlesMain, IReadOnlyList<Candle> candlesTrend, CoinInfo coinInfo)
        {
            // Spot 1m is noisy; operate purely on main TF.
            if (candlesMain == null || candlesMain.Count < MinBars)
                return new TradeSignal { Type = SignalType.None, Symbol = coinInfo.Symbol, Reason = "Not enough bars" };

            // Use last CLOSED candle.
            int i = candlesMain.Count - 2;
            if (i <= 2)
                return new TradeSignal { Type = SignalType.None, Symbol = coinInfo.Symbol, Reason = "Not enough closed bars" };

            int iPrev = i - 1;

            // Indicators
            var emaFast = _indicators.Ema(candlesMain, EmaFastPeriod);
            var emaSlow = _indicators.Ema(candlesMain, EmaSlowPeriod);
            var rsi = _indicators.Rsi(candlesMain, RsiPeriod);

            var c = candlesMain[i];
            var cPrev = candlesMain[iPrev];

            var close = c.Close;
            var open = c.Open;
            var low = c.Low;

            var emaFastNow = emaFast[i];
            var emaSlowNow = emaSlow[i];
            var rsiNow = rsi[i];

            // -------------------------------
            // 1) Light trend bias (avoid nasty chop)
            // -------------------------------
            // Keep it simple for 1m: require EMA34 not below EMA89.
            bool trendOk = emaFastNow >= emaSlowNow;

            // -------------------------------
            // 2) Nearest retest in last N bars
            //    "Retest" means price touched/undercut EMA34 band recently.
            // -------------------------------
            int start = Math.Max(1, i - RetestLookbackBars);
            bool hadRetest = false;
            int retestIndex = -1;

            for (int k = i; k >= start; k--)
            {
                var emaK = emaFast[k];
                var lowK = candlesMain[k].Low;

                // Touch/undercut into EMA band (allow noise)
                if (lowK <= emaK * (1m + RetestBandPercent))
                {
                    hadRetest = true;
                    retestIndex = k;
                    break; // nearest retest
                }
            }

            // -------------------------------
            // 3) Reclaim/confirm
            // -------------------------------
            bool reclaim = close > emaFastNow * (1m + ReclaimBufferPercent);
            bool bullishCandle = close > open;

            // Also allow "reclaim after a down candle" as long as it's closing above EMA
            // by keeping bullishCandle as a soft condition:
            bool confirmOk = reclaim && (bullishCandle || (close > cPrev.Close));

            // -------------------------------
            // ENTRY: Long on retest + reclaim + RSI
            // -------------------------------
            if (trendOk && hadRetest && confirmOk && rsiNow >= RsiLongMin)
            {
                var entry = close;

                return new TradeSignal
                {
                    Symbol = coinInfo.Symbol,
                    Time = DateTime.UtcNow,
                    Type = SignalType.Long,
                    EntryPrice = entry,
                    StopLoss = entry * (1m - DefaultSl),
                    TakeProfit = entry * (1m + DefaultTp),
                    Reason = $"Spot1m: Retest EMA{EmaFastPeriod} (idx={retestIndex}) + reclaim + RSI{RsiPeriod}={rsiNow:F1}"
                };
            }

            // -------------------------------
            // EXIT: soften exit (avoid whipsaw)
            // - Close below EMA34 + RSI weak, OR
            // - Two consecutive closes below EMA34 (structure lost)
            // -------------------------------
            bool closeBelowFast = c.Close < emaFastNow;
            bool closeBelowFastPrev = cPrev.Close < emaFast[iPrev];
            bool twoClosesBelow = closeBelowFast && closeBelowFastPrev;

            if ((closeBelowFast && rsiNow <= RsiExitMax) || twoClosesBelow)
            {
                return new TradeSignal
                {
                    Symbol = coinInfo.Symbol,
                    Time = DateTime.UtcNow,
                    Type = SignalType.Short,
                    Reason = twoClosesBelow
                        ? $"Spot1m: 2 closes below EMA{EmaFastPeriod} (exit)"
                        : $"Spot1m: Close<EMA{EmaFastPeriod} + RSI{RsiPeriod}={rsiNow:F1} (exit)"
                };
            }

            return new TradeSignal { Type = SignalType.None, Symbol = coinInfo.Symbol };
        }
    }
}
