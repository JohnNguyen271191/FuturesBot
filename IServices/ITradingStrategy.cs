using FuturesBot.Config;
using FuturesBot.Models;

namespace FuturesBot.IServices
{
    /// <summary>
    /// Market-agnostic strategy contract.
    /// </summary>
    public interface ITradingStrategy
    {
        TradeSignal GenerateSignal(IReadOnlyList<Candle> candlesMain, IReadOnlyList<Candle> candlesTrend, CoinInfo coinInfo);
    }

    /// <summary>
    /// Futures strategy marker.
    /// </summary>
    public interface IFuturesTradingStrategy : ITradingStrategy { }

    /// <summary>
    /// Spot strategy marker.
    /// </summary>
    public interface ISpotTradingStrategy : ITradingStrategy { }
}
