using FuturesBot.Config;
using FuturesBot.Models;

namespace FuturesBot.IServices
{
    public interface IStrategyService
    {
        TradeSignal GenerateSignal(IReadOnlyList<Candle> candles15m, IReadOnlyList<Candle> candles1h, CoinInfo coinInfo);
    }
}
