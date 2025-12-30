using System;
using System.Collections.Concurrent;
using System.Text.Json;
using FuturesBot.Config;
using FuturesBot.IServices;
using FuturesBot.Models;
using static FuturesBot.Utils.EnumTypesHelper;

namespace FuturesBot.Services
{
    /// <summary>
    /// SpotOrderManagerService (OMS for SPOT) - NO-OCO (maker-first)
    ///
    /// Goals:
    /// - 1 symbol = 1 spot position at a time (no accidental DCA/double-entry)
    /// - Entry: maker-only (LIMIT_MAKER) with reprice timeout
    /// - TP: maker-only (LIMIT_MAKER) - maintain exactly 1 TP and auto-replace if too near/far
    /// - SL: Soft-SL maker-first (LIMIT_MAKER) -> fallback MARKET SELL (safety)
    ///
    /// Notes:
    /// - Spot holdings include Free + Locked. If you lock base in TP order, Free may be 0; we MUST consider Locked.
    /// - We intentionally avoid OCO because stop legs are always taker and limit control is poor.
    /// </summary>
    public sealed class SpotOrderManagerService
    {
        private readonly ISpotExchangeService _spot;
        private readonly SlackNotifierService _notify;
        private readonly BotConfig _config;

        // throttle per-symbol actions
        private readonly ConcurrentDictionary<string, DateTime> _lastActionUtc = new();

        // per-symbol state (in-memory). We also double-check holdings from exchange to survive restarts.
        private readonly ConcurrentDictionary<string, SpotSymbolState> _state = new();

        // persisted state (for resume after restart)
        private readonly string _stateFilePath;
        private readonly object _stateFileLock = new();

        private sealed class SpotSymbolState
        {
            public decimal EntryRefPrice { get; set; }
            public DateTime EntrySeenUtc { get; set; } = DateTime.UtcNow;
            public DateTime LastExitUtc { get; set; } = DateTime.MinValue;

            public bool SoftExitInProgress { get; set; }

            public string? LastTpOrderId { get; set; }
            public decimal LastTpPrice { get; set; }
            public DateTime LastTpCheckUtc { get; set; } = DateTime.MinValue;

            // for OPEN/CLOSE transition notify
            public bool WasInPosition { get; set; }
        }

        private sealed class PersistedSpotState
        {
            public string Symbol { get; set; } = string.Empty;
            public decimal EntryRefPrice { get; set; }
            public DateTime PositionOpenedUtc { get; set; }
        }

        public SpotOrderManagerService(ISpotExchangeService spot, SlackNotifierService notify, BotConfig config)
        {
            _spot = spot;
            _notify = notify;
            _config = config;

            _stateFilePath = Path.Combine(AppContext.BaseDirectory, "spot_resume_state.json");
        }

        // ----------------------- Persist helpers -----------------------

        private Dictionary<string, PersistedSpotState> LoadPersistedUnsafe()
        {
            // caller must lock(_stateFileLock)
            try
            {
                if (!File.Exists(_stateFilePath)) return new();
                var json = File.ReadAllText(_stateFilePath);
                var list = JsonSerializer.Deserialize<List<PersistedSpotState>>(json) ?? new();
                return list
                    .Where(x => !string.IsNullOrWhiteSpace(x.Symbol))
                    .ToDictionary(x => x.Symbol, x => x, StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new();
            }
        }

        private void SavePersistedUnsafe(Dictionary<string, PersistedSpotState> map)
        {
            // caller must lock(_stateFileLock)
            try
            {
                var list = map.Values.ToList();
                var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_stateFilePath, json);
            }
            catch
            {
                // ignore
            }
        }

        private void UpsertPersisted(string symbol, decimal entryRefPrice, DateTime openedUtc)
        {
            lock (_stateFileLock)
            {
                var map = LoadPersistedUnsafe();
                map[symbol] = new PersistedSpotState
                {
                    Symbol = symbol,
                    EntryRefPrice = entryRefPrice,
                    PositionOpenedUtc = openedUtc
                };
                SavePersistedUnsafe(map);
            }
        }

        private void RemovePersisted(string symbol)
        {
            lock (_stateFileLock)
            {
                var map = LoadPersistedUnsafe();
                if (map.Remove(symbol))
                    SavePersistedUnsafe(map);
            }
        }

        // ----------------------- Public APIs -----------------------

