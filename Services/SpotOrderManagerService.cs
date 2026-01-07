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
    /// - Entry: maker BUY limit, TTL reprice (cancel & wait next signal)
    /// - Exit: maker SELL limit, TTL reprice
    /// - Monitor: EMA34/89 + RSI + ATR trailing (ALL dynamic by timeframe)
    ///
    /// IMPORTANT:
    /// - Spot has NO short. SignalType.Short is treated as EXIT SUGGESTION only.
    /// - When placing SELL, ALWAYS use FREE quantity (avoid Free+Locked reject).
    /// - Partial fills (dust) are handled: if baseQtyTotal > 0 => treat as inPosition.
    /// </summary>
    public sealed class SpotOrderManagerService
    {
        private readonly ISpotExchangeService _spot;
        private readonly SlackNotifierService _notify;
        private readonly BotConfig _config;
        private readonly IndicatorService _indicators;

        private readonly ConcurrentDictionary<string, DateTime> _lastActionUtc = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTime> _lastWhyLogUtc = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, int> _entryRepriceCount = new(StringComparer.OrdinalIgnoreCase);

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

        // ===== Base tunables (for 5m) =====
        private const int BaseTfMin = 5;

        private const int BarsForMonitorMinBase = 120;

        // maker placement offsets (base)
        private const decimal MakerSellOffsetBase = 0.0006m; // +0.06% from last price (maker-first)

        // trailing logic (ATR-based) base
        private const decimal AtrTrailMultBase = 1.20m;      // trail = peak - ATR*mult
        private const decimal AtrSoftStopMultBase = 1.60m;   // soft stop from anchor

        // RSI/EMA exit base
        private const decimal ExitRsiWeakBase = 43m;
        private const decimal EmaBreakTolBase = 0.0006m;

        // pending TTL seconds (reuse config EntryRepriceSeconds)
        private const int MaxEntryRepriceBeforeStopBase = 2; // maker-only: keep low; can scale dynamically

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

            var tfMin = ParseTimeFrameMinutes(coinInfo.MainTimeFrame);
            var factorTf = GetFactorTf(tfMin);

            // ===== Dynamic tunables =====
            int barsForMonitorMin = ClampInt((int)Math.Round(BarsForMonitorMinBase * factorTf), 80, 320);

            decimal makerSellOffset = ClampDec(MakerSellOffsetBase * factorTf, 0.00020m, 0.00180m);

            decimal atrTrailMult = ClampDec(AtrTrailMultBase + (factorTf - 1m) * 0.10m, 1.05m, 1.60m);
            decimal atrSoftStopMult = ClampDec(AtrSoftStopMultBase + (factorTf - 1m) * 0.12m, 1.20m, 2.20m);

            decimal exitRsiWeak = ClampDec(ExitRsiWeakBase + (factorTf - 1m) * 1.0m, 38m, 55m);
            decimal emaBreakTol = ClampDec(EmaBreakTolBase * factorTf, 0.00025m, 0.00150m);

            int maxEntryRepriceBeforeStop = ClampInt((int)Math.Round(MaxEntryRepriceBeforeStopBase + (factorTf - 1m) * 1.0m), 1, 6);

            var price = await _spot.GetLastPriceAsync(symbol);
            if (price <= 0m) return;

            var quoteHold = await _spot.GetHoldingAsync(quoteAsset);
            var baseHold = await _spot.GetHoldingAsync(baseAsset);

            var baseQtyTotal = baseHold.Free + baseHold.Locked;
            var holdingNotional = baseQtyTotal * price;

            // IMPORTANT: handle partial fills (dust)
            // - If you have ANY base qty, treat as inPosition so exit logic can clean up.
            var minHolding = _config.SpotOms.MinHoldingNotionalUsd;
            bool inPosition = holdingNotional >= minHolding || baseQtyTotal > 0m;

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
                await MonitorExitAsync(
                    symbol,
                    coinInfo,
                    candlesMain,
                    price,
                    baseHold,
                    pendingSell,
                    signal,
                    barsForMonitorMin,
                    atrTrailMult,
                    atrSoftStopMult,
                    exitRsiWeak,
                    emaBreakTol,
                    makerSellOffset,
                    ct);

                return;
            }

            // If not inPosition: clear state
            if (_state.TryGetValue(symbol, out var st0)) st0.Active = false;

            // ===== 2) If pending buy exists -> manage TTL =====
            if (pendingBuy != null)
            {
                await HandlePendingBuyAsync(symbol, pendingBuy, ct, maxEntryRepriceBeforeStop);
                return;
            }

            // ignore SHORT when no position (spot has no short)
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

            // maker buy offset comes from config (already)
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
            int barsForMonitorMin,
            decimal atrTrailMult,
            decimal atrSoftStopMult,
            decimal exitRsiWeak,
            decimal emaBreakTol,
            decimal makerSellOffset,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var qtyTotal = baseHold.Free + baseHold.Locked;
            if (qtyTotal <= 0m) return;

            // IMPORTANT: sell MUST use FREE qty to avoid insufficient balance / locked rejection
            var freeQty = baseHold.Free;

            if (candlesMain == null || candlesMain.Count < barsForMonitorMin)
            {
                if (signal != null && signal.Type == SignalType.Short && freeQty > 0m)
                    await EnsureMakerSellAsync(symbol, freeQty, lastPrice, pendingSell, makerSellOffset, "SIG_SHORT(noCandles)", ct);

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
                st.Trail = atr > 0m ? (st.Peak - atr * atrTrailMult) : lastPrice * (1m - 0.0075m);
                st.SellRepriceCount = 0;

                await _notify.SendAsync($"[SPOT][MON] attach {symbol} qty={qtyTotal:0.########} anchor≈{st.Anchor:0.##}");
            }

            st.Peak = Math.Max(st.Peak, lastPrice);

            // ATR trail (dynamic)
            if (atr > 0m)
            {
                var newTrail = st.Peak - atr * atrTrailMult;
                if (newTrail > st.Trail) st.Trail = newTrail;
            }

            // exit rules
            bool exitBySignal = signal != null && signal.Type == SignalType.Short;

            bool closeBelow34 = (e34 > 0m) && (c0.Close < e34 * (1m - emaBreakTol));
            bool closeBelow89 = (e89 > 0m) && (c0.Close < e89 * (1m - emaBreakTol));
            bool rsiWeak = r <= exitRsiWeak;

            bool exitByEmaRsi = (closeBelow34 && rsiWeak) || closeBelow89;
            bool exitByTrail = lastPrice <= st.Trail;

            bool softStop = false;
            if (atr > 0m && st.Anchor > 0m)
                softStop = lastPrice <= (st.Anchor - atr * atrSoftStopMult);

            bool shouldExit = exitBySignal || exitByEmaRsi || exitByTrail || softStop;

            if (!shouldExit)
                return;

            var reason = BuildExitReason(exitBySignal, exitByEmaRsi, exitByTrail, softStop, r, e34, e89, st.Trail, signal);

            if (freeQty <= 0m)
            {
                // If everything is locked by an existing sell, just keep repricing that sell.
                if (pendingSell != null)
                    await HandlePendingSellAsync(symbol, lastPrice, pendingSell, makerSellOffset, reason, ct);
                else
                    LogWhy(symbol, $"Exit wanted but freeQty=0 (locked={baseHold.Locked:0.########}) reason={reason}");

                return;
            }

            await EnsureMakerSellAsync(symbol, freeQty, lastPrice, pendingSell, makerSellOffset, reason, ct);
        }

        private async Task EnsureMakerSellAsync(
            string symbol,
            decimal freeQty,
            decimal lastPrice,
            OpenOrderInfo? pendingSell,
            decimal makerSellOffset,
            string reason,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            // if pending sell exists -> TTL reprice
            if (pendingSell != null)
            {
                await HandlePendingSellAsync(symbol, lastPrice, pendingSell, makerSellOffset, reason, ct);
                return;
            }

            if (IsThrottled(symbol))
            {
                LogWhyThrottled(symbol, $"Exit throttled. reason={reason}");
                return;
            }

            if (freeQty <= 0m) return;

            var sellPrice = lastPrice * (1m + makerSellOffset);

            var sell = await _spot.PlaceLimitSellAsync(symbol, freeQty, sellPrice);
            if (sell.OrderId == "REJECTED" || sell.OrderId == "UNKNOWN")
            {
                LogWhy(symbol, $"Exit SELL REJECTED: qty={freeQty:0.########} price={sellPrice:0.##} reason={reason}");
                return;
            }

            await _notify.SendAsync($"[SPOT][EXIT] Maker SELL {symbol}: qty={freeQty:0.########} price={sellPrice:0.##} reason={reason}");
            MarkAction(symbol);
        }

        private async Task HandlePendingSellAsync(
            string symbol,
            decimal lastPrice,
            OpenOrderInfo pendingSell,
            decimal makerSellOffset,
            string reason,
            CancellationToken ct)
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

            // best-effort: use lastPrice offset again
            var sellPrice = lastPrice * (1m + makerSellOffset);

            // after cancel, balance should be free again, but we don't fetch holding here (keep light)
            // place sell with "pendingSell.Quantity" if available else keep same notional via original qty
            var qty = Math.Max(0m, pendingSell.OrigQty - pendingSell.ExecutedQty);
