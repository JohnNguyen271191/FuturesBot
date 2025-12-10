using FuturesBot.Models;
using FuturesBot.Services;
using static FuturesBot.Utils.EnumTypesHelper;

namespace FuturesBot.IServices
{
    public interface IExchangeClientService
    {
        Task<IReadOnlyList<Candle>> GetRecentCandlesAsync(string symbol, string interval, int limit = 200);

        Task<bool> PlaceFuturesOrderAsync(string symbol, SignalType side, decimal quantity, decimal entryPrice, decimal stopLoss, decimal takeProfit, int leverage, SlackNotifierService slackNotifierService, bool marketOrder = true);


        Task<PositionInfo> GetPositionAsync(string symbol);

        Task<UserTradeInfo?> GetLastUserTradeAsync(string symbol, DateTime since);

        Task<bool> HasOpenPositionOrOrderAsync(string symbol);

        Task CancelAllOpenOrdersAsync(string symbol);

        Task ClosePositionAsync(string symbol, decimal quantity);

        Task<IReadOnlyList<OpenOrderInfo>> GetOpenOrdersAsync(string symbol);

        Task<bool> PlaceStopOnlyAsync(string symbol, string side, string positionSide, decimal quantity, decimal stopPrice);
        Task<NetPnlResult> GetNetPnlAsync(string symbol, DateTime fromUtc, DateTime? toUtc = null);
        Task CancelStopLossOrdersAsync(string symbol);
        Task<bool> PlaceTakeProfitAsync(string symbol, string positionSide, decimal qty, decimal takeProfitPrice);
        Task<bool> HasTakeProfitOrderAsync(string symbol);
        Task<IReadOnlyList<OpenOrderInfo>> GetOpenAlgoOrdersAsync(string symbol);
    }
}
