using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    /// FIXES:
    /// 1) Holdings: use Free + Locked (best-effort via reflection) to avoid state bugs with OCO locking.
    /// 2) Entry maker miss: reprice count + optional fallback to MARKET after too many reprices.
    /// 3) Safer cancel: only cancel when necessary, and refresh holdings after cancel before selling.
    /// 4) Add missing usings + null-safe handling for open orders list.
    /// </summary>
    public sealed class SpotOrderManagerService
    {
        private readonly ISpotExchangeService _spot;
        private readonly SlackNotifierService _notify;
        private readonly BotConfig _config;

        // per-symbol throttle
        private readonly ConcurrentDictionary<string, DateTime> _lastActionUtc = new();

        // entry reprice tracking (to avoid infinite maker miss loop)
        private readonly ConcurrentDictionary<string, int> _entryRepriceCount = new();

        // If maker keeps missing, after N reprices we fallback to market (or aggressive limit via PlaceSpotOrderAsync)
        private const int MaxRepriceBeforeMarketFallback = 2;

        public SpotOrderManagerService(
            ISpotExchangeService spot,
            SlackNotifierService notify,
            BotConfig config)
        {
            _spot = spot;
            _notify = notify;
            _config = config;
        }

        /// <summary>
        /// Resume after restart (best-effort): if holdings exist, notify Slack.
        /// The current simple OMS doesn't persist state; it relies on exchange holdings + open orders.
        /// </summary>
        public async Task RecoverAsync(IEnumerable<CoinInfo> spotCoins, CancellationToken ct)
        {
            foreach (var coin in spotCoins)
            {
                ct.ThrowIfCancellationRequested();

                var symbol = coin.Symbol;
                var baseAsset = GuessBaseAsset(symbol);
                var quoteAsset = !string.IsNullOrWhiteSpace(_config.Spot.QuoteAsset)
                    ? _config.Spot.QuoteAsset
                    : _config.SpotQuoteAsset;

                var price = await _spot.GetLastPriceAsync(symbol);
                if (price <= 0) continue;

                var baseHold = await _spot.GetHoldingAsync(baseAsset);
                var qtyTotal = GetHoldingTotalQty(baseHold);
                var notional = qtyTotal * price;

                if (notional >= _config.SpotOms.MinHoldingNotionalUsd)
                {
                    await _notify.SendAsync(
                        $"[SPOT][RESUME] {symbol} holdingQty(total)={qtyTotal:0.########} notional≈{notional:0.##} quote={quoteAsset}");
                }
            }
        }

        public async Task TickAsync(TradeSignal? signal, CoinInfo coinInfo, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var symbol = coinInfo.Symbol;
            var quoteAsset = !string.IsNullOrWhiteSpace(_config.Spot.QuoteAsset)
                ? _config.Spot.QuoteAsset
                : _config.SpotQuoteAsset;
            var baseAsset = GuessBaseAsset(symbol);

            var price = await _spot.GetLastPriceAsync(symbol);
            if (price <= 0)
                return;

            var quote = await _spot.GetHoldingAsync(quoteAsset);
            var baseHold = await _spot.GetHoldingAsync(baseAsset);

            var baseQtyTotal = GetHoldingTotalQty(baseHold);
            var holdingNotional = baseQtyTotal * price;

            // Meaningful holding threshold
            var minHolding = _config.SpotOms.MinHoldingNotionalUsd;
            var inPosition = holdingNotional >= minHolding;

            // Treat dust as NOT in position for entry/exit logic
            var hasDust = holdingNotional > 0m && holdingNotional < minHolding;

            // 1) Rescue OCO if we are holding meaningfully but have no open orders.
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

                    // Use FREE qty for OCO placement (locked qty is already tied to other orders, but we have no open orders here)
                    var qty = baseHold.Free;
                    if (qty > 0)
                    {
                        await _spot.PlaceOcoSellAsync(symbol, qty, tp, sl, stopLimit);
                        await _notify.SendAsync($"[SPOT][OMS] Rescue OCO placed for {symbol}. qty={qty:0.########}, tp={tp:0.##}, sl={sl:0.##}");
                        MarkAction(symbol);
                    }
                }
            }

            if (signal == null || signal.Type == SignalType.None)
                return;

            // 2) Exit (SHORT signal means sell holdings; long-only spot)
            if (signal.Type == SignalType.Short)
            {
                // If only dust, ignore exit signal (optional: you can sell dust if you want)
                if (!inPosition)
                    return;

                if (IsThrottled(symbol))
                    return;

                await _spot.CancelAllOpenOrdersAsync(symbol);

                // refresh holding after cancel (OCO locks get released)
                baseHold = await _spot.GetHoldingAsync(baseAsset);

                var sellQty = baseHold.Free;
                if (sellQty > 0)
                {
                    await _spot.PlaceSpotOrderAsync(symbol, SignalType.Short, sellQty);
                    await _notify.SendAsync($"[SPOT][OMS] Exit SELL {symbol}. qty={sellQty:0.########} (reason={signal.Reason})");
                    MarkAction(symbol);
                }

                // reset entry reprice state after exit
                _entryRepriceCount.TryRemove(symbol, out _);

                return;
            }

            // 3) Entry (LONG signal)
            if (signal.Type == SignalType.Long)
            {
                // If meaningful position exists -> no new entry
                if (inPosition)
                    return;

                // If only dust exists, we still allow entry (won't get stuck by dust)
                // If you want to be stricter: return when hasDust == true.
                if (IsThrottled(symbol))
                    return;

                // ================================
                // Option A sizing:
                // - Total Spot cap: Spot.WalletCapUsd (in quote asset)
                // - Per-coin cap: totalCap * AllocationPercent
                // - Spend per entry: coinCap * Risk% (per trade)
                // ================================
                var totalCap = _config.Spot.WalletCapUsd > 0 ? _config.Spot.WalletCapUsd : quote.Free;
                var usableTotal = Math.Min(quote.Free, totalCap);

                var allocation = coinInfo.AllocationPercent > 0 ? coinInfo.AllocationPercent : 100m;
                var coinCap = usableTotal * allocation / 100m;

                var riskPct = coinInfo.RiskPerTradePercent > 0
                    ? coinInfo.RiskPerTradePercent
                    : (_config.Spot.DefaultRiskPerTradePercent > 0 ? _config.Spot.DefaultRiskPerTradePercent : 10m);

                var usdToUse = Math.Max(0m, coinCap * (riskPct / 100m));

                // apply quote buffer
                var buffer = _config.SpotOms.EntryQuoteBufferPercent > 0
                    ? _config.SpotOms.EntryQuoteBufferPercent
                    : _config.Spot.Oms.EntryQuoteBufferPercent;

                usdToUse = usdToUse * (1m - buffer);

                // Binance spot rejects quoteOrderQty with too much precision. FDUSD uses 2 decimals.
                usdToUse = Math.Floor(usdToUse * 100m) / 100m;
                if (usdToUse <= 0)
                    return;

                var minEntry = _config.SpotOms.MinEntryNotionalUsd > 0
                    ? _config.SpotOms.MinEntryNotionalUsd
                    : _config.Spot.Oms.MinEntryNotionalUsd;

                if (usdToUse < minEntry)
                {
                    await _notify.SendAsync(
                        $"[SPOT] Skip BUY {symbol}: budget too small {usdToUse:0.##} < minEntryNotional {minEntry:0.##}. " +
                        $"(usableTotal={usableTotal:0.##}, alloc={allocation:0.##}%, risk={riskPct:0.##}%, dust={hasDust})");
                    return;
                }

                // Maker-only entry:
                // - If a pending BUY LIMIT exists and it's still fresh -> wait
                // - If it's older than EntryRepriceSeconds -> cancel & reprice
                // - After too many reprices -> fallback to MARKET (PlaceSpotOrderAsync) so you actually get filled
                var openOrders = await _spot.GetOpenOrdersAsync(symbol) ?? new List<SpotOrder>();
                var pendingBuy = openOrders.FirstOrDefault(o => string.Equals(o.Side, "BUY", StringComparison.OrdinalIgnoreCase)
                                                            && string.Equals(o.Type, "LIMIT", StringComparison.OrdinalIgnoreCase));

                if (pendingBuy != null)
                {
                    var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (pendingBuy.TimeMs > 0)
                    {
                        var ageSec = (nowMs - pendingBuy.TimeMs) / 1000;
                        if (ageSec < _config.SpotOms.EntryRepriceSeconds)
                            return;
                    }

                    // too old -> cancel & reprice
                    await _spot.CancelAllOpenOrdersAsync(symbol);

                    var count = _entryRepriceCount.AddOrUpdate(symbol, 1, (_, c) => c + 1);

                    // fallback to market after N reprices
                    if (count > MaxRepriceBeforeMarketFallback)
                    {
                        var marketQty = usdToUse / price;
                        var m = await _spot.PlaceSpotOrderAsync(symbol, SignalType.Long, marketQty);

                        await _notify.SendAsync(
                            $"[SPOT][OMS] MARKET fallback BUY {symbol} after {count - 1} reprices. " +
                            $"spend≈{usdToUse:0.##} {quoteAsset}, qty≈{marketQty:0.########}, lastPrice={price:0.##}, dust={hasDust}");

                        MarkAction(symbol);
                        _entryRepriceCount.TryRemove(symbol, out _);
                        return;
                    }

                    await _notify.SendAsync($"[SPOT][OMS] Reprice pending entry {symbol} after {_config.SpotOms.EntryRepriceSeconds}s. (repriceCount={count})");
                }
                else
                {
                    // No pending buy -> reset reprice count
                    _entryRepriceCount.TryRemove(symbol, out _);
                }

                var entryPrice = price * (1m - _config.SpotOms.EntryMakerOffsetPercent);
                var rawQty = usdToUse / entryPrice;

                var buy = await _spot.PlaceLimitBuyAsync(symbol, rawQty, entryPrice);
                if (buy.OrderId == "REJECTED" || buy.OrderId == "UNKNOWN")
                {
                    // PlaceLimitBuyAsync already validates minQty/minNotional and returns REJECTED when too small.
                    return;
                }

                await _notify.SendAsync(
                    $"[SPOT][OMS] Maker BUY LIMIT {symbol}: spend≈{usdToUse:0.##} {quoteAsset}, price={entryPrice:0.##}, rawQty={rawQty:0.########}, orderId={buy.OrderId}. " +
                    $"(offset={_config.SpotOms.EntryMakerOffsetPercent:0.#######}, dust={hasDust}, reason={signal.Reason})");

                MarkAction(symbol);

                // Exits will be placed by the Rescue-OCO block once entry is filled (inPosition becomes true).
                return;
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

        private static decimal GetHoldingTotalQty(AssetHolding holding)
        {
            if (holding == null) return 0m;

            // Best effort: prefer Free + Locked if Locked exists; otherwise Free only.
            var free = holding.Free;

            var lockedProp = holding.GetType().GetProperty("Locked");
            if (lockedProp != null && lockedProp.PropertyType == typeof(decimal))
            {
                var locked = (decimal)(lockedProp.GetValue(holding) ?? 0m);
                return free + locked;
            }

            return free;
        }

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
