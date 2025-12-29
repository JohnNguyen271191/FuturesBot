using FuturesBot.Models;
using static FuturesBot.Utils.EnumTypesHelper;

namespace FuturesBot.IServices
{
    /// <summary>
    /// Futures trading gateway (orders + futures account).
    /// Separate from Spot to keep domain models honest (ISP/LSP).
    /// </summary>
    public interface IFuturesExchangeService : IMarketDataService
    {
        Task<bool> PlaceFuturesOrderAsync(
            string symbol,
            SignalType side,
            decimal quantity,
            decimal entryPrice,
            decimal stopLoss,
            decimal takeProfit,
            int leverage,
            Services.SlackNotifierService slackNotifierService,
            bool marketOrder = true);

        Task<FuturesPosition> GetPositionAsync(string symbol);
        Task<UserTradeInfo?> GetLastUserTradeAsync(string symbol, DateTime since);
        Task<bool> HasOpenPositionOrOrderAsync(string symbol);
        Task CancelAllOpenOrdersAsync(string symbol);
        Task ClosePositionAsync(string symbol, decimal quantity);

        Task<IReadOnlyList<OpenOrderInfo>> GetOpenOrdersAsync(string symbol);
        Task<bool> PlaceStopOnlyAsync(string symbol, string side, string positionSide, decimal quantity, decimal stopPrice);
        Task CancelStopLossOrdersAsync(string symbol);
        Task<bool> PlaceTakeProfitAsync(string symbol, string positionSide, decimal qty, decimal takeProfitPrice);
        Task<bool> HasTakeProfitOrderAsync(string symbol);
        Task<IReadOnlyList<OpenOrderInfo>> GetOpenAlgoOrdersAsync(string symbol);
        Task<NetPnlResult> GetNetPnlAsync(string symbol, DateTime fromUtc, DateTime? toUtc = null);
        Task<decimal> GetCommissionFromUserTradesAsync(string symbol, DateTime fromUtc, DateTime toUtc);
    }
}
