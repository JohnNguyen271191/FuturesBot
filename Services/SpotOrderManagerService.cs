using System;
using System.Collections.Concurrent;
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
    /// Spot OMS - NO OCO + Monitor exits (maker-first)
    /// - Entry: maker BUY limit
    /// - Exit: maker SELL limit, reprice TTL
    /// - Monitor: EMA34/89 + RSI + ATR trail
    /// </summary>
    public sealed class SpotOrderManagerService
    {
        private readonly ISpotExchangeService _spot;
        private readonly SlackNotifierService _notify;
        private readonly BotConfig _config;
        private readonly IndicatorService _indicators;

        private readonly ConcurrentDictionary<string, DateTime> _lastActionUtc = new();
        private readonly ConcurrentDictionary<string, DateTime> _lastWhyLogUtc = new();
        private readonly ConcurrentDictionary<string, int> _entryRepriceCount = new();

        // Cache "entry intent" so maker-chasing (cancel/replace) can continue without needing the strategy
        // to re-signal every time.
        private sealed class EntryIntent
        {
            public string QuoteAsset = "";
            public decimal UsdToUse;
            public decimal RawQty;
            public decimal FirstPrice;
            public decimal LastPrice;
            public DateTime CreatedUtc;
            public string Reason = "";
        }

        private readonly ConcurrentDictionary<string, EntryIntent> _entryIntent = new(StringComparer.OrdinalIgnoreCase);

        // ===== Exit monitor state =====
        private sealed class PosState
        {
            public bool Active;
            public decimal Anchor;     // best-effort anchor
            public decimal Peak;
            public decimal Trail;      // dynamic trail stop (price)
            public int SellRepriceCount;
        }

        private readonly ConcurrentDictionary<string, PosState> _state = new(StringComparer.OrdinalIgnoreCase);

        // ===== Tunables (safe defaults) =====
        private const int BarsForMonitorMin = 120;

        // maker placement offsets (fallback if config is missing)
        private const decimal MakerSellOffsetFallback = 0.0006m;   // +0.06% from last price
        // resolved from config (dynamic), fallback if not set
        private decimal MakerSellOffset => (_config?.SpotOms?.EntryMakerOffsetPercent > 0m
            ? _config.SpotOms.EntryMakerOffsetPercent
            : MakerSellOffsetFallback);

        // trailing logic (ATR-based)
        private const decimal AtrTrailMult = 1.2m;         // trail = peak - ATR*mult
        private const decimal AtrSoftStopMult = 1.6m;      // soft stop from anchor

        // RSI/EMA exit
        private const decimal ExitRsiWeak = 43m;
        private const decimal EmaBreakTol = 0.0006m;

        // pending TTL seconds (reuse config EntryRepriceSeconds)
        // NOTE: Spot is maker-only by design. No market fallback.

        public SpotOrderManagerService(
            ISpotExchangeService spot,
            SlackNotifierService notify,
            BotConfig config,
            IndicatorService indicators)
        {
            _spot = spot;
            _notify = notify;
            _config = config;
            _indicators = indicators;
        }

        /// <summary>
        /// Tick with candles so OMS can monitor exits by market structure.
        /// </summary>
        public async Task TickAsync(TradeSignal? signal, CoinInfo coinInfo, IReadOnlyList<Candle> candlesMain, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var symbol = coinInfo.Symbol;

            var omsCfg = _config.Spot?.Oms ?? _config.SpotOms;

            var quoteAsset = (_config.Spot != null && !string.IsNullOrWhiteSpace(_config.Spot.QuoteAsset))
    ? _config.Spot.QuoteAsset
    : _config.SpotQuoteAsset;

            var baseAsset = GuessBaseAsset(symbol);

            var price = await _spot.GetLastPriceAsync(symbol);
            if (price <= 0) return;

            var quoteHold = await _spot.GetHoldingAsync(quoteAsset);
            var baseHold = await _spot.GetHoldingAsync(baseAsset);

            var baseQtyTotal = baseHold.Free + baseHold.Locked;
            var holdingNotional = baseQtyTotal * price;

            var minHolding = omsCfg.MinHoldingNotionalUsd;
            bool inPosition = holdingNotional >= minHolding;

            var openOrders = await _spot.GetOpenOrdersAsync(symbol) ?? Array.Empty<OpenOrderInfo>();

            var pendingBuy = openOrders.FirstOrDefault(o =>
                o.Side.Equals("BUY", StringComparison.OrdinalIgnoreCase) &&
                (o.Type.Equals("LIMIT", StringComparison.OrdinalIgnoreCase) || o.Type.Equals("LIMIT_MAKER", StringComparison.OrdinalIgnoreCase)));

            var pendingSell = openOrders.FirstOrDefault(o =>
                o.Side.Equals("SELL", StringComparison.OrdinalIgnoreCase) &&
                (o.Type.Equals("LIMIT", StringComparison.OrdinalIgnoreCase) || o.Type.Equals("LIMIT_MAKER", StringComparison.OrdinalIgnoreCase)));

            // ===== 1) If inPosition -> monitor exits (ignore entry signals) =====
            if (inPosition)
            {
                // clear cached intent once we are in position
                _entryIntent.TryRemove(symbol, out _);
                _entryRepriceCount.TryRemove(symbol, out _);

                await MonitorExitAsync(symbol, coinInfo, candlesMain, price, baseHold, pendingSell, signal, ct);
                return;
            }

            // If not inPosition: clear state
            if (_state.TryGetValue(symbol, out var st0)) st0.Active = false;

            // ===== 2) If pending buy exists -> manage TTL =====
            if (pendingBuy != null)
            {
                await HandlePendingBuyAsync(symbol, quoteAsset, pendingBuy, quoteHold.Free, price, omsCfg, ct);
                return;
            }

            // ignore SHORT when no position
            if (signal != null && signal.Type == SignalType.Short)
                return;

            if (signal == null || signal.Type == SignalType.None)
                return;

            if (signal.Type != SignalType.Long)
                return;

            // ===== 3) Entry maker BUY limit =====
            if (IsThrottled(symbol))
            {
                LogWhyThrottled(symbol, $"Entry throttled. (reason={signal.Reason})");
                return;
            }

            var totalCap = _config.Spot.WalletCapUsd > 0 ? _config.Spot.WalletCapUsd : quoteHold.Free;
            var usableTotal = Math.Min(quoteHold.Free, totalCap);

            var allocation = coinInfo.AllocationPercent > 0 ? coinInfo.AllocationPercent : 100m;
            var coinCap = usableTotal * allocation / 100m;

            var riskPct = coinInfo.RiskPerTradePercent > 0
                ? coinInfo.RiskPerTradePercent
                : (_config.Spot.DefaultRiskPerTradePercent > 0 ? _config.Spot.DefaultRiskPerTradePercent : 10m);

            var usdToUse = Math.Max(0m, coinCap * (riskPct / 100m));
            usdToUse *= (1m - omsCfg.EntryQuoteBufferPercent);
            usdToUse = Math.Floor(usdToUse * 100m) / 100m;

            if (usdToUse <= 0m)
            {
                LogWhy(symbol, $"Entry skipped: usdToUse<=0 (usableTotal={usableTotal:0.##}, alloc={allocation:0.##}%, risk={riskPct:0.##}%)");
                return;
            }

            if (usdToUse < omsCfg.MinEntryNotionalUsd)
            {
                await _notify.SendAsync($"[SPOT] Skip BUY {symbol}: budget {usdToUse:0.##} < minEntry {omsCfg.MinEntryNotionalUsd:0.##}");
                return;
            }

            var baseOffset = omsCfg.EntryMakerOffsetPercent > 0 ? omsCfg.EntryMakerOffsetPercent : 0.0003m;
            var entryPrice = price * (1m - baseOffset);
            var rawQty = usdToUse / entryPrice;

            if (rawQty <= 0m)
            {
                LogWhy(symbol, $"Entry skipped: rawQty<=0 usdToUse={usdToUse:0.##} entryPrice={entryPrice:0.##}");
                return;
            }

            // cache intent for maker-chasing
            _entryIntent[symbol] = new EntryIntent
            {
                QuoteAsset = quoteAsset,
                UsdToUse = usdToUse,
                RawQty = rawQty,
                FirstPrice = entryPrice,
                LastPrice = entryPrice,
                CreatedUtc = DateTime.UtcNow,
                Reason = signal.Reason ?? ""
            };

            var buy = await PlaceMakerBuyWithRetriesAsync(symbol, rawQty, entryPrice, omsCfg, ct);
            if (buy.OrderId == "REJECTED" || buy.OrderId == "UNKNOWN")
            {
                LogWhy(symbol, $"Entry REJECTED: rawQty={rawQty:0.########} price={entryPrice:0.##} spend≈{usdToUse:0.##} (reason={signal.Reason})");
                _entryIntent.TryRemove(symbol, out _);
                return;
            }

            await _notify.SendAsync($"[SPOT][OMS] Maker BUY {symbol}: spend≈{usdToUse:0.##} {quoteAsset}, price={entryPrice:0.##}, qty≈{rawQty:0.########}, id={buy.OrderId}. (reason={signal.Reason})");
            MarkAction(symbol);
        }

        private async Task<SpotOrderResult> PlaceMakerBuyWithRetriesAsync(string symbol, decimal qty, decimal price, SpotOmsConfig omsCfg, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            // If LIMIT_MAKER rejects (because it would match immediately), step price a bit lower and retry.
            int maxTry = 3;
            var p = price;
            for (int k = 0; k < maxTry; k++)
            {
                var res = await _spot.PlaceLimitBuyAsync(symbol, qty, p);
                if (res.OrderId != "REJECTED" && res.OrderId != "UNKNOWN")
                    return res;

                // step lower by min offset so it's clearly maker
                var step = Math.Max(0.00005m, omsCfg.EntryMinMakerOffsetPercent / 2m);
                p = p * (1m - step);
            }

            return new SpotOrderResult { OrderId = "REJECTED", RawStatus = "REJECTED" };
        }

        // ============================================================
        // Monitor exit by EMA/RSI/ATR + maker sell reprice TTL
        // ============================================================
        private async Task MonitorExitAsync(
            string symbol,
            CoinInfo coinInfo,
            IReadOnlyList<Candle> candlesMain,
            decimal lastPrice,
            SpotHolding baseHold,
            OpenOrderInfo? pendingSell,
            TradeSignal? signal,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var qtyTotal = baseHold.Free + baseHold.Locked;
            if (qtyTotal <= 0m) return;

            if (candlesMain == null || candlesMain.Count < BarsForMonitorMin)
            {
                // if no candles, still can exit on strategy Short
                if (signal != null && signal.Type == SignalType.Short)
                    await EnsureMakerSellAsync(symbol, qtyTotal, lastPrice, pendingSell, "SIG_SHORT(noCandles)", ct);

                return;
            }

            int i = candlesMain.Count - 2;
            if (i < 50) return;

            var ema34 = _indicators.Ema(candlesMain, 34);
            var ema89 = _indicators.Ema(candlesMain, 89);
            var rsi = _indicators.Rsi(candlesMain, 14);

            var c0 = candlesMain[i];
            var c1 = candlesMain[i - 1];

            var e34 = ema34[i];
            var e89 = ema89[i];
            var r = rsi[i];

            decimal atr = ComputeAtr(candlesMain, 14);

            // attach state
            var st = _state.GetOrAdd(symbol, _ => new PosState());
            if (!st.Active)
            {
                st.Active = true;
                st.Anchor = lastPrice; // best-effort
                st.Peak = lastPrice;
                st.Trail = atr > 0 ? (st.Peak - atr * AtrTrailMult) : lastPrice * (1m - 0.0075m);
                st.SellRepriceCount = 0;

                await _notify.SendAsync($"[SPOT][MON] attach {symbol} qty={qtyTotal:0.########} anchor≈{st.Anchor:0.##}");
            }

            st.Peak = Math.Max(st.Peak, lastPrice);

            // ATR trail (dynamic)
            if (atr > 0m)
            {
                var newTrail = st.Peak - atr * AtrTrailMult;
                if (newTrail > st.Trail) st.Trail = newTrail;
            }

            // exit rules:
            bool exitBySignal = signal != null && signal.Type == SignalType.Short;

            bool closeBelow34 = (e34 > 0m) && (c0.Close < e34 * (1m - EmaBreakTol));
            bool closeBelow89 = (e89 > 0m) && (c0.Close < e89 * (1m - EmaBreakTol));
            bool rsiWeak = r <= ExitRsiWeak;

            bool exitByEmaRsi = (closeBelow34 && rsiWeak) || closeBelow89;
            bool exitByTrail = lastPrice <= st.Trail;

            bool softStop = false;
            if (atr > 0m && st.Anchor > 0m)
                softStop = lastPrice <= (st.Anchor - atr * AtrSoftStopMult);

            bool shouldExit = exitBySignal || exitByEmaRsi || exitByTrail || softStop;

            if (!shouldExit)
                return;

            var reason = BuildExitReason(exitBySignal, exitByEmaRsi, exitByTrail, softStop, r, e34, e89, st.Trail, signal);

            await EnsureMakerSellAsync(symbol, qtyTotal, lastPrice, pendingSell, reason, ct);
        }

        private async Task EnsureMakerSellAsync(string symbol, decimal qtyTotal, decimal lastPrice, OpenOrderInfo? pendingSell, string reason, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            // if pending sell exists -> TTL reprice
            if (pendingSell != null)
            {
                await HandlePendingSellAsync(symbol, qtyTotal, lastPrice, pendingSell, reason, ct);
                return;
            }

            if (IsThrottled(symbol))
            {
                LogWhyThrottled(symbol, $"Exit throttled. reason={reason}");
                return;
            }

            var sellPrice = lastPrice * (1m + MakerSellOffset);

            var sell = await _spot.PlaceLimitSellAsync(symbol, qtyTotal, sellPrice);
            if (sell.OrderId == "REJECTED" || sell.OrderId == "UNKNOWN")
            {
                LogWhy(symbol, $"Exit SELL REJECTED: qty={qtyTotal:0.########} price={sellPrice:0.##} reason={reason}");
                return;
            }

            await _notify.SendAsync($"[SPOT][EXIT] Maker SELL {symbol}: qty={qtyTotal:0.########} price={sellPrice:0.##} reason={reason}");
            MarkAction(symbol);
        }

        private async Task HandlePendingSellAsync(string symbol, decimal qtyTotal, decimal lastPrice, OpenOrderInfo pendingSell, string reason, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long ageSec = 0;
            if (pendingSell.TimeMs > 0) ageSec = (nowMs - pendingSell.TimeMs) / 1000;

            var repriceSec = Math.Max(5, _config.SpotOms.EntryRepriceSeconds);

            if (ageSec > 0 && ageSec < repriceSec)
            {
                LogWhy(symbol, $"Pending SELL wait age={ageSec}s < {repriceSec}s reason={reason}");
                return;
            }

            await _spot.CancelAllOpenOrdersAsync(symbol);

            var st = _state.GetOrAdd(symbol, _ => new PosState());
            st.SellRepriceCount++;

            if (st.SellRepriceCount > 8)
            {
                await _notify.SendAsync($"[SPOT][EXIT] {symbol} SELL reprice too many ({st.SellRepriceCount}) -> keep maker-only wait.");
                st.SellRepriceCount = 8;
                return;
            }

            var sellPrice = lastPrice * (1m + MakerSellOffset);
            var sell = await _spot.PlaceLimitSellAsync(symbol, qtyTotal, sellPrice);

            if (sell.OrderId == "REJECTED" || sell.OrderId == "UNKNOWN")
            {
                LogWhy(symbol, $"Reprice SELL REJECTED: qty={qtyTotal:0.########} price={sellPrice:0.##} reason={reason}");
                return;
            }

            await _notify.SendAsync($"[SPOT][EXIT] Reprice maker SELL {symbol}: qty={qtyTotal:0.########} price={sellPrice:0.##} age={ageSec}s count={st.SellRepriceCount} reason={reason}");
            MarkAction(symbol);
        }

        private async Task HandlePendingBuyAsync(string symbol, string quoteAsset, OpenOrderInfo pendingBuy, decimal quoteFree, decimal lastPrice, SpotOmsConfig omsCfg, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long ageSec = 0;
            if (pendingBuy.TimeMs > 0) ageSec = (nowMs - pendingBuy.TimeMs) / 1000;

            var repriceSec = Math.Max(5, _config.SpotOms.EntryRepriceSeconds);

            if (ageSec > 0 && ageSec < repriceSec)
            {
                LogWhy(symbol, $"Pending BUY wait age={ageSec}s < {repriceSec}s");
                return;
            }

            var count = _entryRepriceCount.AddOrUpdate(symbol, 1, (_, c) => c + 1);

            if (!_entryIntent.TryGetValue(symbol, out var intent))
            {
                // No cached intent -> safest is cancel and wait.
                await _spot.CancelAllOpenOrdersAsync(symbol);
                await _notify.SendAsync($"[SPOT][OMS] {symbol} pending BUY stale canceled (no cached intent). age={ageSec}s");
                _entryRepriceCount.TryRemove(symbol, out _);
                MarkAction(symbol);
                return;
            }

            if (count > Math.Max(1, omsCfg.EntryMaxReprices))
            {
                await _spot.CancelAllOpenOrdersAsync(symbol);
                await _notify.SendAsync($"[SPOT][OMS] {symbol} maker-chase STOP (maxReprices={omsCfg.EntryMaxReprices}). Canceled pending BUY. age={ageSec}s");
                _entryIntent.TryRemove(symbol, out _);
                _entryRepriceCount.TryRemove(symbol, out _);
                MarkAction(symbol);
                return;
            }

            // cancel current pending and re-place a slightly more aggressive maker price
            await _spot.CancelAllOpenOrdersAsync(symbol);

            var baseOffset = omsCfg.EntryMakerOffsetPercent > 0 ? omsCfg.EntryMakerOffsetPercent : 0.0003m;
            var minOffset = omsCfg.EntryMinMakerOffsetPercent > 0 ? omsCfg.EntryMinMakerOffsetPercent : 0.00010m;

            // chase: gradually reduce offset towards minOffset
            var t = Math.Min(1m, (decimal)count / Math.Max(1, omsCfg.EntryMaxReprices));
            var offset = baseOffset - (baseOffset - minOffset) * t;
            offset = Math.Max(minOffset, offset);

            var newPrice = lastPrice * (1m - offset);

            // clamp by max chase distance from the first maker price
            var maxUp = intent.FirstPrice * (1m + Math.Max(0m, omsCfg.EntryMaxChaseDistancePercent));
            if (newPrice > maxUp) newPrice = maxUp;

            intent.LastPrice = newPrice;
            _entryIntent[symbol] = intent;

            var res = await PlaceMakerBuyWithRetriesAsync(symbol, intent.RawQty, newPrice, omsCfg, ct);
            if (res.OrderId == "REJECTED" || res.OrderId == "UNKNOWN")
            {
                await _notify.SendAsync($"[SPOT][OMS] {symbol} maker-chase REJECTED on reprice. count={count} newPrice={newPrice:0.##}");
                return;
            }

            await _notify.SendAsync($"[SPOT][OMS] {symbol} maker-chase reprice#{count}/{omsCfg.EntryMaxReprices}: price={newPrice:0.##}, qty≈{intent.RawQty:0.########}");
            MarkAction(symbol);
        }

        private static string BuildExitReason(bool sig, bool emaRsi, bool trail, bool softStop, decimal rsi, decimal e34, decimal e89, decimal trailPrice, TradeSignal? signal)
        {
            var parts = new System.Collections.Generic.List<string>();
            if (sig) parts.Add("SIG");
            if (emaRsi) parts.Add("EMA_RSI");
            if (trail) parts.Add("TRAIL");
            if (softStop) parts.Add("SOFTSTOP");

            var core = parts.Count == 0 ? "NA" : string.Join("+", parts);
            core += $" rsi={rsi:F1} e34={e34:0.##} e89={e89:0.##} trail={trailPrice:0.##}";
            if (sig && !string.IsNullOrWhiteSpace(signal?.Reason)) core += $" ({signal!.Reason})";
            return core;
        }

        private static decimal ComputeAtr(IReadOnlyList<Candle> candles, int period)
        {
            if (candles == null || candles.Count < period + 2) return 0m;
            int end = candles.Count - 2;
            int start = Math.Max(1, end - period + 1);

            decimal sum = 0m;
            int n = 0;

            for (int i = start; i <= end; i++)
            {
                var c = candles[i];
                var prev = candles[i - 1];
                var tr = Math.Max(
                    c.High - c.Low,
                    Math.Max(Math.Abs(c.High - prev.Close), Math.Abs(c.Low - prev.Close))
                );
                sum += tr;
                n++;
            }

            return n > 0 ? sum / n : 0m;
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
            try { await _notify.SendAsync(msg); } catch { }
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