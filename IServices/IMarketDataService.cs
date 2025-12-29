using FuturesBot.Models;

namespace FuturesBot.IServices
{
    /// <summary>
    /// Market data access shared by Spot/Futures.
    /// Kept small on purpose (Interface Segregation Principle).
    /// </summary>
    public interface IMarketDataService
    {
        Task<IReadOnlyList<Candle>> GetRecentCandlesAsync(string symbol, string interval, int limit = 200);
    }
}
