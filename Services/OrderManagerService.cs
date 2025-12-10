using System.Collections.Concurrent;
using FuturesBot.Config;
using FuturesBot.IServices;
using FuturesBot.Models;
using static FuturesBot.Utils.EnumTypesHelper;

namespace FuturesBot.Services
{
    public class OrderManagerService
    {
        private readonly IExchangeClientService _exchange;
        private readonly SlackNotifierService _notify;
        private readonly BotConfig _botConfig;

        private const int MonitorIntervalMs = 3000;
        private const decimal EarlyExitRR = 0.5m;
        private const decimal HardReverseRR = 0.2m;
        private const decimal EmaBreakTolerance = 0.001m;

        // ============================================================
        // TRACK SYMBOL ĐANG ĐƯỢC GIÁM SÁT
        // ============================================================

        private readonly ConcurrentDictionary<string, bool> _monitoringLimit = new();
        private readonly ConcurrentDictionary<string, bool> _monitoringPosition = new();

        private bool IsMonitoringLimit(string symbol)
            => _monitoringLimit.ContainsKey(symbol);

        private bool IsMonitoringPosition(string symbol)
            => _monitoringPosition.ContainsKey(symbol);

        private void SetMonitoringLimit(string symbol)
            => _monitoringLimit[symbol] = true;

        private void SetMonitoringPosition(string symbol)
            => _monitoringPosition[symbol] = true;

        private void ClearMonitoringLimit(string symbol)
            => _monitoringLimit.TryRemove(symbol, out _);

        private void ClearMonitoringPosition(string symbol)
            => _monitoringPosition.TryRemove(symbol, out _);

        private void ClearAllMonitoring(string symbol)
        {
            ClearMonitoringLimit(symbol);
            ClearMonitoringPosition(symbol);
        }

        // ============================================================
        // CONSTRUCTOR
        // ============================================================

        public OrderManagerService(
            IExchangeClientService exchange,
            SlackNotifierService notify,
            BotConfig config)
        {
            _exchange = exchange;
            _notify = notify;
            _botConfig = config;
        }

        // ============================================================
        //   MONITOR LIMIT ORDER (CHỜ KHỚP)
        // ============================================================

        public async Task MonitorLimitOrderAsync(TradeSignal signal)
        {
            string symbol = signal.Coin;
            bool isLong = signal.Type == SignalType.Long;

            if (IsMonitoringLimit(symbol) || IsMonitoringPosition(symbol))
            {
                await _notify.SendAsync($"[{symbol}] LIMIT: đã monitor → bỏ qua.");
                return;
            }

            SetMonitoringLimit(symbol);

            await _notify.SendAsync($"[{symbol}] Monitor LIMIT started...");

            while (true)
            {
                await Task.Delay(MonitorIntervalMs);

                var openOrders = await _exchange.GetOpenOrdersAsync(symbol);
                var pos = await _exchange.GetPositionAsync(symbol);

                bool hasPosition = pos.PositionAmt != 0;
                bool hasOpenOrder = openOrders.Any();

                // LIMIT ĐÃ KHỚP → CHUYỂN POSITION
                if (hasPosition)
                {
                    await _notify.SendAsync($"[{symbol}] LIMIT filled → chuyển sang monitor POSITION");

                    ClearMonitoringLimit(symbol);

                    if (!IsMonitoringPosition(symbol))
                        _ = MonitorPositionAsync(signal);

                    return;
                }

                // LIMIT không còn order → hủy monitor
                if (!hasOpenOrder)
                {
                    await _notify.SendAsync($"[{symbol}] LIMIT không còn order → stop LIMIT monitor.");
                    ClearMonitoringLimit(symbol);
                    return;
                }

                // CHECK SETUP INVALID
                var candles = await _exchange.GetRecentCandlesAsync(symbol, _botConfig.Intervals[0].FrameTime, 80);
                if (candles.Count == 0) continue;

                var last = candles[^1];
                decimal ema34 = ComputeEmaLast(candles, 34);
                decimal ema89 = ComputeEmaLast(candles, 89);
                decimal ema200 = ComputeEmaLast(candles, 200);

                decimal entry = signal.EntryPrice ?? last.Close;
                decimal boundary = isLong
                    ? GetDynamicBoundaryForLong(entry, ema34, ema89, ema200)
                    : GetDynamicBoundaryForShort(entry, ema34, ema89, ema200);

                bool broken = false;

                if (isLong)
                {
                    if (last.Close < signal.StopLoss) broken = true;
                    if (boundary > 0 && last.Close < boundary * (1 - EmaBreakTolerance))
                        broken = true;
                }
                else
                {
                    if (last.Close > signal.StopLoss) broken = true;
                    if (boundary > 0 && last.Close > boundary * (1 + EmaBreakTolerance))
                        broken = true;
                }

                if (broken)
                {
                    await _notify.SendAsync($"[{symbol}] LIMIT setup broke → cancel orders...");
                    await _exchange.CancelAllOpenOrdersAsync(symbol);
                    ClearMonitoringLimit(symbol);
                    return;
                }
            }
        }

