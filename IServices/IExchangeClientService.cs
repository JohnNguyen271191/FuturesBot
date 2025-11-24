using FuturesBot.Models;
using static FuturesBot.Utils.EnumTypesHelper;

namespace FuturesBot.IServices
{
    public interface IExchangeClientService
    {
        Task<IReadOnlyList<Candle>> GetRecentCandlesAsync(string symbol, string interval, int limit = 200);

        Task PlaceFuturesOrderAsync(string symbol, SignalType side, decimal quantity, decimal entryPrice, decimal stopLoss, decimal takeProfit, int leverage, bool marketOrder = true);


        Task<PositionInfo> GetPositionAsync(string symbol);

        Task<UserTradeInfo?> GetLastUserTradeAsync(string symbol, DateTime since);

        Task<bool> HasOpenPositionOrOrderAsync(string symbol);
    }
}
