using FuturesBot.Config;
using FuturesBot.IServices;
using FuturesBot.Models;
using static FuturesBot.Utils.EnumTypesHelper;

namespace FuturesBot.Services
{
    public class TradeExecutorService(
        IFuturesExchangeService exchange,
        RiskManager risk,
        BotConfig config,
        SlackNotifierService notifier,
        OrderManagerService orderManagerService)
    {
        private readonly IFuturesExchangeService _exchange = exchange;
        private readonly RiskManager _risk = risk;
        private readonly BotConfig _config = config;
        private readonly SlackNotifierService _notifier = notifier;
        private readonly OrderManagerService _orderManagerService = orderManagerService;

        public async Task HandleSignalAsync(TradeSignal signal, CoinInfo coinInfo)
        {
            if (signal.Type == SignalType.None) return;

            if (signal.Type == SignalType.Info)
            {
                await _notifier.SendAsync($"[INFO] {signal.Reason}");
                return;
            }

            // NEW: chặn nếu đã có vị thế hoặc lệnh chờ
            if (await _exchange.HasOpenPositionOrOrderAsync(coinInfo.Symbol))
            {
                return;
            }

            if (signal.EntryPrice is null || signal.StopLoss is null || signal.TakeProfit is null)
            {
                return;
            }

            var entry = signal.EntryPrice.Value;
            var sl = signal.StopLoss.Value;
            var tp = signal.TakeProfit.Value;

            var allocation = coinInfo.AllocationPercent > 0 ? coinInfo.AllocationPercent : 100m;
            var totalCap = _config.Futures.WalletCapUsd > 0 ? _config.Futures.WalletCapUsd : _config.AccountBalance;
            var coinCap = totalCap * allocation / 100m;

            var riskPct = coinInfo.RiskPerTradePercent > 0
                ? coinInfo.RiskPerTradePercent
                : (_config.Futures.DefaultRiskPerTradePercent > 0 ? _config.Futures.DefaultRiskPerTradePercent : 1m);

            var qty = _risk.CalculatePositionSize(entry, sl, coinCap, riskPct);
            if (qty <= 0)
            {
                return;
            }

            var msg =
    $@"================ SIGNAL ================
Time : {signal.Time:yyyy-MM-dd HH:mm:ss} UTC
Type : {signal.Type}
Symbol: {coinInfo.Symbol}
Entry: {entry}
SL   : {sl}
TP   : {tp}
Cap  : {coinCap:0.####} (alloc={allocation:0.##}%, risk={riskPct:0.##}%)
Size : {qty:F6} {coinInfo.Symbol.Replace("USDT", "")}
Reason: {signal.Reason}
PaperMode: {_config.PaperMode}
========================================";

            await _notifier.SendAsync(msg);

            if (_config.PaperMode)
            {
                return;
            }

            var isOrdered = await _exchange.PlaceFuturesOrderAsync(coinInfo.Symbol, signal.Type, qty, entry, sl, tp, coinInfo.Leverage, _notifier, marketOrder: false);

            if (isOrdered)
            {
                await _notifier.SendAsync($"[INFO] - {coinInfo.Symbol} - API call sent to place the order (check Binance).");
                await _orderManagerService.MonitorLimitOrderAsync(signal);
            } 
            else
            {
                await _notifier.SendAsync($"[ERROR] - {coinInfo.Symbol} - API calln't sent to place the order (check Binance).");
            }            
        }
    }
}