        // ============================================================
        //         MONITOR POSITION V3 (AUTO-TP LOGIC)
        // ============================================================

        public async Task MonitorPositionAsync(TradeSignal signal)
        {
            string symbol = signal.Coin;
            bool isLong = signal.Type == SignalType.Long;

            if (IsMonitoringPosition(symbol))
                return;

            ClearMonitoringLimit(symbol);
            SetMonitoringPosition(symbol);

            decimal entry = signal.EntryPrice ?? 0;
            decimal sl = signal.StopLoss ?? 0;
            decimal tp = signal.TakeProfit ?? 0;

            if (entry == 0 || sl == 0 || tp == 0)
            {
                await _notify.SendAsync($"[{symbol}] POSITION: thiếu Entry/SL/TP.");
            }

            decimal risk = isLong ? entry - sl : sl - entry;

            if (risk <= 0)
            {
                await _notify.SendAsync($"[{symbol}] POSITION: risk <= 0 → stop.");
                ClearMonitoringPosition(symbol);
                return;
            }

            await _notify.SendAsync($"[{symbol}] Monitor POSITION started...");

            while (true)
            {
                await Task.Delay(MonitorIntervalMs);

                var pos = await _exchange.GetPositionAsync(symbol);
                decimal qty = pos.PositionAmt;

                // POSITION CLOSED
                if (qty == 0)
                {
                    await _notify.SendAsync($"[{symbol}] Position closed → stop monitor.");
                    ClearMonitoringPosition(symbol);
                    return;
                }

                decimal price = pos.MarkPrice;

                // AUTO-TP CHECK ================
                bool hasTP = await _exchange.HasTakeProfitOrderAsync(symbol);

                if (!hasTP)
                {
                    decimal absQty = Math.Abs(qty);
                    string posSide = qty > 0 ? "LONG" : "SHORT";

                    await _notify.SendAsync($"[{symbol}] AUTO-TP → đặt TP mới {tp}");

                    await _exchange.PlaceTakeProfitAsync(symbol, posSide, absQty, tp);
                }

                // STOPLOSS HIT
                if ((isLong && price <= sl) || (!isLong && price >= sl))
                {
                    await _notify.SendAsync($"[{symbol}] SL HIT → stop monitor.");
                    ClearMonitoringPosition(symbol);
                    return;
                }

                // TAKE PROFIT HIT
                if ((isLong && price >= tp) || (!isLong && price <= tp))
                {
                    await _notify.SendAsync($"[{symbol}] TP HIT → stop monitor.");
                    ClearMonitoringPosition(symbol);
                    return;
                }

                // RR CALC
                decimal rr = isLong ? (price - entry) / risk : (entry - price) / risk;

                var candles = await _exchange.GetRecentCandlesAsync(symbol, _botConfig.Intervals[0].FrameTime, 40);
                if (candles.Count < 3) continue;

                var (reverse, hardReverse) = CheckMomentumReversal(candles, isLong, entry);

                // EARLY EXIT
                if (rr >= EarlyExitRR && reverse)
                {
                    await _notify.SendAsync($"[{symbol}] EARLY EXIT rr={rr:F2} → đóng lệnh.");
                    await _exchange.ClosePositionAsync(symbol, qty);
                    ClearMonitoringPosition(symbol);
                    return;
                }

                // HARD REVERSE
                if (hardReverse && rr >= -HardReverseRR)
                {
                    await _notify.SendAsync($"[{symbol}] HARD REVERSE → đóng lệnh.");
                    await _exchange.ClosePositionAsync(symbol, qty);
                    ClearMonitoringPosition(symbol);
                    return;
                }

                // TRAILING SL
                decimal newSL = sl;

                if (rr >= 1m)
                    newSL = isLong ? entry + risk * 0.5m : entry - risk * 0.5m;

                if (rr >= 1.5m)
                    newSL = isLong ? entry + risk * 1m : entry - risk * 1m;

                if (newSL != sl)
                {
                    sl = newSL;
                    await UpdateStopLossAsync(symbol, newSL, isLong);
                }
            }
        }

        // ============================================================
        // OTHER METHODS UNCHANGED (ATTACH, EMA, MOMENTUM, TRAILING)
        // ============================================================

        public async Task AttachManualPositionAsync(PositionInfo pos)
        {
            if (pos == null || pos.PositionAmt == 0) return;

            if (IsMonitoringPosition(pos.Symbol)) return;

            ClearMonitoringLimit(pos.Symbol);

            decimal qty = pos.PositionAmt;
            bool isLong = qty > 0;

            decimal entry = pos.EntryPrice;

            var (sl, tp) = await DetectManualSlTpAsync(pos.Symbol, isLong, entry);

            await _notify.SendAsync(
                $"[{pos.Symbol}] MANUAL ATTACH → side={(isLong ? "LONG" : "SHORT")} entry={entry}, SL={sl}, TP={tp}"
            );

            var signal = new TradeSignal
            {
                Coin = pos.Symbol,
                Type = isLong ? SignalType.Long : SignalType.Short,
                EntryPrice = entry,
                StopLoss = sl,
                TakeProfit = tp,
                Time = DateTime.UtcNow,
                Reason = "MANUAL ATTACH"
            };

            _ = MonitorPositionAsync(signal);
        }

