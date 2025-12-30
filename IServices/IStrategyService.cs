using FuturesBot.Config;
using FuturesBot.Models;

namespace FuturesBot.IServices
{
    // Legacy-style strategy service (used by some older code paths).
    // We keep it FUTURES-only to avoid mixing assumptions.
    public interface IStrategyService
    {
        TradeSignal GenerateSignal(IReadOnlyList<Candle> candlesMain, IReadOnlyList<Candle> candlesTrend, FuturesCoinConfig coin);
    }
}
