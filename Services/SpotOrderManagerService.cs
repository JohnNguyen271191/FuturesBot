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
    /// FIX:
    /// 1) Holding calc uses Free + Locked (SpotHolding) to avoid state bugs when OCO locks coins.
    /// 2) Maker miss fill: reprice loop + MARKET fallback using PlaceMarketBuyByQuoteAsync after too many reprices.
    /// 3) Exit: cancel orders first, refresh holding, then sell.
    /// </summary>
    public sealed class SpotOrderManagerService
    {
        private readonly ISpotExchangeService _spot;
        private readonly SlackNotifierService _notify;
        private readonly BotConfig _config;

        // per-symbol throttle
        private readonly ConcurrentDictionary<string, DateTime> _lastActionUtc = new();

        // track reprice count to avoid infinite maker loop
        private readonly ConcurrentDictionary<string, int> _entryRepriceCount = new();
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
                var qtyTotal = GetTotalQty(baseHold);
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
            if (price <= 0) return;

            var quoteHold = await _spot.GetHoldingAsync(quoteAsset);
            var baseHold = await _spot.GetHoldingAsync(baseAsset);

            var baseQtyTotal = GetTotalQty(baseHold);
            var holdingNotional = baseQtyTotal * price;

            var minHolding = _config.SpotOms.MinHoldingNotionalUsd;
            var inPosition = holdingNotional >= minHolding;
            var hasDust = holdingNotional > 0m && holdingNotional < minHolding;

            // ============================================================
            // 1) Rescue OCO: holding but no open orders
            // ============================================================
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

                    // No open orders -> Free is safe to use
                    var qty = baseHold.Free;
                    if (qty > 0)
                    {
                        await _spot.PlaceOcoSellAsync(symbol, qty, tp, sl, stopLimit);
                        await _notify.SendAsync(
                            $"[SPOT][OMS] Rescue OCO placed {symbol}. qty={qty:0.########}, tp={tp:0.##}, sl={sl:0.##}");
                        MarkAction(symbol);
                    }
                }
            }

            if (signal == null || signal.Type == SignalType.None)
                return;

            // ============================================================
            // 2) Exit (SHORT signal)
            // ============================================================
            if (signal.Type == SignalType.Short)
            {
                if (!inPosition) return;
                if (IsThrottled(symbol)) return;

                await _spot.CancelAllOpenOrdersAsync(symbol);

                // refresh holding after cancel (release locked)
                baseHold = await _spot.GetHoldingAsync(baseAsset);

                var sellQty = baseHold.Free;
                if (sellQty > 0)
                {
                    await _spot.PlaceSpotOrderAsync(symbol, SignalType.Short, sellQty);
                    await _notify.SendAsync(
                        $"[SPOT][OMS] Exit SELL {symbol}. qty={sellQty:0.########} (reason={signal.Reason})");
                    MarkAction(symbol);
                }

                _entryRepriceCount.TryRemove(symbol, out _);
                return;
            }

            // ============================================================
            // 3) Entry (LONG signal)
            // ============================================================
            if (signal.Type == SignalType.Long)
            {
                if (inPosition) return;
                if (IsThrottled(symbol)) return;

                // Sizing
                var totalCap = _config.Spot.WalletCapUsd > 0 ? _config.Spot.WalletCapUsd : quoteHold.Free;
                var usableTotal = Math.Min(quoteHold.Free, totalCap);

                var allocation = coinInfo.AllocationPercent > 0 ? coinInfo.AllocationPercent : 100m;
                var coinCap = usableTotal * allocation / 100m;

                var riskPct = coinInfo.RiskPerTradePercent > 0
                    ? coinInfo.RiskPerTradePercent
                    : (_config.Spot.DefaultRiskPerTradePercent > 0 ? _config.Spot.DefaultRiskPerTradePercent : 10m);

                var usdToUse = Math.Max(0m, coinCap * (riskPct / 100m));

                // buffer
                var buffer = _config.SpotOms.EntryQuoteBufferPercent;
                usdToUse *= (1m - buffer);

                // FDUSD 2 decimals
                usdToUse = Math.Floor(usdToUse * 100m) / 100m;
                if (usdToUse <= 0) return;

                var minEntry = _config.SpotOms.MinEntryNotionalUsd;
                if (usdToUse < minEntry)
                {
                    await _notify.SendAsync(
                        $"[SPOT] Skip BUY {symbol}: budget too small {usdToUse:0.##} < minEntry {minEntry:0.##}. " +
                        $"(usableTotal={usableTotal:0.##}, alloc={allocation:0.##}%, risk={riskPct:0.##}%, dust={hasDust})");
                    return;
                }

                // pending maker buy?
                var openOrders = await _spot.GetOpenOrdersAsync(symbol) ?? Array.Empty<OpenOrderInfo>();
                var pendingBuy = openOrders.FirstOrDefault(o =>
                    string.Equals(o.Side, "BUY", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(o.Type, "LIMIT", StringComparison.OrdinalIgnoreCase));

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

                    var count = _entryRepriceCount.AddOrUpdate(symbol, 1, (_, c) => c + 1);

                    // Market fallback to guarantee fill
                    if (count > MaxRepriceBeforeMarketFallback)
                    {
                        var mk = await _spot.PlaceMarketBuyByQuoteAsync(symbol, usdToUse);

                        await _notify.SendAsync(
                            $"[SPOT][OMS] MARKET fallback BUY {symbol} after {count - 1} reprices. " +
                            $"spend≈{usdToUse:0.##} {quoteAsset}, lastPrice={price:0.##}, orderId={mk.OrderId}, dust={hasDust}. (reason={signal.Reason})");

                        MarkAction(symbol);
                        _entryRepriceCount.TryRemove(symbol, out _);
                        return;
                    }

                    await _notify.SendAsync(
                        $"[SPOT][OMS] Reprice pending BUY {symbol} after {_config.SpotOms.EntryRepriceSeconds}s. (repriceCount={count})");
                }
                else
                {
                    _entryRepriceCount.TryRemove(symbol, out _);
                }

                // place maker limit buy
                var entryPrice = price * (1m - _config.SpotOms.EntryMakerOffsetPercent);
                var rawQty = usdToUse / entryPrice;

                var buy = await _spot.PlaceLimitBuyAsync(symbol, rawQty, entryPrice);
                if (buy.OrderId == "REJECTED" || buy.OrderId == "UNKNOWN")
                    return;

                await _notify.SendAsync(
                    $"[SPOT][OMS] Maker BUY LIMIT {symbol}: spend≈{usdToUse:0.##} {quoteAsset}, price={entryPrice:0.##}, rawQty={rawQty:0.########}, orderId={buy.OrderId}, dust={hasDust}. (reason={signal.Reason})");

                MarkAction(symbol);
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

        private static decimal GetTotalQty(SpotHolding h)
        {
            if (h == null) return 0m;
            return h.Free + h.Locked;
        }

        private static string GuessBaseAsset(string symbol)
        {
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
