using FuturesBot.Models;
using static FuturesBot.Utils.EnumTypesHelper;

namespace FuturesBot.IServices
{
    /// <summary>
    /// Spot trading gateway (orders + spot account).
    /// Long-only unless you implement spot margin separately.
    /// </summary>
    public interface ISpotExchangeService : IMarketDataService
    {
        /// <summary>
        /// Get holding for a base asset (e.g., BTC, ETH).
        /// </summary>
        Task<SpotHolding> GetHoldingAsync(string asset);

        Task<decimal> GetLastPriceAsync(string symbol);

        /// <summary>
        /// Market or limit order. For SELL, quantity is in base asset.
        /// For BUY market, quantity is in base asset as well.
        /// </summary>
        Task<SpotOrderResult> PlaceSpotOrderAsync(string symbol, SignalType side, decimal quantity, decimal? limitPrice = null);

        /// <summary>
        /// Place an OCO SELL order (take-profit LIMIT + stop-loss STOP_LIMIT).
        /// </summary>
        /// <param name="symbol">Trading symbol, e.g., BTCUSDT</param>
        /// <param name="quantity">Quantity in base asset</param>
        /// <param name="takeProfitPrice">Take-profit LIMIT price</param>
        /// <param name="stopPrice">Stop trigger price</param>
        /// <param name="stopLimitPrice">Stop-limit price (usually slightly worse than stopPrice)</param>
        Task<string> PlaceOcoSellAsync(string symbol, decimal quantity, decimal takeProfitPrice, decimal stopPrice, decimal stopLimitPrice);

        Task CancelAllOpenOrdersAsync(string symbol);
        Task<IReadOnlyList<OpenOrderInfo>> GetOpenOrdersAsync(string symbol);
    }
}
