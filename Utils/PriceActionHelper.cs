using FuturesBot.Models;

namespace FuturesBot.Utils
{
    public static class PriceActionHelper
    {
        public static decimal FindSwingLow(IReadOnlyList<Candle> candles, int endIndex, int lookback = 5)
        {
            if (candles.Count == 0) return 0;

            int start = Math.Max(0, endIndex - lookback + 1);
            decimal low = candles[start].Low;

            for (int i = start + 1; i <= endIndex; i++)
            {
                if (candles[i].Low < low)
                    low = candles[i].Low;
            }

            return low;
        }

        public static decimal FindSwingHigh(IReadOnlyList<Candle> candles, int endIndex, int lookback = 5)
        {
            if (candles.Count == 0) return 0;

            int start = Math.Max(0, endIndex - lookback + 1);
            decimal high = candles[start].High;

            for (int i = start + 1; i <= endIndex; i++)
            {
                if (candles[i].High > high)
                    high = candles[i].High;
            }

            return high;
        }

        public static decimal AverageVolume(IReadOnlyList<Candle> candles, int endIndex, int length)
        {
            if (candles.Count == 0 || endIndex <= 0) return 0;

            int start = Math.Max(0, endIndex - length + 1);
            int count = endIndex - start + 1;

            decimal sum = 0;
            for (int i = start; i <= endIndex; i++)
                sum += candles[i].Volume;

            return count > 0 ? sum / count : 0;
        }
    }
}