if (qty <= 0m)
{
    LogWhy(symbol, $"Reprice SELL skipped: remainQty<=0 reason={reason}");
    return;
}

            var sell = await _spot.PlaceLimitSellAsync(symbol, qty, sellPrice);

            if (sell.OrderId == "REJECTED" || sell.OrderId == "UNKNOWN")
            {
                LogWhy(symbol, $"Reprice SELL REJECTED: qty={qty:0.########} price={sellPrice:0.##} reason={reason}");
                return;
            }

            await _notify.SendAsync($"[SPOT][EXIT] Reprice maker SELL {symbol}: qty={qty:0.########} price={sellPrice:0.##} age={ageSec}s count={st.SellRepriceCount} reason={reason}");
            MarkAction(symbol);
        }

        private async Task HandlePendingBuyAsync(string symbol, OpenOrderInfo pendingBuy, CancellationToken ct, int maxEntryRepriceBeforeStop)
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

            // maker-only: cancel & wait next signal (avoid spamming reprices without cached budget)
            if (count > maxEntryRepriceBeforeStop)
            {
                await _notify.SendAsync($"[SPOT][OMS] {symbol} pending BUY stale canceled, stop repricing. age={ageSec}s count={count}");
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

        private static int ParseTimeFrameMinutes(string? tf)
        {
            if (string.IsNullOrWhiteSpace(tf)) return BaseTfMin;
            tf = tf.Trim().ToLowerInvariant();

            if (tf.EndsWith("m") && int.TryParse(tf[..^1], out var m)) return Math.Max(1, m);
            if (tf.EndsWith("h") && int.TryParse(tf[..^1], out var h)) return Math.Max(1, h * 60);

            return BaseTfMin;
        }

        /// <summary>
        /// factorTF: sqrt(tfMin / BaseTfMin) clamped
        /// - 1m  => ~0.447 -> clamp to >=0.75
        /// - 5m  => 1.0
        /// - 15m => ~1.732
        /// - 1h  => ~3.464 -> clamp to <=2.50
        /// </summary>
        private static decimal GetFactorTf(int tfMin)
        {
            if (tfMin <= 0) tfMin = BaseTfMin;
            var raw = SqrtDec((decimal)tfMin / BaseTfMin);
            return ClampDec(raw, 0.75m, 2.50m);
        }

        private static decimal SqrtDec(decimal x)
        {
            if (x <= 0m) return 0m;
            return (decimal)Math.Sqrt((double)x);
        }

        private static int ClampInt(int v, int min, int max) => Math.Min(max, Math.Max(min, v));
        private static decimal ClampDec(decimal v, decimal min, decimal max) => Math.Min(max, Math.Max(min, v));
    }
}
