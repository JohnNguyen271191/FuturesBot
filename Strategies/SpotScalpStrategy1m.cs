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
    /// Design goals:
    /// - Long-only entries (SignalType.Long)
    /// - Exit signal via SignalType.Short (Spot OMS interprets as SELL / exit)
    /// - Simple, fast, low-latency logic suitable for 1m noise
    /// - TP/SL small; winrate-oriented
    ///
    /// NOTE: This intentionally does NOT share Futures assumptions (leverage, positionSide, trend confirmations).
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
        private const int EmaPeriod = 34;
        private const int RsiPeriod = 14;

        // Signal gates
        private const decimal RsiLongMin = 52m;
        private const decimal RsiExitMax = 48m;

        // Default TP/SL for 1m spot (ratio)
        private const decimal DefaultTp = 0.0045m; // +0.45%
        private const decimal DefaultSl = 0.0030m; // -0.30%

        public TradeSignal GenerateSignal(IReadOnlyList<Candle> candlesMain, IReadOnlyList<Candle> candlesTrend, SpotCoinConfig coinInfo)
        {
            // Spot 1m is noisy; we operate purely on main TF.
            if (candlesMain == null || candlesMain.Count < MinBars)
                return new TradeSignal { Type = SignalType.None, Symbol = coinInfo.Symbol, Reason = "Not enough bars" };

            // Use last CLOSED candle.
            int i = candlesMain.Count - 2;
            int iPrev = i - 1;
            if (iPrev <= 0)
                return new TradeSignal { Type = SignalType.None, Symbol = coinInfo.Symbol, Reason = "Not enough closed bars" };

            var ema = _indicators.Ema(candlesMain, EmaPeriod);
            var rsi = _indicators.Rsi(candlesMain, RsiPeriod);

            var close = candlesMain[i].Close;
            var closePrev = candlesMain[iPrev].Close;
            var emaNow = ema[i];
            var emaPrev = ema[iPrev];
            var rsiNow = rsi[i];

            // Basic cross logic:
            bool crossUp = closePrev <= emaPrev && close > emaNow;
            bool crossDown = closePrev >= emaPrev && close < emaNow;

            if (crossUp && rsiNow >= RsiLongMin)
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
                    Reason = $"Spot1m: CrossUp EMA{EmaPeriod} + RSI{RsiPeriod}={rsiNow:F1}"
                };
            }

            // For spot OMS, treat Short as an EXIT hint when momentum flips.
            if (crossDown && rsiNow <= RsiExitMax)
            {
                return new TradeSignal
                {
                    Symbol = coinInfo.Symbol,
                    Time = DateTime.UtcNow,
                    Type = SignalType.Short,
                    Reason = $"Spot1m: CrossDown EMA{EmaPeriod} + RSI{RsiPeriod}={rsiNow:F1} (exit)"
                };
            }

            return new TradeSignal { Type = SignalType.None, Symbol = coinInfo.Symbol };
        }
    }
}
