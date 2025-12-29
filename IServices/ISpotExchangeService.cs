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
        /// <summary>Get holding for an asset (e.g., FDUSD, BTC).</summary>
        Task<SpotHolding> GetHoldingAsync(string asset);

        /// <summary>Get last traded price for symbol (e.g., BTCFDUSD).</summary>
        Task<decimal> GetLastPriceAsync(string symbol);

        /// <summary>
        /// Place a spot order.
        /// - SELL quantity is base asset qty.
        /// - BUY quantity is base asset qty (use PlaceMarketBuyByQuoteAsync for quote-based MARKET BUY).
        /// </summary>
        Task<SpotOrderResult> PlaceSpotOrderAsync(string symbol, SignalType side, decimal quantity, decimal? limitPrice = null);

        /// <summary>
        /// MARKET BUY using quoteOrderQty (recommended for "MAX" entries and to avoid qty rounding to zero).
        /// quoteOrderQty is in quote asset (e.g., FDUSD).
        /// </summary>
        Task<SpotOrderResult> PlaceMarketBuyByQuoteAsync(string symbol, decimal quoteOrderQty);


        /// <summary>
        /// Place a BUY LIMIT order (intended to be maker by pricing below the current ask).
        /// </summary>
        Task<SpotOrderResult> PlaceLimitBuyAsync(string symbol, decimal quantity, decimal price);

        /// <summary>
        /// Place an OCO SELL order (take-profit LIMIT + stop-loss STOP_LIMIT).
        /// </summary>
        Task<string> PlaceOcoSellAsync(string symbol, decimal quantity, decimal takeProfitPrice, decimal stopPrice, decimal stopLimitPrice);

        Task CancelAllOpenOrdersAsync(string symbol);
        Task<IReadOnlyList<OpenOrderInfo>> GetOpenOrdersAsync(string symbol);
    }
}
