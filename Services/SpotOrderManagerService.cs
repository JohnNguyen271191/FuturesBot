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
    /// FIX (OMS v2):
    /// 1) "inPosition" không chỉ dựa vào holdingNotional >= minHolding (tránh chặn oan vì dust / OCO lock).
    ///    - Nếu có SELL open orders => coi như inPosition.
    ///    - Nếu chỉ dust < minHolding => vẫn cho phép entry (tùy config, mặc định allow).
    /// 2) Pending BUY TTL: tránh kẹt maker limit nhiều ngày.
    /// 3) Log WHY-NOT-TRADE có throttle: biết chính xác vì sao không vào.
    /// 4) Reject qty/minNotional/rounding: log rõ và không cooldown oan.
    /// </summary>
    public sealed class SpotOrderManagerService
    {
        private readonly ISpotExchangeService _spot;
        private readonly SlackNotifierService _notify;
        private readonly BotConfig _config;

        // per-symbol throttle action
        private readonly ConcurrentDictionary<string, DateTime> _lastActionUtc = new();

        // per-symbol throttle WHY log
        private readonly ConcurrentDictionary<string, DateTime> _lastWhyLogUtc = new();

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

            // Fetch holdings
            var quoteHold = await _spot.GetHoldingAsync(quoteAsset);
            var baseHold = await _spot.GetHoldingAsync(baseAsset);

            var baseQtyTotal = GetTotalQty(baseHold);
            var holdingNotional = baseQtyTotal * price;

            var minHolding = _config.SpotOms.MinHoldingNotionalUsd;
            var hasDust = holdingNotional > 0m && holdingNotional < minHolding;

            // Fetch open orders once per tick (OMS decisions depend on it)
            var openOrders = await _spot.GetOpenOrdersAsync(symbol) ?? Array.Empty<OpenOrderInfo>();

            // ============ Determine inPosition more robust ============
            // If we have SELL open orders (OCO/TP/SL), treat as "inPosition" even if Free=0 because Locked.
            bool hasAnySellExit = openOrders.Any(o =>
                string.Equals(o.Side, "SELL", StringComparison.OrdinalIgnoreCase));

            // If base holding is meaningful (>= minHolding notional), treat as inPosition.
            bool meaningfulHolding = holdingNotional >= minHolding;

            // If base qty is "tradeable size" even when notional < minHolding (some exchanges filter), treat as position.
            // (Simple heuristic: if baseQtyTotal is non-trivial and there are SELL exits -> inPosition already)
            bool inPosition = meaningfulHolding || hasAnySellExit;

            // ============================================================
            // 1) Rescue OCO: holding but no open orders
            // ============================================================
            if (inPosition)
            {
                if (openOrders.Count == 0)
                {
                    if (IsThrottled(symbol))
                    {
                        LogWhyThrottled(symbol, "RescueOCO throttled");
                        return;
                    }

                    // Default OCO based on current price
                    var tp = price * (1m + _config.SpotOms.DefaultTakeProfitPercent);
                    var sl = price * (1m - _config.SpotOms.DefaultStopLossPercent);
                    var stopLimit = sl * (1m + _config.SpotOms.SlMakerBufferPercent);

                    // No open orders -> Free is safe to use (locked should be 0 here)
                    var qty = baseHold.Free;
                    if (qty > 0)
                    {
                        await _spot.PlaceOcoSellAsync(symbol, qty, tp, sl, stopLimit);
                        await _notify.SendAsync(
                            $"[SPOT][OMS] Rescue OCO placed {symbol}. qty={qty:0.########}, tp={tp:0.##}, sl={sl:0.##}");
                        MarkAction(symbol);
                    }
                    else
                    {
                        LogWhy(symbol, $"RescueOCO skipped: baseFree=0 but inPosition inferred. holdingNotional≈{holdingNotional:0.##} dust={hasDust}");
                    }
                }
            }

            if (signal == null || signal.Type == SignalType.None)
            {
                // Optional: debug why not trade (only if you want)
                return;
            }

            // ============================================================
            // 2) Exit (SHORT signal) - long-only spot
            // ============================================================
            if (signal.Type == SignalType.Short)
            {
                if (!inPosition)
                {
                    LogWhy(symbol, $"Exit skipped: not inPosition. (reason={signal.Reason})");
                    return;
                }
                if (IsThrottled(symbol))
                {
                    LogWhyThrottled(symbol, $"Exit throttled. (reason={signal.Reason})");
                    return;
                }

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
                else
                {
                    LogWhy(symbol, $"Exit skipped: baseFree=0 after cancel. (reason={signal.Reason})");
                }

                _entryRepriceCount.TryRemove(symbol, out _);
                return;
            }

            // ============================================================
            // 3) Entry (LONG signal)
            // ============================================================
            if (signal.Type == SignalType.Long)
            {
                // If meaningful holding or has exit orders => do not re-enter
                // BUT if it's just dust (and NO sell exits), allow entry (don't block for days).
                if (inPosition)
                {
                    // allow entry if only dust AND no sell exits
                    if (!(hasDust && !hasAnySellExit))
                    {
                        LogWhy(symbol, $"Entry skipped: inPosition. holdingNotional≈{holdingNotional:0.##} minHolding={minHolding:0.##} hasSellExit={hasAnySellExit} dust={hasDust} (reason={signal.Reason})");
                        return;
                    }

                    // dust but inPosition inferred by sell exits shouldn't happen due to condition above
                    // still safe
                }

                if (IsThrottled(symbol))
                {
                    LogWhyThrottled(symbol, $"Entry throttled. (reason={signal.Reason})");
                    return;
                }

                // If dust exists + there are any sell exits, cancel them to avoid locking + conflicts
                // (rare, but prevents "dust + exit orders" causing perpetual inPosition)
                if (hasDust && hasAnySellExit)
                {
                    await _spot.CancelAllOpenOrdersAsync(symbol);
                    await _notify.SendAsync($"[SPOT][OMS] Dust cleanup: canceled exits for {symbol} before new entry. dustNotional≈{holdingNotional:0.##}");
                    // refresh
                    openOrders = await _spot.GetOpenOrdersAsync(symbol) ?? Array.Empty<OpenOrderInfo>();
                }

                // --------------- Sizing ---------------
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
                if (usdToUse <= 0)
                {
                    LogWhy(symbol, $"Entry skipped: usdToUse<=0 (usableTotal={usableTotal:0.##}, alloc={allocation:0.##}%, risk={riskPct:0.##}%)");
                    return;
                }

                var minEntry = _config.SpotOms.MinEntryNotionalUsd;
                if (usdToUse < minEntry)
                {
                    await _notify.SendAsync(
                        $"[SPOT] Skip BUY {symbol}: budget too small {usdToUse:0.##} < minEntry {minEntry:0.##}. " +
                        $"(usableTotal={usableTotal:0.##}, alloc={allocation:0.##}%, risk={riskPct:0.##}%, dust={hasDust})");
                    return;
                }

                // --------------- Pending maker buy TTL ---------------
                var pendingBuy = openOrders.FirstOrDefault(o =>
                    string.Equals(o.Side, "BUY", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(o.Type, "LIMIT", StringComparison.OrdinalIgnoreCase));

                if (pendingBuy != null)
                {
                    // If still fresh, don't spam cancel/replace
                    var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var ageSec = 0L;

                    if (pendingBuy.TimeMs > 0)
                        ageSec = (nowMs - pendingBuy.TimeMs) / 1000;

                    // If too young -> skip
                    if (ageSec > 0 && ageSec < _config.SpotOms.EntryRepriceSeconds)
                    {
                        LogWhy(symbol, $"Entry skipped: pending BUY age={ageSec}s < repriceSec={_config.SpotOms.EntryRepriceSeconds}s (reason={signal.Reason})");
                        return;
                    }

                    // Cancel stale pending
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

                // --------------- Place maker limit buy ---------------
                var entryPrice = price * (1m - _config.SpotOms.EntryMakerOffsetPercent);
                var rawQty = usdToUse / entryPrice;

                // Optional preflight sanity (avoid silent reject spam)
                if (rawQty <= 0m)
                {
                    LogWhy(symbol, $"Entry skipped: rawQty<=0. usdToUse={usdToUse:0.##} entryPrice={entryPrice:0.##}");
                    return;
                }

                var buy = await _spot.PlaceLimitBuyAsync(symbol, rawQty, entryPrice);
                if (buy.OrderId == "REJECTED" || buy.OrderId == "UNKNOWN")
                {
                    // Important: do NOT MarkAction -> avoid cooldown on reject
                    LogWhy(symbol, $"Entry REJECTED: {symbol} rawQty={rawQty:0.########} price={entryPrice:0.##} spend≈{usdToUse:0.##} (reason={signal.Reason})");
                    return;
                }

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

        private void LogWhyThrottled(string symbol, string msg)
        {
            // throttle why logs to avoid spam
            var everySec = Math.Max(10, _config.SpotOms.MinSecondsBetweenActions);
            if (_lastWhyLogUtc.TryGetValue(symbol, out var last))
            {
                if ((DateTime.UtcNow - last) < TimeSpan.FromSeconds(everySec))
                    return;
            }
            _lastWhyLogUtc[symbol] = DateTime.UtcNow;

            _ = SafeNotifyAsync($"[SPOT][WHY] {symbol} {msg}");
        }

        private void LogWhy(string symbol, string msg)
        {
            // throttle why logs (fixed 30s)
            const int whyEverySec = 30;
            if (_lastWhyLogUtc.TryGetValue(symbol, out var last))
            {
                if ((DateTime.UtcNow - last) < TimeSpan.FromSeconds(whyEverySec))
                    return;
            }
            _lastWhyLogUtc[symbol] = DateTime.UtcNow;

            _ = SafeNotifyAsync($"[SPOT][WHY] {symbol} {msg}");
        }

        private async Task SafeNotifyAsync(string msg)
        {
            try { await _notify.SendAsync(msg); } catch { /* ignore */ }
        }

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