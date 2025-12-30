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
    /// SpotScalpStrategy1m (IMPROVED) - designed to fit maker-only Spot OMS
    ///
    /// TF:
    /// - Entry TF: 1m (candlesMain)
    /// - Trend TF: 5m (candlesTrend)
    ///
    /// Key ideas:
    /// - Strong trend gate on Trend TF (EMA34 > EMA89 + EMA34 slope up)
    /// - Sideway filter (EMA gap too small => no trade)
    /// - Entry on 1m: retest EMA34 + rejection + 2-step confirm
    /// - Avoid impulse/climax candle to reduce chop & bad fills
    /// - Exit intent: SpotExitNow when structure breaks / trend breaks
    /// </summary>
    public sealed class SpotScalpStrategy1m : ISpotTradingStrategy
    {
        private readonly IndicatorService _indicators;

        // ===== Tunables (spot) =====
        private const int EmaFast = 34;
        private const int EmaSlow = 89;
        private const int RsiPeriod = 14;

        // Sideway filter on 5m: |EMA34-EMA89|/price < 0.08% => no trade
        private const decimal SidewayEmaGapPercent = 0.0008m;

        // Retest band tolerance around EMA34 1m
        private const decimal RetestBandPercent = 0.0003m; // 0.03%

        // Climax filter
        private const int BodyLookback = 20;
        private const decimal ClimaxBodyMultiplier = 1.8m;

        // RSI guard
        private const decimal RsiMinForLong = 52m;
        private const decimal RsiExitBelow = 47m;

        public SpotScalpStrategy1m(IndicatorService indicators)
        {
            _indicators = indicators;
        }

        public TradeSignal GenerateSignal(
            IReadOnlyList<Candle> candlesMain,
            IReadOnlyList<Candle> candlesTrend,
            SpotCoinConfig coinInfo)
        {
            if (candlesMain == null || candlesMain.Count < 120 || candlesTrend == null || candlesTrend.Count < 120)
                return new TradeSignal { Type = SignalType.None, Symbol = coinInfo.Symbol };

            // Use CLOSED candles only (Count - 2)
            var e1 = candlesMain[^2];
            var e0 = candlesMain[^3];
            var t1 = candlesTrend[^2];

            var price = e1.Close;
            if (price <= 0)
                return new TradeSignal { Type = SignalType.None, Symbol = coinInfo.Symbol };

            // ===== Trend indicators (5m) =====
            var ema34_5 = _indicators.Ema(candlesTrend, EmaFast);
            var ema89_5 = _indicators.Ema(candlesTrend, EmaSlow);

            var ema34_5_now = ema34_5[^2];
            var ema34_5_prev = ema34_5[^3];
            var ema89_5_now = ema89_5[^2];

            // Trend gate: EMA34 > EMA89 and EMA34 slope up
            if (ema34_5_now <= ema89_5_now)
                return new TradeSignal { Type = SignalType.None, Symbol = coinInfo.Symbol, Reason = "Spot: Trend gate fail (EMA34<=EMA89)" };

            if (ema34_5_now <= ema34_5_prev)
                return new TradeSignal { Type = SignalType.None, Symbol = coinInfo.Symbol, Reason = "Spot: Trend slope not up" };

            // Sideway filter
            var emaGapPct = Math.Abs(ema34_5_now - ema89_5_now) / price;
            if (emaGapPct < SidewayEmaGapPercent)
                return new TradeSignal { Type = SignalType.None, Symbol = coinInfo.Symbol, Reason = "Spot: Sideway (EMA gap too small)" };

            // ===== Entry indicators (1m) =====
            var ema34_1 = _indicators.Ema(candlesMain, EmaFast);
            var ema34_1_now = ema34_1[^2];

            var rsi = _indicators.Rsi(candlesMain, RsiPeriod);
            var rsiNow = rsi[^2];

            // Avoid climax/impulse candle
            if (IsClimaxCandle(candlesMain))
                return new TradeSignal { Type = SignalType.None, Symbol = coinInfo.Symbol, Reason = "Spot: Climax candle" };

            // Retest + rejection
            var bandTop = ema34_1_now * (1m + RetestBandPercent);
            var touched = e1.Low <= bandTop;
            var rejected = e1.Close > ema34_1_now && e1.Close > e1.Open;

            // 2-step confirm: close breaks previous high (reduce false retest)
            var confirmed = e1.Close > e0.High;

            if (touched && rejected && confirmed && rsiNow >= RsiMinForLong)
            {
                // Provide OMS hints (non-breaking)
                return new TradeSignal
                {
                    Symbol = coinInfo.Symbol,
                    Time = DateTime.UtcNow,
                    Type = SignalType.Long,
                    Reason = $"Spot1m: Retest EMA{EmaFast} + Reject + Confirm, RSI{RsiPeriod}={rsiNow:F1}",
                    SpotEntryRefPrice = e1.Close
                };
            }

            // ===== Exit intent (chart xấu) =====
            // Exit if:
            // - price loses EMA34 1m with weak RSI, OR
            // - Trend TF breaks (EMA34<=EMA89) (rare due to gate, but safe)
            var loseEma = e1.Close < ema34_1_now && rsiNow <= RsiExitBelow;
            var trendBreak = ema34_5_now <= ema89_5_now;

            if (loseEma || trendBreak)
            {
                return new TradeSignal
                {
                    Symbol = coinInfo.Symbol,
                    Time = DateTime.UtcNow,
                    // keep backward compat: Short implies exit for OMS, but also set SpotExitNow
                    Type = SignalType.Short,
                    SpotExitNow = true,
                    Reason = loseEma
                        ? $"Spot1m: Lose EMA{EmaFast} + RSI{RsiPeriod}={rsiNow:F1} (exit)"
                        : "Spot5m: Trend break (exit)"
                };
            }

            return new TradeSignal { Type = SignalType.None, Symbol = coinInfo.Symbol };
        }

        private static bool IsClimaxCandle(IReadOnlyList<Candle> candles)
        {
            // Use last closed candle vs median body
            if (candles.Count < BodyLookback + 5) return false;

            var sample = candles.Skip(Math.Max(0, candles.Count - (BodyLookback + 3))).Take(BodyLookback).ToList();
            if (sample.Count < BodyLookback) return false;

            var bodies = sample
                .Select(c => Math.Abs(c.Close - c.Open))
                .OrderBy(x => x)
                .ToList();

            var median = bodies[bodies.Count / 2];
            var last = Math.Abs(sample[^1].Close - sample[^1].Open);

            return median > 0 && last >= median * ClimaxBodyMultiplier;
        }
    }
}
