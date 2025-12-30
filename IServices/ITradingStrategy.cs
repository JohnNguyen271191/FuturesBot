using FuturesBot.Config;
using FuturesBot.Models;

namespace FuturesBot.IServices
{
    public interface IFuturesTradingStrategy
    {
        TradeSignal GenerateSignal(IReadOnlyList<Candle> candlesMain, IReadOnlyList<Candle> candlesTrend, FuturesCoinConfig coin);
    }

    public interface ISpotTradingStrategy
    {
        TradeSignal GenerateSignal(IReadOnlyList<Candle> candlesMain, IReadOnlyList<Candle> candlesTrend, SpotCoinConfig coin);
    }
}
