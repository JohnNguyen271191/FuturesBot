using System;
using System.Linq;
using System.Collections.Concurrent;
using FuturesBot.Config;
using FuturesBot.IServices;
using FuturesBot.Models;
using static FuturesBot.Utils.EnumTypesHelper;

namespace FuturesBot.Services
{
    /// <summary>
    /// SpotOrderManagerService (OMS for SPOT)
    /// Responsibilities:
    /// - Execute entry (BUY) on LONG signals when no meaningful holdings.
    /// - Maintain exits via OCO (TP LIMIT + SL STOP_LIMIT).
    /// - Rescue: if holdings exist but no open orders, re-create a default OCO.
    /// - Exit on SHORT signal by canceling orders and selling holdings (long-only spot).
    ///
    /// NOTE: This is intentionally NOT the same as Futures OMS.
    /// Spot has holdings + open orders, not futures positions/leverage.
    /// </summary>
    public sealed class SpotOrderManagerService
    {
        private readonly ISpotExchangeService _spot;
        private readonly SlackNotifierService _notify;
        private readonly BotConfig _config;

        // per-symbol throttle
        private readonly ConcurrentDictionary<string, DateTime> _lastActionUtc = new();

        public SpotOrderManagerService(
            ISpotExchangeService spot,
            SlackNotifierService notify,
            BotConfig config)
        {
            _spot = spot;
            _notify = notify;
            _config = config;
        }

        public async Task TickAsync(TradeSignal? signal, CoinInfo coinInfo, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var symbol = coinInfo.Symbol;
            var quoteAsset = _config.SpotQuoteAsset;
            var baseAsset = GuessBaseAsset(symbol);

            var price = await _spot.GetLastPriceAsync(symbol);
            if (price <= 0)
                return;

            var quote = await _spot.GetHoldingAsync(quoteAsset);
            var baseHold = await _spot.GetHoldingAsync(baseAsset);

            var holdingNotional = baseHold.Free * price;
            var inPosition = holdingNotional >= _config.SpotOms.MinHoldingNotionalUsd;

            // 1) Rescue OCO if we are holding but have no open orders.
            // This protects against losing OCO due to manual cancels, API hiccups, etc.
            if (inPosition)
            {
                var open = await _spot.GetOpenOrdersAsync(symbol);
                if (open == null || open.Count == 0)
                {
                    if (IsThrottled(symbol))
                        return;

                    var tp = price * (1m + _config.SpotOms.DefaultTakeProfitPercent);
                    var sl = price * (1m - _config.SpotOms.DefaultStopLossPercent);
                    var stopLimit = sl * (1m + _config.SpotOms.SlMakerBufferPercent);

                    var qty = baseHold.Free;
                    if (qty > 0)
                    {
                        await _spot.PlaceOcoSellAsync(symbol, qty, tp, sl, stopLimit);
                        await _notify.SendAsync($"[SPOT][OMS] Rescue OCO placed for {symbol}. qty={qty}, tp={tp}, sl={sl}");
                        MarkAction(symbol);
                    }
                }
            }

            if (signal == null || signal.Type == SignalType.None)
                return;

            // 2) Exit (SHORT signal means sell holdings; long-only spot)
            if (signal.Type == SignalType.Short)
            {
                if (!inPosition)
                    return;

                if (IsThrottled(symbol))
                    return;

                await _spot.CancelAllOpenOrdersAsync(symbol);

                var sellQty = baseHold.Free;
                if (sellQty > 0)
                {
                    await _spot.PlaceSpotOrderAsync(symbol, SignalType.Short, sellQty);
                    await _notify.SendAsync($"[SPOT][OMS] Exit SELL {symbol}. qty={sellQty}");
                    MarkAction(symbol);
                }

                return;
            }

            // 3) Entry (LONG signal)
            if (signal.Type == SignalType.Long)
            {
                if (inPosition)
                    return;

                if (IsThrottled(symbol))
                    return;

                var usdToUse = Math.Max(0m, quote.Free * (1m - _config.SpotOms.EntryQuoteBufferPercent));
                // Binance spot rejects quoteOrderQty with too much precision. FDUSD uses 2 decimals.
                usdToUse = Math.Floor(usdToUse * 100m) / 100m;
                if (usdToUse <= 0)
                    return;

                if (usdToUse < _config.SpotOms.MinEntryNotionalUsd)
                {
                    await _notify.SendAsync($"[SPOT] Skip BUY {symbol}: quote too small {usdToUse} < minEntryNotional {_config.SpotOms.MinEntryNotionalUsd}");
                    return;
                }
                
                // Maker-only entry: place BUY LIMIT below last price so it does not execute immediately (maker).
                // If there is already a pending BUY LIMIT, wait up to EntryRepriceSeconds then cancel & reprice.
                var openOrders = await _spot.GetOpenOrdersAsync(symbol);
                var pendingBuy = openOrders.FirstOrDefault(o => o.Side == "BUY" && o.Type == "LIMIT");

                if (pendingBuy != null)
                {
                    var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (pendingBuy.TimeMs > 0)
                    {
                        var ageSec = (nowMs - pendingBuy.TimeMs) / 1000;
                        if (ageSec < _config.SpotOms.EntryRepriceSeconds)
                            return;
                    }

                    await _spot.CancelAllOpenOrdersAsync(symbol);
                    await _notify.SendAsync($"[SPOT][OMS] Reprice pending entry {symbol} after {_config.SpotOms.EntryRepriceSeconds}s.");
                }

                var entryPrice = price * (1m - _config.SpotOms.EntryMakerOffsetPercent);
                var rawQty = usdToUse / entryPrice;

                var buy = await _spot.PlaceLimitBuyAsync(symbol, rawQty, entryPrice);
                if (buy.OrderId == "REJECTED" || buy.OrderId == "UNKNOWN")
                {
                    // PlaceLimitBuyAsync already validates minQty/minNotional and returns REJECTED when too small.
                    return;
                }

                await _notify.SendAsync($"[SPOT][OMS] Maker BUY LIMIT {symbol}: spend≈{usdToUse} {quoteAsset}, price={entryPrice}, rawQty={rawQty}, orderId={buy.OrderId}");
                MarkAction(symbol);

                // Exits will be placed by the Rescue-OCO block once entry is filled (inPosition becomes true).
}
        }

        private bool IsThrottled(string symbol)
        {
            var min = TimeSpan.FromSeconds(Math.Max(1, _config.SpotOms.MinSecondsBetweenActions));
            if (_lastActionUtc.TryGetValue(symbol, out var last))
            {
                if (DateTime.UtcNow - last < min)
                    return true;
            }
            return false;
        }

        private void MarkAction(string symbol) => _lastActionUtc[symbol] = DateTime.UtcNow;

        private static string GuessBaseAsset(string symbol)
        {
            // Best-effort: common quote suffixes.
            var quotes = new[] { "USDT", "USDC", "BUSD", "FDUSD", "TUSD", "BTC", "ETH" };
            foreach (var q in quotes)
            {
                if (symbol.EndsWith(q, StringComparison.OrdinalIgnoreCase))
                    return symbol.Substring(0, symbol.Length - q.Length);
            }

            return symbol;
        }
    }
}
