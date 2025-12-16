using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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

        // FIX -1003: giảm polling
        private const int MonitorIntervalMs = 10000; // cũ 3000
        private const int SlTpCheckEverySec = 30;    // check SL/TP từ sàn mỗi 30s
        private const int CandleFetchEverySec = 20;  // fetch candles mỗi 20s

        private const decimal EarlyExitRR = 1.0m;   // chỉ cắt khi đã >= 1R
        private const decimal HardReverseRR = 0.5m; // chỉ cắt khi đã sai rõ

        private const decimal EmaBreakTolerance = 0.001m;

        private static readonly TimeSpan LimitTimeout = TimeSpan.FromMinutes(20);

        // NEW: grace period sau khi vào lệnh (tránh vừa fill xong detect missing và close oan)
        private const int SlTpGraceAfterFillSec = 8;

        // throttle debug spam
        private readonly ConcurrentDictionary<string, DateTime> _lastMissingLogUtc = new();

        // ============================================================
        // TRACK SYMBOL ĐANG ĐƯỢC GIÁM SÁT
        // ============================================================

        private readonly ConcurrentDictionary<string, bool> _monitoringLimit = new();
        private readonly ConcurrentDictionary<string, bool> _monitoringPosition = new();

        private bool IsMonitoringLimit(string symbol) => _monitoringLimit.ContainsKey(symbol);
        private bool IsMonitoringPosition(string symbol) => _monitoringPosition.ContainsKey(symbol);

        private bool TryStartMonitoringLimit(string symbol) => _monitoringLimit.TryAdd(symbol, true);
        private bool TryStartMonitoringPosition(string symbol) => _monitoringPosition.TryAdd(symbol, true);

        private void ClearMonitoringLimit(string symbol) => _monitoringLimit.TryRemove(symbol, out _);
        private void ClearMonitoringPosition(string symbol) => _monitoringPosition.TryRemove(symbol, out _);

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
            string symbol = signal.Symbol;
            bool isLong = signal.Type == SignalType.Long;

            if (IsMonitoringPosition(symbol) || !TryStartMonitoringLimit(symbol))
            {
                await _notify.SendAsync($"[{symbol}] LIMIT: đã monitor → bỏ qua.");
                return;
            }

            await _notify.SendAsync($"[{symbol}] Monitor LIMIT started...");

            var startTime = DateTime.UtcNow;

            try
            {
                while (true)
                {
                    await Task.Delay(MonitorIntervalMs);

                    var elapsed = DateTime.UtcNow - startTime;
                    if (elapsed > LimitTimeout)
                    {
                        await _notify.SendAsync(
                            $"[{symbol}] LIMIT quá {LimitTimeout.TotalMinutes} phút chưa khớp → cancel open orders và stop LIMIT monitor.");

                        await _exchange.CancelAllOpenOrdersAsync(symbol);
                        return;
                    }

                    var openOrders = await _exchange.GetOpenOrdersAsync(symbol);
                    var pos = await _exchange.GetPositionAsync(symbol);

                    bool hasPosition = pos.PositionAmt != 0;
                    bool hasOpenOrder = openOrders.Any();

                    if (hasPosition)
                    {
                        await _notify.SendAsync($"[{symbol}] LIMIT filled → chuyển sang monitor POSITION");

                        ClearMonitoringLimit(symbol);
                        _ = MonitorPositionAsync(signal);
                        return;
                    }

                    if (!hasOpenOrder)
                    {
                        await _notify.SendAsync($"[{symbol}] LIMIT không còn order → stop LIMIT monitor.");
                        return;
                    }

                    var candles = await _exchange.GetRecentCandlesAsync(symbol, _botConfig.Intervals[0].FrameTime, 80);
                    if (candles == null || candles.Count < 3) continue;

                    var lastClosed = candles[^2];

                    decimal ema34 = ComputeEmaLast(candles, 34);
                    decimal ema89 = ComputeEmaLast(candles, 89);
                    decimal ema200 = ComputeEmaLast(candles, 200);

                    decimal entry = signal.EntryPrice ?? lastClosed.Close;
                    decimal boundary = isLong
                        ? GetDynamicBoundaryForLong(entry, ema34, ema89, ema200)
                        : GetDynamicBoundaryForShort(entry, ema34, ema89, ema200);

                    bool broken = false;

                    var slVal = signal.StopLoss ?? 0m;
                    bool hasSl = slVal > 0m;

                    if (isLong)
                    {
                        if (hasSl && lastClosed.Close < slVal) broken = true;
                        if (boundary > 0 && lastClosed.Close < boundary * (1 - EmaBreakTolerance)) broken = true;
                    }
                    else
                    {
                        if (hasSl && lastClosed.Close > slVal) broken = true;
                        if (boundary > 0 && lastClosed.Close > boundary * (1 + EmaBreakTolerance)) broken = true;
                    }

                    if (broken)
                    {
                        await _notify.SendAsync($"[{symbol}] LIMIT setup broke → cancel open orders...");
                        await _exchange.CancelAllOpenOrdersAsync(symbol);
                        return;
                    }
                }
            }
            finally
            {
                ClearMonitoringLimit(symbol);
            }
        }

        // ============================================================
        //         MONITOR POSITION (AUTO-TP, TRAILING, EARLY EXIT)
        // ============================================================

        public async Task MonitorPositionAsync(TradeSignal signal)
        {
            string symbol = signal.Symbol;

            if (!TryStartMonitoringPosition(symbol))
            {
                await _notify.SendAsync($"[{symbol}] POSITION: đã monitor → bỏ qua.");
                return;
            }

            ClearMonitoringLimit(symbol);

            decimal entry = signal.EntryPrice ?? 0m;
            decimal sl = signal.StopLoss ?? 0m;
            decimal tp = signal.TakeProfit ?? 0m;

            bool missingNotified = false;
            bool tpInitialized = false;
            bool autoTpPlaced = false;

            // FIX: chỉ cảnh báo missing sau khi đã thử sync SL/TP ít nhất 1 lần
            bool slTpSyncedAtLeastOnce = false;

            const decimal DefaultManualRR = 2m;

            DateTime lastSlTpCheckUtc = DateTime.MinValue;
            DateTime lastCandleFetchUtc = DateTime.MinValue;
            IReadOnlyList<Candle>? cachedCandles = null;

            // NEW: thời điểm bắt đầu monitor (grace)
            var positionMonitorStartedUtc = DateTime.UtcNow;

            await _notify.SendAsync($"[{symbol}] Monitor POSITION started...");

            try
            {
                while (true)
                {
                    await Task.Delay(MonitorIntervalMs);

                    var pos = await _exchange.GetPositionAsync(symbol);
                    decimal qty = pos.PositionAmt;

                    if (qty == 0)
                    {
                        await _notify.SendAsync($"[{symbol}] Position closed → stop monitor.");
                        return;
                    }

                    bool isLongPosition = qty > 0;
                    decimal price = pos.MarkPrice;
                    decimal absQty = Math.Abs(qty);
                    string posSide = isLongPosition ? "LONG" : "SHORT";

                    // luôn sync entry theo sàn (pivot chuẩn)
                    if (pos.EntryPrice > 0m)
                    {
                        if (entry <= 0m || Math.Abs(entry - pos.EntryPrice) / pos.EntryPrice > 0.0005m)
                            entry = pos.EntryPrice;
                    }

                    bool hasEntry = entry > 0m;
                    bool hasSL = sl > 0m;
                    bool hasTP = tp > 0m;

                    bool inGrace = (DateTime.UtcNow - positionMonitorStartedUtc) < TimeSpan.FromSeconds(SlTpGraceAfterFillSec);

                    // =================== SYNC SL/TP TỪ SÀN (throttle) ===================
                    if ((DateTime.UtcNow - lastSlTpCheckUtc) >= TimeSpan.FromSeconds(SlTpCheckEverySec) || lastSlTpCheckUtc == DateTime.MinValue)
                    {
                        var det = await DetectManualSlTpAsync(symbol, isLongPosition, entry, pos);
                        lastSlTpCheckUtc = DateTime.UtcNow;

                        // đã thử sync ít nhất 1 lần (dù có ra hay không)
                        slTpSyncedAtLeastOnce = true;

                        if (sl <= 0m && det.Sl.HasValue && det.Sl.Value > 0m)
                        {
                            sl = det.Sl.Value;
                            hasSL = true;
                            await _notify.SendAsync($"[{symbol}] Sync SL từ sàn → SL={Math.Round(sl, 6)}");
                        }

                        if (tp <= 0m && det.Tp.HasValue && det.Tp.Value > 0m)
                        {
                            tp = det.Tp.Value;
                            hasTP = true;
                            tpInitialized = true;
                            await _notify.SendAsync($"[{symbol}] Sync TP từ sàn → TP={Math.Round(tp, 6)}");
                        }
                    }

                    // FIX: chỉ notify missing sau grace + sau khi đã thử sync ít nhất 1 lần
                    if (!inGrace && slTpSyncedAtLeastOnce)
                    {
                        if ((!hasEntry || !hasSL || !hasTP) && !missingNotified)
                        {
                            await _notify.SendAsync(
                                $"[{symbol}] POSITION: thiếu Entry/SL/TP sau sync. entry={entry}, sl={sl}, tp={tp} (sẽ auto-sync nếu có thể)");
                            missingNotified = true;
                        }
                    }

                    // =================== MANUAL: có SL nhưng thiếu TP → AUTO TP ===================
                    if (hasEntry && hasSL && !hasTP && !autoTpPlaced)
                    {
                        decimal riskManual = isLongPosition ? (entry - sl) : (sl - entry);
                        if (riskManual > 0m)
                        {
                            decimal autoTp = isLongPosition
                                ? entry + riskManual * DefaultManualRR
                                : entry - riskManual * DefaultManualRR;

                            tp = autoTp;
                            hasTP = true;
                            tpInitialized = false;

                            await _notify.SendAsync(
                                $"[{symbol}] Manual có SL nhưng thiếu TP → AUTO-TP={Math.Round(autoTp, 6)} theo RR={DefaultManualRR}");

                            var ok = await _exchange.PlaceTakeProfitAsync(symbol, posSide, absQty, autoTp);
                            if (!ok)
                            {
                                await _notify.SendAsync($"[{symbol}] AUTO-TP FAILED → tp={Math.Round(autoTp, 6)}, qty={absQty}");
                            }
                            else
                            {
                                autoTpPlaced = true;
                                tpInitialized = true;
                            }
                        }
                    }

                    // =================== AUTO-TP 1 lần: check TP trên sàn trước ===================
                    if (hasTP && !tpInitialized)
                    {
                        if ((DateTime.UtcNow - lastSlTpCheckUtc) >= TimeSpan.FromSeconds(5))
                        {
                            var det = await DetectManualSlTpAsync(symbol, isLongPosition, entry, pos);
                            lastSlTpCheckUtc = DateTime.UtcNow;

                            if (det.Tp.HasValue)
                            {
                                tpInitialized = true;
                            }
                            else
                            {
                                decimal tpDisplay = Math.Round(tp, 6);
                                await _notify.SendAsync($"[{symbol}] AUTO-TP → đặt TP mới {tpDisplay}");

                                var ok = await _exchange.PlaceTakeProfitAsync(symbol, posSide, absQty, tp);
                                if (!ok)
                                    await _notify.SendAsync($"[{symbol}] AUTO-TP FAILED → tp={tpDisplay}, qty={absQty}");
                                else
                                    tpInitialized = true;
                            }
                        }
                    }

                    // =================== SL HIT (theo giá) ===================
                    if (!inGrace && hasEntry && hasSL && IsValidStopLoss(sl, isLongPosition, entry))
                    {
                        if ((isLongPosition && price <= sl) || (!isLongPosition && price >= sl))
                        {
                            var det = await DetectManualSlTpAsync(symbol, isLongPosition, entry, pos);

                            if (det.Sl.HasValue)
                            {
                                await _notify.SendAsync($"[{symbol}] SL touched (MarkPrice) → waiting exchange SL.");
                            }
                            else
                            {
                                await _notify.SendAsync($"[{symbol}] SL touched but missing on exchange → force close.");
                                await _exchange.ClosePositionAsync(symbol, qty);
                            }

                            return;
                        }
                    }

                    // =================== TP HIT (theo giá local) ===================
                    if (hasEntry && hasTP && IsValidTakeProfit(tp, isLongPosition, entry))
                    {
                        bool hitTp = (isLongPosition && price >= tp) || (!isLongPosition && price <= tp);
                        if (hitTp)
                        {
                            decimal? tpOnExchange2 = null;

                            if ((DateTime.UtcNow - lastSlTpCheckUtc) >= TimeSpan.FromSeconds(5))
                            {
                                var det2 = await DetectManualSlTpAsync(symbol, isLongPosition, entry, pos);
                                tpOnExchange2 = det2.Tp;
                                lastSlTpCheckUtc = DateTime.UtcNow;
                            }

                            if (!tpOnExchange2.HasValue)
                            {
                                await _notify.SendAsync($"[{symbol}] Giá chạm TP nhưng không thấy TP trên sàn → đóng position.");
                                await _exchange.ClosePositionAsync(symbol, qty);
                            }
                            else
                            {
                                await _notify.SendAsync($"[{symbol}] TP HIT (theo giá) → stop monitor.");
                            }
                            return;
                        }
                    }

                    // =================== RR / EARLY EXIT / HARD REVERSE / TRAILING ===================
                    decimal risk = 0m;
                    bool useRR = false;

                    if (hasEntry && hasSL && IsValidStopLoss(sl, isLongPosition, entry))
                    {
                        risk = isLongPosition ? entry - sl : sl - entry;
                        if (risk > 0m) useRR = true;
                    }

                    if (useRR)
                    {
                        decimal rr = isLongPosition ? (price - entry) / risk : (entry - price) / risk;

                        // fetch candles theo throttle
                        if ((DateTime.UtcNow - lastCandleFetchUtc) >= TimeSpan.FromSeconds(CandleFetchEverySec))
                        {
                            cachedCandles = await _exchange.GetRecentCandlesAsync(
                                symbol, _botConfig.Intervals[0].FrameTime, 60);
                            lastCandleFetchUtc = DateTime.UtcNow;
                        }

                        if (cachedCandles != null && cachedCandles.Count >= 5)
                        {
                            var (reverse, hardReverse) = CheckMomentumReversal(cachedCandles, isLongPosition, entry);

                            // EARLY EXIT (confirm, chỉ khi đã >= 1R)
                            if (rr >= EarlyExitRR && reverse)
                            {
                                var confirm = CheckMomentumReversal(cachedCandles, isLongPosition, entry);
                                if (confirm.reverse)
                                {
                                    await _notify.SendAsync($"[{symbol}] EARLY EXIT CONFIRMED rr={rr:F2} → close.");
                                    await _exchange.ClosePositionAsync(symbol, qty);
                                    return;
                                }
                            }

                            // HARD REVERSE (chỉ khi đã sai rõ)
                            if (hardReverse && rr <= -HardReverseRR)
                            {
                                await _notify.SendAsync($"[{symbol}] HARD REVERSE rr={rr:F2} → close.");
                                await _exchange.ClosePositionAsync(symbol, qty);
                                return;
                            }
                        }

                        // Trailing SL theo RR
                        if (hasSL)
                        {
                            decimal newSL = sl;

                            if (rr >= 1m)
                                newSL = isLongPosition ? entry + risk * 0.5m : entry - risk * 0.5m;

                            if (rr >= 1.5m)
                                newSL = isLongPosition ? entry + risk * 1m : entry - risk * 1m;

                            if (newSL != sl)
                            {
                                sl = newSL;

                                lastSlTpCheckUtc = await UpdateStopLossAsync(
                                    symbol,
                                    newSL,
                                    isLongPosition,
                                    hasTP,
                                    tp,
                                    pos,
                                    lastSlTpCheckUtc);
                            }
                        }
                    }
                }
            }
            finally
            {
                ClearMonitoringPosition(symbol);
            }
        }

        // ============================================================
        //          MANUAL ATTACH POSITION (AUTO TÍNH TP NẾU MẤT)
        // ============================================================

        public async Task AttachManualPositionAsync(PositionInfo pos)
        {
            if (pos == null || pos.PositionAmt == 0)
                return;

            if (IsMonitoringPosition(pos.Symbol))
                return;

            ClearMonitoringLimit(pos.Symbol);

            decimal qty = pos.PositionAmt;
            bool isLong = qty > 0;

            decimal entry = pos.EntryPrice;

            var det = await DetectManualSlTpAsync(pos.Symbol, isLong, entry, pos);

            decimal? sl = det.Sl;
            decimal? tp = det.Tp;

            if (!tp.HasValue && sl.HasValue && entry > 0)
            {
                decimal risk = isLong ? entry - sl.Value : sl.Value - entry;

                if (risk > 0)
                {
                    const decimal defaultRR = 2m;

                    var autoTp = isLong
                        ? entry + risk * defaultRR
                        : entry - risk * defaultRR;

                    tp = autoTp;

                    await _notify.SendAsync(
                        $"[{pos.Symbol}] MANUAL ATTACH: không tìm thấy TP trên sàn → auto TP={autoTp} theo RR={defaultRR}");
                }
            }

            await _notify.SendAsync(
                $"[{pos.Symbol}] MANUAL ATTACH → side={(isLong ? "LONG" : "SHORT")} entry={entry}, SL={sl}, TP={tp}"
            );

            var signal = new TradeSignal
            {
                Symbol = pos.Symbol,
                Type = isLong ? SignalType.Long : SignalType.Short,
                EntryPrice = entry,
                StopLoss = sl,
                TakeProfit = tp,
                Time = DateTime.UtcNow,
                Reason = "MANUAL ATTACH"
            };

            _ = MonitorPositionAsync(signal);
        }

        // ============================================================
        //   NEW: MANUAL ATTACH LIMIT ORDERS (ENTRY LIMIT đang treo)
        //   - Chỉ attach ENTRY LIMIT (không reduceOnly, không STOP/TAKE)
        //   - Nếu đã có position => bỏ qua (đã monitor position)
        //   - Nếu đã monitor limit/position => bỏ qua
        // ============================================================

        public async Task AttachManualLimitOrdersAsync(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return;

            if (IsMonitoringPosition(symbol) || IsMonitoringLimit(symbol))
                return;

            var pos = await _exchange.GetPositionAsync(symbol);
            if (pos != null && pos.PositionAmt != 0)
                return; // đã có position thì không attach limit entry nữa

            var openOrders = await _exchange.GetOpenOrdersAsync(symbol);
            if (openOrders == null || openOrders.Count == 0)
                return;

            var entryLimits = openOrders
                .Where(IsEntryLimitOrder)
                .ToList();

            if (entryLimits.Count == 0)
                return;

            // chọn 1 order để attach (tránh monitor trùng)
            // BUY: chọn giá cao nhất (gần khớp), SELL: chọn giá thấp nhất
            OpenOrderInfo chosen = ChooseBestEntryLimit(entryLimits);

            var side = (chosen.Side ?? "").Trim().ToUpperInvariant();
            var type = (chosen.Type ?? "").Trim().ToUpperInvariant();
            decimal entryPrice = chosen.Price > 0 ? chosen.Price : chosen.StopPrice;

            if (entryPrice <= 0)
                return;

            SignalType sigType;
            if (side == "BUY") sigType = SignalType.Long;
            else if (side == "SELL") sigType = SignalType.Short;
            else
            {
                // side không rõ => bỏ qua cho an toàn
                await _notify.SendAsync($"[{symbol}] MANUAL ATTACH LIMIT: side invalid ({chosen.Side}) → skip.");
                return;
            }

            var signal = new TradeSignal
            {
                Symbol = symbol,
                Type = sigType,
                EntryPrice = entryPrice,
                StopLoss = null,
                TakeProfit = null,
                Time = DateTime.UtcNow,
                Reason = $"MANUAL ATTACH LIMIT ({type}/{side})"
            };

            await _notify.SendAsync($"[{symbol}] MANUAL ATTACH LIMIT → start monitor. side={side}, entry={entryPrice}");
            _ = MonitorLimitOrderAsync(signal);
        }

        // tiện: 1 call reattach cả position + limit cho 1 symbol
        public async Task AttachManualStateAsync(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return;

            var pos = await _exchange.GetPositionAsync(symbol);
            if (pos != null && pos.PositionAmt != 0)
            {
                await AttachManualPositionAsync(pos);
                return;
            }

            await AttachManualLimitOrdersAsync(symbol);
        }

        private static bool IsEntryLimitOrder(OpenOrderInfo o)
        {
            if (o == null) return false;

            // loại TP/SL & conditional
            var type = (o.Type ?? "").Trim();

            bool isLimit =
                type.Equals("LIMIT", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("LIMIT", StringComparison.OrdinalIgnoreCase);

            if (!isLimit) return false;

            // loại STOP/TAKE
            if (type.Contains("STOP", StringComparison.OrdinalIgnoreCase)) return false;
            if (type.Contains("TAKE", StringComparison.OrdinalIgnoreCase)) return false;

            // loại reduceOnly (TP/SL đóng lệnh)
            if (o.ReduceOnly) return false;

            // entry limit phải có giá
            if (o.Price <= 0m && o.StopPrice <= 0m) return false;

            return true;
        }

        private static OpenOrderInfo ChooseBestEntryLimit(List<OpenOrderInfo> entryLimits)
        {
            // nếu có mix BUY/SELL (hiếm) thì ưu tiên nhóm có nhiều hơn
            int buyCount = entryLimits.Count(x => string.Equals((x.Side ?? "").Trim(), "BUY", StringComparison.OrdinalIgnoreCase));
            int sellCount = entryLimits.Count(x => string.Equals((x.Side ?? "").Trim(), "SELL", StringComparison.OrdinalIgnoreCase));

            if (buyCount >= sellCount)
            {
                return entryLimits
                    .Where(x => string.Equals((x.Side ?? "").Trim(), "BUY", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.Price > 0 ? x.Price : x.StopPrice)
                    .First();
            }

            return entryLimits
                .Where(x => string.Equals((x.Side ?? "").Trim(), "SELL", StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.Price > 0 ? x.Price : x.StopPrice)
                .First();
        }

        public async Task ClearMonitoringTrigger(string symbol)
        {
            if (IsMonitoringLimit(symbol) || IsMonitoringPosition(symbol))
            {
                ClearAllMonitoring(symbol);
                await _notify.SendAsync($"[{symbol}] đã clear monitoring.");
            }
        }

        // ============================================================
        //   DETECT TP/SL từ openOrders + openAlgoOrders
        //   ưu tiên phân loại theo ENTRY (đỡ nhầm TP thành SL)
        //   lọc theo SIDE protective (LONG -> SELL, SHORT -> BUY)
        // ============================================================

        private sealed class SlTpDetection
        {
            public decimal? Sl { get; set; }
            public decimal? Tp { get; set; }
            public int TotalOrders { get; set; }
            public int ConsideredOrders { get; set; }
        }

        private async Task<SlTpDetection> DetectManualSlTpAsync(
            string symbol, bool isLong, decimal entryPriceFromCaller, PositionInfo pos)
        {
            var normalOrders = await _exchange.GetOpenOrdersAsync(symbol);
            var algoOrders = await _exchange.GetOpenAlgoOrdersAsync(symbol);

            var orders = new List<OpenOrderInfo>();
            if (normalOrders != null) orders.AddRange(normalOrders);
            if (algoOrders != null) orders.AddRange(algoOrders);

            var result = new SlTpDetection
            {
                TotalOrders = orders.Count
            };

            if (orders.Count == 0)
                return result;

            static decimal GetTrigger(OpenOrderInfo o)
            {
                if (o == null) return 0m;
                if (o.StopPrice > 0) return o.StopPrice;
                if (o.Price > 0) return o.Price;
                return 0m;
            }

            static bool IsTake(string type)
                => !string.IsNullOrWhiteSpace(type) &&
                   type.Contains("TAKE", StringComparison.OrdinalIgnoreCase);

            static bool IsStop(string type)
                => !string.IsNullOrWhiteSpace(type) &&
                   (type.Contains("STOP", StringComparison.OrdinalIgnoreCase) ||
                    type.Contains("LOSS", StringComparison.OrdinalIgnoreCase))
                   && !type.Contains("TAKE", StringComparison.OrdinalIgnoreCase);

            static bool IsProtectiveSideForPosition(OpenOrderInfo o, bool isLongPos)
            {
                var side = (o?.Side ?? "").Trim().ToUpperInvariant();
                if (string.IsNullOrWhiteSpace(side)) return true;
                return isLongPos ? side == "SELL" : side == "BUY";
            }

            decimal markPrice = pos?.MarkPrice ?? 0m;
            decimal exEntry = pos?.EntryPrice ?? 0m;

            decimal entryPivot =
                exEntry > 0 ? exEntry :
                entryPriceFromCaller > 0 ? entryPriceFromCaller : 0m;

            decimal? sl = null;
            decimal? tp = null;

            foreach (var o in orders)
            {
                if (o == null) continue;

                string type = o.Type ?? string.Empty;
                decimal trigger = GetTrigger(o);
                if (trigger <= 0) continue;

                bool take = IsTake(type);
                bool stop = IsStop(type);
                if (!take && !stop) continue;

                if (!IsProtectiveSideForPosition(o, isLong))
                    continue;

                result.ConsideredOrders++;

                // PRIMARY: phân loại theo ENTRY
                if (entryPivot > 0m)
                {
                    if (isLong)
                    {
                        if (stop && trigger < entryPivot)
                            sl = sl.HasValue ? Math.Max(sl.Value, trigger) : trigger;

                        if (take && trigger > entryPivot)
                            tp = tp.HasValue ? Math.Min(tp.Value, trigger) : trigger;
                    }
                    else
                    {
                        if (stop && trigger > entryPivot)
                            sl = sl.HasValue ? Math.Min(sl.Value, trigger) : trigger;

                        if (take && trigger < entryPivot)
                            tp = tp.HasValue ? Math.Max(tp.Value, trigger) : trigger;
                    }

                    continue;
                }

                // FALLBACK: theo MARK
                if (markPrice > 0m)
                {
                    if (isLong)
                    {
                        if (stop && trigger < markPrice)
                            sl = sl.HasValue ? Math.Max(sl.Value, trigger) : trigger;

                        if (take && trigger > markPrice)
                            tp = tp.HasValue ? Math.Min(tp.Value, trigger) : trigger;
                    }
                    else
                    {
                        if (stop && trigger > markPrice)
                            sl = sl.HasValue ? Math.Min(sl.Value, trigger) : trigger;

                        if (take && trigger < markPrice)
                            tp = tp.HasValue ? Math.Max(tp.Value, trigger) : trigger;
                    }

                    continue;
                }

                // LAST RESORT
                if (take && !tp.HasValue) tp = trigger;
                if (stop && !sl.HasValue) sl = trigger;
            }

            result.Sl = sl;
            result.Tp = tp;

            return result;
        }

        // ============================================================
        //                  VALIDATION HELPERS
        // ============================================================

        private static bool IsValidStopLoss(decimal sl, bool isLong, decimal entry)
        {
            if (sl <= 0m || entry <= 0m) return false;
            return isLong ? sl < entry : sl > entry;
        }

        private static bool IsValidTakeProfit(decimal tp, bool isLong, decimal entry)
        {
            if (tp <= 0m || entry <= 0m) return false;
            return isLong ? tp > entry : tp < entry;
        }

        // ============================================================
        //                       EMA HELPERS
        // ============================================================

        private static decimal ComputeEmaLast(IReadOnlyList<Candle> candles, int period)
        {
            if (candles == null || candles.Count == 0) return 0;

            var closes = candles
                .Skip(Math.Max(0, candles.Count - period * 3))
                .Select(c => c.Close)
                .ToArray();

            if (closes.Length == 0) return 0;

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

        // ============================================================
        //                    MOMENTUM REVERSAL
        // ============================================================

        private (bool reverse, bool hardReverse) CheckMomentumReversal(
            IReadOnlyList<Candle> candles15m, bool isLong, decimal entryPrice)
        {
            int i0 = candles15m.Count - 2;
            int i1 = candles15m.Count - 3;
            if (i1 < 0) return (false, false);

            var c0 = candles15m[i0];
            var c1 = candles15m[i1];

            decimal ema34 = ComputeEmaLast(candles15m, 34);
            decimal ema89 = ComputeEmaLast(candles15m, 89);
            decimal ema200 = ComputeEmaLast(candles15m, 200);

            decimal boundary = isLong
                ? GetDynamicBoundaryForLong(entryPrice, ema34, ema89, ema200)
                : GetDynamicBoundaryForShort(entryPrice, ema34, ema89, ema200);

            bool reverse;
            bool hard = false;

            if (isLong)
            {
                reverse = c0.Close < c0.Open && c0.Volume >= c1.Volume * 0.8m;
                if (boundary > 0 && c0.Close < boundary * (1 - EmaBreakTolerance)) hard = true;
            }
            else
            {
                reverse = c0.Close > c0.Open && c0.Volume >= c1.Volume * 0.8m;
                if (boundary > 0 && c0.Close > boundary * (1 + EmaBreakTolerance)) hard = true;
            }

            return (reverse, hard);
        }

        // ============================================================
        //                     UPDATE STOPLOSS (TRAILING)
        // ============================================================

        private async Task<DateTime> UpdateStopLossAsync(
            string symbol,
            decimal newSL,
            bool isLong,
            bool hasTp,
            decimal? expectedTp,
            PositionInfo currentPos,
            DateTime lastSlTpCheckUtc)
        {
            await _notify.SendAsync($"[{symbol}] Trailing SL update → {newSL}");

            await _exchange.CancelStopLossOrdersAsync(symbol);

            decimal qty = Math.Abs(currentPos?.PositionAmt ?? 0m);
            string posSide = isLong ? "LONG" : "SHORT";

            if (qty <= 0m)
            {
                var pos = await _exchange.GetPositionAsync(symbol);
                qty = Math.Abs(pos.PositionAmt);
                if (qty <= 0m)
                {
                    await _notify.SendAsync($"[{symbol}] Không tìm thấy position khi update SL.");
                    ClearMonitoringPosition(symbol);
                    return lastSlTpCheckUtc;
                }

                currentPos = pos;
            }

            string side = isLong ? "SELL" : "BUY";
            await _exchange.PlaceStopOnlyAsync(symbol, side, posSide, qty, newSL);

            // giữ TP: chỉ check lại theo throttle
            if (hasTp && expectedTp.HasValue)
            {
                if ((DateTime.UtcNow - lastSlTpCheckUtc) >= TimeSpan.FromSeconds(SlTpCheckEverySec))
                {
                    var det = await DetectManualSlTpAsync(symbol, isLong, currentPos.EntryPrice, currentPos);
                    lastSlTpCheckUtc = DateTime.UtcNow;

                    if (!det.Tp.HasValue)
                    {
                        decimal tpVal = expectedTp.Value;
                        decimal tpDisplay = Math.Round(tpVal, 6);

                        await _notify.SendAsync($"[{symbol}] Trailing giữ TP → đặt lại TP {tpDisplay}");

                        var ok = await _exchange.PlaceTakeProfitAsync(symbol, posSide, qty, tpVal);
                        if (!ok)
                            await _notify.SendAsync($"[{symbol}] Trailing giữ TP FAILED → tp={tpDisplay}");
                    }
                }
            }

            return lastSlTpCheckUtc;
        }
    }
}