        public async Task ClearMonitoringTrigger(string symbol)
        {
            if (IsMonitoringLimit(symbol) || IsMonitoringPosition(symbol))
            {
                ClearAllMonitoring(symbol);
                await _notify.SendAsync($"[{symbol}] đã clear monitoring.");
            }
        }

        private async Task<(decimal? sl, decimal? tp)> DetectManualSlTpAsync(
            string symbol, bool isLong, decimal entryPrice)
        {
            var orders = await _exchange.GetOpenOrdersAsync(symbol);

            if (orders == null || orders.Count == 0)
                return (null, null);

            decimal? sl = null;
            decimal? tp = null;

            foreach (var o in orders)
            {
                decimal trigger = o.StopPrice > 0 ? o.StopPrice : o.Price;
                if (trigger <= 0) continue;

                if (isLong)
                {
                    if (o.Side == "SELL" && trigger > entryPrice &&
                        (o.Type.Contains("LIMIT") || o.Type.Contains("TAKE")))
                        tp = Math.Min(tp ?? trigger, trigger);

                    if (o.Side == "SELL" && trigger < entryPrice &&
                        o.Type.Contains("STOP"))
                        sl = Math.Max(sl ?? trigger, trigger);
                }
                else
                {
                    if (o.Side == "BUY" && trigger < entryPrice &&
                        (o.Type.Contains("LIMIT") || o.Type.Contains("TAKE")))
                        tp = Math.Max(tp ?? trigger, trigger);

                    if (o.Side == "BUY" && trigger > entryPrice &&
                        o.Type.Contains("STOP"))
                        sl = Math.Min(sl ?? trigger, trigger);
                }
            }

            return (sl, tp);
        }

        private static decimal ComputeEmaLast(IReadOnlyList<Candle> candles, int period)
        {
            if (candles.Count == 0) return 0;

            var closes = candles
                .Skip(Math.Max(0, candles.Count - period * 3))
                .Select(c => c.Close)
                .ToArray();

            decimal k = 2m / (period + 1);
            decimal ema = closes[0];

            for (int i = 1; i < closes.Length; i++)
                ema = closes[i] * k + ema * (1 - k);

            return ema;
        }

        private static decimal GetDynamicBoundaryForShort(decimal entry, decimal ema34, decimal ema89, decimal ema200)
        {
            var emas = new List<decimal> { ema34, ema89, ema200 }.Where(e => e > 0).ToList();
            var candidate = emas.Where(e => e >= entry).OrderBy(e => e).FirstOrDefault();
            return candidate == 0 ? 0 : candidate;
        }

        private static decimal GetDynamicBoundaryForLong(decimal entry, decimal ema34, decimal ema89, decimal ema200)
        {
            var emas = new List<decimal> { ema34, ema89, ema200 }.Where(e => e > 0).ToList();
            var candidate = emas.Where(e => e <= entry).OrderByDescending(e => e).FirstOrDefault();
            return candidate == 0 ? 0 : candidate;
        }

        private (bool reverse, bool hardReverse) CheckMomentumReversal(
            IReadOnlyList<Candle> c, bool isLong, decimal entryPrice)
        {
            int i = c.Count - 1;
            var c0 = c[i];
            var c1 = c[i - 1];

            decimal ema34 = ComputeEmaLast(c, 34);
            decimal ema89 = ComputeEmaLast(c, 89);
            decimal ema200 = ComputeEmaLast(c, 200);

            decimal boundary = isLong
                ? GetDynamicBoundaryForLong(entryPrice, ema34, ema89, ema200)
                : GetDynamicBoundaryForShort(entryPrice, ema34, ema89, ema200);

            bool reverse = false;
            bool hard = false;

            if (isLong)
            {
                reverse = c0.Close < c0.Open && c0.Volume >= c1.Volume * 0.8m;
                if (boundary > 0 && c0.Close < boundary * (1 - EmaBreakTolerance))
                    hard = true;
            }
            else
            {
                reverse = c0.Close > c0.Open && c0.Volume >= c1.Volume * 0.8m;
                if (boundary > 0 && c0.Close > boundary * (1 + EmaBreakTolerance))
                    hard = true;
            }

            return (reverse, hard);
        }

        private async Task UpdateStopLossAsync(string symbol, decimal newSL, bool isLong)
        {
            await _notify.SendAsync($"[{symbol}] Trailing SL update → {newSL}");

            await _exchange.CancelStopLossOrdersAsync(symbol);

            var pos = await _exchange.GetPositionAsync(symbol);
            decimal qty = Math.Abs(pos.PositionAmt);
            if (qty <= 0)
            {
                await _notify.SendAsync($"[{symbol}] Không tìm thấy position khi update SL.");
                ClearMonitoringPosition(symbol);
                return;
            }

            string side = isLong ? "SELL" : "BUY";
            string posSide = isLong ? "LONG" : "SHORT";

            await _exchange.PlaceStopOnlyAsync(symbol, side, posSide, qty, newSL);
        }
    }
}