        /// <summary>
        /// Resume positions after restart:
        /// - detect holdings from exchange (Free + Locked)
        /// - hydrate EntryRefPrice from persisted file (or lastPrice fallback)
        /// - lock WasInPosition = true to prevent accidental re-entry
        /// </summary>
        public async Task RecoverAsync(IEnumerable<CoinInfo> spotCoins, CancellationToken ct)
        {
            Dictionary<string, PersistedSpotState> persisted;
            lock (_stateFileLock) persisted = LoadPersistedUnsafe();

            foreach (var coin in spotCoins)
            {
                ct.ThrowIfCancellationRequested();

                var symbol = coin.Symbol;
                var baseAsset = GuessBaseAsset(symbol);
                var quoteAsset = _config.SpotQuoteAsset;

                var lastPrice = await _spot.GetLastPriceAsync(symbol);
                if (lastPrice <= 0) continue;

                var baseHold = await _spot.GetHoldingAsync(baseAsset);
                var baseTotal = baseHold.Free + baseHold.Locked;
                var holdingNotional = baseTotal * lastPrice;

                if (holdingNotional < _config.SpotOms.MinHoldingNotionalUsd)
                    continue;

                var st = _state.GetOrAdd(symbol, _ => new SpotSymbolState());

                // hydrate from persisted if any
                if (persisted.TryGetValue(symbol, out var ps) && ps.EntryRefPrice > 0)
                {
                    st.EntryRefPrice = ps.EntryRefPrice;
                    st.EntrySeenUtc = ps.PositionOpenedUtc == default ? DateTime.UtcNow : ps.PositionOpenedUtc;
                }
                else
                {
                    if (st.EntryRefPrice <= 0)
                    {
                        st.EntryRefPrice = lastPrice;
                        st.EntrySeenUtc = DateTime.UtcNow;
                    }
                    UpsertPersisted(symbol, st.EntryRefPrice, st.EntrySeenUtc);
                }

                st.WasInPosition = true;

                await _notify.SendAsync(
                    $"[SPOT][RESUME] {symbol} qty={baseTotal} entryRef≈{st.EntryRefPrice} quote={quoteAsset}"
                );
            }
        }

