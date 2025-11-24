using FuturesBot.Models;

namespace FuturesBot.Services
{
    public class IndicatorService
    {
        public decimal[] Ema(IReadOnlyList<Candle> candles, int period)
        {
            var result = new decimal[candles.Count];
            if (candles.Count == 0) return result;

            decimal k = 2m / (period + 1);
            result[0] = candles[0].Close;

            for (int i = 1; i < candles.Count; i++)
            {
                result[i] = candles[i].Close * k + result[i - 1] * (1 - k);
            }

            return result;
        }

        public decimal[] Rsi(IReadOnlyList<Candle> candles, int period)
        {
            var result = new decimal[candles.Count];
            if (candles.Count <= period) return result;

            decimal gain = 0, loss = 0;

            for (int i = 1; i <= period; i++)
            {
                var change = candles[i].Close - candles[i - 1].Close;
                if (change > 0) gain += change;
                else loss -= change;
            }

            gain /= period;
            loss /= period;

            decimal rs = loss == 0 ? 0 : gain / loss;
            result[period] = 100 - 100 / (1 + rs);

            for (int i = period + 1; i < candles.Count; i++)
            {
                var change = candles[i].Close - candles[i - 1].Close;

                if (change > 0)
                {
                    gain = (gain * (period - 1) + change) / period;
                    loss = (loss * (period - 1)) / period;
                }
                else
                {
                    gain = (gain * (period - 1)) / period;
                    loss = (loss * (period - 1) - change) / period;
                }

                rs = loss == 0 ? 0 : gain / loss;
                result[i] = 100 - 100 / (1 + rs);
            }

            return result;
        }

        public (decimal[] macd, decimal[] signal, decimal[] hist) Macd(IReadOnlyList<Candle> candles, int fastPeriod, int slowPeriod, int signalPeriod)
        {
            var fastEma = Ema(candles, fastPeriod);
            var slowEma = Ema(candles, slowPeriod);

            var macd = new decimal[candles.Count];
            for (int i = 0; i < candles.Count; i++)
                macd[i] = fastEma[i] - slowEma[i];

            var macdCandles = macd.Select((v, i) => new Candle { Close = v, OpenTime = candles[i].OpenTime }).ToList();

            var signal = Ema(macdCandles, signalPeriod);

            var hist = new decimal[candles.Count];
            for (int i = 0; i < candles.Count; i++)
                hist[i] = macd[i] - signal[i];

            return (macd, signal, hist);
        }
    }
}
