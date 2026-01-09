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

        // maker placement offsets
        private const decimal MakerSellOffset = 0.0006m;   // +0.06% from last price
        private const decimal MakerBuyOffset = 0.0006m;    // use config EntryMakerOffsetPercent for buy

        // trailing logic (ATR-based)
        private const decimal AtrTrailMult = 1.2m;         // trail = peak - ATR*mult
        private const decimal AtrSoftStopMult = 1.6m;      // soft stop from anchor

        // RSI/EMA exit
        private const decimal ExitRsiWeak = 43m;
        private const decimal EmaBreakTol = 0.0006m;

        // pending TTL seconds (reuse config EntryRepriceSeconds)
        private const int MaxEntryRepriceBeforeMarketFallback = 2; // if you want maker-only, set high

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

            var quoteAsset = !string.IsNullOrWhiteSpace(_config.Spot.QuoteAsset)
                ? _config.Spot.QuoteAsset
                : _config.SpotQuoteAsset;

            var baseAsset = GuessBaseAsset(symbol);

            var price = await _spot.GetLastPriceAsync(symbol);
            if (price <= 0) return;

            var quoteHold = await _spot.GetHoldingAsync(quoteAsset);
            var baseHold = await _spot.GetHoldingAsync(baseAsset);

            var baseQtyTotal = baseHold.Free + baseHold.Locked;
            var holdingNotional = baseQtyTotal * price;

            var minHolding = _config.SpotOms.MinHoldingNotionalUsd;
            bool inPosition = holdingNotional >= minHolding;

            var openOrders = await _spot.GetOpenOrdersAsync(symbol) ?? Array.Empty<OpenOrderInfo>();

            var pendingBuy = openOrders.FirstOrDefault(o =>
                o.Side.Equals("BUY", StringComparison.OrdinalIgnoreCase) &&
                o.Type.Equals("LIMIT", StringComparison.OrdinalIgnoreCase));

            var pendingSell = openOrders.FirstOrDefault(o =>
                o.Side.Equals("SELL", StringComparison.OrdinalIgnoreCase) &&
                o.Type.Equals("LIMIT", StringComparison.OrdinalIgnoreCase));

            // ===== 1) If inPosition -> monitor exits (ignore entry signals) =====
            if (inPosition)
            {
                await MonitorExitAsync(symbol, coinInfo, candlesMain, price, baseHold, pendingSell, signal, ct);
                return;
            }

            // If not inPosition: clear state
            if (_state.TryGetValue(symbol, out var st0)) st0.Active = false;

            // ===== 2) If pending buy exists -> manage TTL =====
            if (pendingBuy != null)
            {
                await HandlePendingBuyAsync(symbol, quoteAsset, pendingBuy, quoteHold.Free, price, ct);
                return;
            }

            // ignore CLOSE when no position
            if (signal != null && signal.Type == SignalType.Close)
                return;

            if (signal == null || signal.Type == SignalType.None)
                return;

            if (signal.Type != SignalType.Open)
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
            usdToUse *= (1m - _config.SpotOms.EntryQuoteBufferPercent);
            usdToUse = Math.Floor(usdToUse * 100m) / 100m;

            if (usdToUse <= 0m)
            {
                LogWhy(symbol, $"Entry skipped: usdToUse<=0 (usableTotal={usableTotal:0.##}, alloc={allocation:0.##}%, risk={riskPct:0.##}%)");
                return;
            }

            if (usdToUse < _config.SpotOms.MinEntryNotionalUsd)
            {
                await _notify.SendAsync($"[SPOT] Skip BUY {symbol}: budget {usdToUse:0.##} < minEntry {_config.SpotOms.MinEntryNotionalUsd:0.##}");
                return;
            }

            var entryPrice = price * (1m - _config.SpotOms.EntryMakerOffsetPercent);
            var rawQty = usdToUse / entryPrice;

            if (rawQty <= 0m)
            {
                LogWhy(symbol, $"Entry skipped: rawQty<=0 usdToUse={usdToUse:0.##} entryPrice={entryPrice:0.##}");
                return;
            }

            var buy = await _spot.PlaceLimitBuyAsync(symbol, rawQty, entryPrice);
            if (buy.OrderId == "REJECTED" || buy.OrderId == "UNKNOWN")
            {
                LogWhy(symbol, $"Entry REJECTED: rawQty={rawQty:0.########} price={entryPrice:0.##} spend≈{usdToUse:0.##} (reason={signal.Reason})");
                return;
            }

            await _notify.SendAsync($"[SPOT][OMS] Maker BUY {symbol}: spend≈{usdToUse:0.##} {quoteAsset}, price={entryPrice:0.##}, qty≈{rawQty:0.########}, id={buy.OrderId}. (reason={signal.Reason})");
            MarkAction(symbol);
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
                // if no candles, still can exit on strategy Close
                if (signal != null && signal.Type == SignalType.Close)
                    await EnsureMakerSellAsync(symbol, qtyTotal, lastPrice, pendingSell, "SIG_CLOSE(noCandles)", ct);

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
            bool exitBySignal = signal != null && signal.Type == SignalType.Close;

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

        private async Task HandlePendingBuyAsync(string symbol, string quoteAsset, OpenOrderInfo pendingBuy, decimal quoteFree, decimal lastPrice, CancellationToken ct)
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

            await _spot.CancelAllOpenOrdersAsync(symbol);

            var count = _entryRepriceCount.AddOrUpdate(symbol, 1, (_, c) => c + 1);

            // optional market fallback (mày muốn maker-only thì set MaxEntryRepriceBeforeMarketFallback rất lớn hoặc bỏ đoạn này)
            if (count > MaxEntryRepriceBeforeMarketFallback)
            {
                // can't know budget here without cached, so just stop repricing to avoid spam
                await _notify.SendAsync($"[SPOT][OMS] {symbol} pending BUY stale canceled (no cached budget). count={count}");
                _entryRepriceCount.TryRemove(symbol, out _);
                return;
            }

            await _notify.SendAsync($"[SPOT][OMS] {symbol} pending BUY stale canceled, wait next signal. age={ageSec}s count={count}");
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