        public async Task TickAsync(TradeSignal? signal, CoinInfo coinInfo, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var symbol = coinInfo.Symbol;
            var quoteAsset = _config.SpotQuoteAsset;
            var baseAsset = GuessBaseAsset(symbol);

            // Ensure state exists early (we use it for transition notify)
            var st = _state.GetOrAdd(symbol, _ => new SpotSymbolState());

            var lastPrice = await _spot.GetLastPriceAsync(symbol);
            if (lastPrice <= 0)
                return;

            var quoteHold = await _spot.GetHoldingAsync(quoteAsset);
            var baseHold = await _spot.GetHoldingAsync(baseAsset);

            // IMPORTANT: include Locked. TP orders lock base and Free becomes 0.
            var baseTotal = baseHold.Free + baseHold.Locked;
            var holdingNotional = baseTotal * lastPrice;
            var inPosition = holdingNotional >= _config.SpotOms.MinHoldingNotionalUsd;

            // Pull open orders once per tick
            var open = await _spot.GetOpenOrdersAsync(symbol);

            // Pending entry exists?
            var pendingBuy = open.FirstOrDefault(o => o.Side == "BUY" && (o.Type == "LIMIT" || o.Type == "LIMIT_MAKER"));
            var hasPendingEntry = pendingBuy != null;

            // ====== Resume/Close transition detection (Slack) ======
            if (st.WasInPosition && !inPosition)
            {
                var note = st.SoftExitInProgress ? "SL/EXIT" : "TP/UNKNOWN";
                await _notify.SendAsync($"[SPOT][CLOSE] {symbol} closed (note={note}).");

                st.WasInPosition = false;
                st.EntryRefPrice = 0m;
                st.SoftExitInProgress = false;
                st.LastTpOrderId = null;
                st.LastTpPrice = 0m;
                RemovePersisted(symbol);
            }
            else if (!st.WasInPosition && inPosition)
            {
                st.WasInPosition = true;

                if (st.EntryRefPrice <= 0)
                {
                    st.EntryRefPrice = lastPrice;
                    st.EntrySeenUtc = DateTime.UtcNow;
                    UpsertPersisted(symbol, st.EntryRefPrice, st.EntrySeenUtc);
                }

                await _notify.SendAsync($"[SPOT][OPEN] {symbol} holdingNotional={holdingNotional:0.##}, entryRef≈{st.EntryRefPrice}");
            }

            // ------------------- ENTRY PATH (only if NOT holding) -------------------
            if (!inPosition)
            {
                // No holding => allow entry logic (but block if pending entry exists)
                st.SoftExitInProgress = false;

                // If there is a pending BUY LIMIT/LIMIT_MAKER, wait then cancel & reprice.
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
                    MarkAction(symbol);
                    return;
                }

                // Only enter when signal says LONG
                if (signal == null || signal.Type != SignalType.Long)
                    return;

                // Throttle
                if (IsThrottled(symbol))
                    return;

                // Use quote balance * risk% as spend (works for FDUSD)
                var riskPct = coinInfo.RiskPerTradePercent > 0 ? coinInfo.RiskPerTradePercent : 1m;
                var usdToUse = quoteHold.Free * (riskPct / 100m);

                // Safety clamp: ensure positive and enough
                if (usdToUse <= 0)
                    return;

                // Determine maker entry price (below current)
                var (bestBid, bestAsk) = await _spot.GetBestBidAskAsync(symbol);
                var entryPrice = lastPrice * (1m - _config.SpotOms.EntryMakerOffsetPercent);

                // ensure non-marketable buy (try keep below ask)
                if (bestAsk > 0 && entryPrice >= bestAsk)
                    entryPrice = bestBid > 0 ? bestBid * (1m - _config.SpotOms.EntryMakerOffsetPercent) : entryPrice;

                if (entryPrice <= 0)
                    return;

                var rawQty = usdToUse / entryPrice;
                var buy = await _spot.PlaceLimitMakerAsync(symbol, SignalType.Long, rawQty, entryPrice);

                if (buy.OrderId == "REJECTED" || buy.OrderId == "UNKNOWN" || buy.OrderId == "REJECT_QTY")
                    return;

                await _notify.SendAsync($"[SPOT][OMS] Maker BUY LIMIT_MAKER {symbol}: spend≈{usdToUse:0.####} {quoteAsset}, price={entryPrice:0.########}, rawQty={rawQty:0.########}, orderId={buy.OrderId}");
                MarkAction(symbol);
                return;
            }

            // ------------------- IN-POSITION PATH -------------------

            // block accidental entry while holding
            if (hasPendingEntry)
            {
                // Just in case: if you see a BUY while holding, cancel it.
                await _spot.CancelAllOpenOrdersAsync(symbol);
                await _notify.SendAsync($"[SPOT][GUARD] {symbol} holding but has pending BUY -> cancelled.");
                MarkAction(symbol);
            }

            // Initialize entry reference once when holding
            if (st.EntryRefPrice <= 0)
            {
                var refPx = signal?.EntryPrice > 0 ? signal!.EntryPrice : lastPrice;
                st.EntryRefPrice = refPx.GetValueOrDefault();
                st.EntrySeenUtc = DateTime.UtcNow;
                UpsertPersisted(symbol, st.EntryRefPrice, st.EntrySeenUtc);
            }

            // ====== Exit triggers ======
            // (You can keep using signal.Short as "exit hint" if your pipeline does that)
            var exitHint = signal?.Type == SignalType.Short;

            var stopRef = st.EntryRefPrice * (1m - _config.SpotOms.DefaultStopLossPercent);
            var stopHit = lastPrice <= stopRef;

            var timeStopHit = _config.SpotOms.TimeStopSeconds > 0 &&
                              (DateTime.UtcNow - st.EntrySeenUtc).TotalSeconds >= _config.SpotOms.TimeStopSeconds;

            if ((exitHint || stopHit || timeStopHit) && !st.SoftExitInProgress)
            {
                if (IsThrottled(symbol))
                    return;

                st.SoftExitInProgress = true;

                var reason = exitHint ? "EXIT_HINT" : (stopHit ? "STOP_HIT" : "TIME_STOP");
                await ExitViaSoftMakerAsync(symbol, lastPrice, stopRef, reason, ct);

                // refresh holding to see if closed
                baseHold = await _spot.GetHoldingAsync(baseAsset);
                baseTotal = baseHold.Free + baseHold.Locked;
                holdingNotional = baseTotal * lastPrice;
                inPosition = holdingNotional >= _config.SpotOms.MinHoldingNotionalUsd;

                if (!inPosition)
                {
                    st.LastExitUtc = DateTime.UtcNow;
                    st.SoftExitInProgress = false;
                    st.EntryRefPrice = 0m;
                    st.LastTpOrderId = null;
                    st.LastTpPrice = 0m;
                    RemovePersisted(symbol);
                }

                MarkAction(symbol);
                return;
            }

            // ====== TP management (maker) ======
            if (_config.SpotOms.MaintainMakerTakeProfit && !st.SoftExitInProgress)
            {
                if (DateTime.UtcNow - st.LastTpCheckUtc >= TimeSpan.FromSeconds(Math.Max(1, _config.SpotOms.TpRecheckSeconds)))
                {
                    st.LastTpCheckUtc = DateTime.UtcNow;

                    // Maintain exactly ONE TP SELL LIMIT/LIMIT_MAKER
                    var sellLimit = open
                        .Where(o => o.Side == "SELL" && (o.Type == "LIMIT" || o.Type == "LIMIT_MAKER"))
                        .OrderByDescending(o => o.Price)
                        .FirstOrDefault();

                    var baseFree = baseHold.Free; // TP uses Free base
                    var hasBaseToSell = baseFree > 0;

                    if (sellLimit == null)
                    {
                        if (hasBaseToSell && !IsThrottled(symbol))
                        {
                            var entryRef = st.EntryRefPrice > 0 ? st.EntryRefPrice : lastPrice;
                            var tpPrice = entryRef * (1m + _config.SpotOms.DefaultTakeProfitPercent);

                            var tp = await _spot.PlaceLimitMakerAsync(symbol, SignalType.Short, baseFree, tpPrice);
                            if (!string.IsNullOrWhiteSpace(tp.OrderId) && tp.OrderId != "REJECTED" && tp.OrderId != "REJECT_QTY")
                            {
                                st.LastTpOrderId = tp.OrderId;
                                st.LastTpPrice = tpPrice;
                                await _notify.SendAsync($"[SPOT][TP] Place TP LIMIT_MAKER {symbol} qty={baseFree:0.########}, price={tpPrice:0.########}, orderId={tp.OrderId}");
                                MarkAction(symbol);
                            }
                        }
                    }
                    else
                    {
                        st.LastTpOrderId = sellLimit.OrderId;
                        st.LastTpPrice = sellLimit.Price;

                        // distance in percent
                        var dist = (sellLimit.Price > 0 && lastPrice > 0)
                            ? (sellLimit.Price - lastPrice) / lastPrice
                            : 0m;

                        var tooClose = dist > 0 && dist < _config.SpotOms.TpMinDistancePercent;
                        var tooFar = dist > _config.SpotOms.TpMaxDistancePercent;

                        if ((tooClose || tooFar) && hasBaseToSell && !IsThrottled(symbol))
                        {
                            await _spot.CancelAllOpenOrdersAsync(symbol);

                            // refresh after cancel unlocks base
                            baseHold = await _spot.GetHoldingAsync(baseAsset);
                            baseFree = baseHold.Free;

                            if (baseFree > 0)
                            {
                                var entryRef = st.EntryRefPrice > 0 ? st.EntryRefPrice : lastPrice;
                                var newTpPrice = entryRef * (1m + _config.SpotOms.DefaultTakeProfitPercent);

                                var tp2 = await _spot.PlaceLimitMakerAsync(symbol, SignalType.Short, baseFree, newTpPrice);
                                if (!string.IsNullOrWhiteSpace(tp2.OrderId) && tp2.OrderId != "REJECTED" && tp2.OrderId != "REJECT_QTY")
                                {
                                    st.LastTpOrderId = tp2.OrderId;
                                    st.LastTpPrice = newTpPrice;
                                    await _notify.SendAsync($"[SPOT][TP] Replace TP {symbol} old={sellLimit.Price:0.########} (dist={dist:P2}) -> new={newTpPrice:0.########} qty={baseFree:0.########} orderId={tp2.OrderId}");
                                    MarkAction(symbol);
                                }
                            }
                        }
                    }
                }
            }
        }

        // ------------------- Exit helpers -------------------

        private async Task ExitViaSoftMakerAsync(string symbol, decimal lastPrice, decimal stopRef, string reason, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            // Cancel all open orders to free locked base (e.g., TP).
            await _spot.CancelAllOpenOrdersAsync(symbol);

            // Refresh holdings after cancel
            var baseAsset = GuessBaseAsset(symbol);
            var hold = await _spot.GetHoldingAsync(baseAsset);
            var qty = hold.Free + hold.Locked;
            if (qty <= 0)
                return;

            // If price is already far below stop, skip soft maker to avoid missing exit.
            var worseThanStopBy = stopRef > 0 ? (stopRef - lastPrice) / stopRef : 0m;
            if (worseThanStopBy >= _config.SpotOms.SoftSlSkipIfWorseThanStopByPercent)
            {
                var mkt = await _spot.PlaceSpotOrderAsync(symbol, SignalType.Short, qty); // MARKET SELL
                var avg = (mkt.ExecutedQty > 0 && mkt.CummulativeQuoteQty > 0) ? (mkt.CummulativeQuoteQty / mkt.ExecutedQty) : lastPrice;
                await _notify.SendAsync($"[SPOT][SL][MARKET] {symbol} qty={qty:0.########}, avg={avg:0.########}, reason={reason} (skip soft: worseBy={worseThanStopBy:P2})");
                return;
            }

            // Place maker SELL closer to bid (inside spread) to increase fill probability while remaining maker.
            var (bid, ask) = await _spot.GetBestBidAskAsync(symbol);
            var spread = Math.Max(0m, ask - bid);

            var ratio = _config.SpotOms.SoftSlInsideSpreadRatio;
            if (ratio < 0m) ratio = 0m;
            if (ratio > 1m) ratio = 1m;

            var inside = bid + spread * ratio;

            // must be > bid to be maker; LIMIT_MAKER will reject if marketable
            var makerPrice = spread > 0 ? inside : (lastPrice * (1m + _config.SpotOms.SoftSlMakerOffsetPercent));
            if (makerPrice <= bid)
                makerPrice = ask > bid ? ask : (bid * (1m + _config.SpotOms.SoftSlMakerOffsetPercent));

            var soft = await _spot.PlaceLimitMakerAsync(symbol, SignalType.Short, qty, makerPrice);
            if (soft.OrderId == "REJECTED" || soft.OrderId == "UNKNOWN")
            {
                // If LIMIT_MAKER rejected, fallback immediately.
                await _spot.PlaceSpotOrderAsync(symbol, SignalType.Short, qty);
                await _notify.SendAsync($"[SPOT][SL][MARKET] {symbol} qty={qty:0.########}, reason={reason} (soft rejected)");
                return;
            }

            await _notify.SendAsync($"[SPOT][SL][SOFT] {symbol} LIMIT_MAKER placed qty={qty:0.########}, price={makerPrice:0.########}, reason={reason}");

            // Wait then check if still holding
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _config.SpotOms.SoftSlWaitSeconds)), ct);

            hold = await _spot.GetHoldingAsync(baseAsset);
            qty = hold.Free + hold.Locked;

            if (qty * lastPrice < _config.SpotOms.MinHoldingNotionalUsd)
            {
                await _notify.SendAsync($"[SPOT][SL][SOFT] {symbol} filled, reason={reason}");
                return;
            }

            // Still holding => try cancel soft order then market sell
            try
            {
                var status = await _spot.GetOrderStatusAsync(symbol, soft.OrderId);
                if (!string.Equals(status.Status, "FILLED", StringComparison.OrdinalIgnoreCase))
                {
                    await _spot.CancelOrderAsync(symbol, soft.OrderId);
                }
            }
            catch
            {
                // ignore and proceed
            }

            // Refresh qty again
            hold = await _spot.GetHoldingAsync(baseAsset);
            qty = hold.Free + hold.Locked;
            if (qty <= 0)
                return;

            await _spot.CancelAllOpenOrdersAsync(symbol);
            var mkt2 = await _spot.PlaceSpotOrderAsync(symbol, SignalType.Short, qty);

            var avg2 = (mkt2.ExecutedQty > 0 && mkt2.CummulativeQuoteQty > 0) ? (mkt2.CummulativeQuoteQty / mkt2.ExecutedQty) : lastPrice;
            await _notify.SendAsync($"[SPOT][SL][MARKET] {symbol} qty={qty:0.########}, avg={avg2:0.########}, reason={reason} (fallback after soft wait)");
        }

        // ------------------- Throttle helpers -------------------

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

        // ------------------- Symbol helpers -------------------

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