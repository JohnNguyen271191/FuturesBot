using FuturesBot.Config;
using FuturesBot.IServices;
using FuturesBot.Models;
using static FuturesBot.Utils.EnumTypesHelper;

namespace FuturesBot.Services
{
    public class TradeExecutorService(
        IExchangeClientService exchange,
        RiskManager risk,
        BotConfig config,
        SlackNotifierService notifier,
        OrderManagerService orderManagerService)
    {
        private readonly IExchangeClientService _exchange = exchange;
        private readonly RiskManager _risk = risk;
        private readonly BotConfig _config = config;
        private readonly SlackNotifierService _notifier = notifier;
        private readonly OrderManagerService _orderManagerService = orderManagerService;

        public async Task HandleSignalAsync(TradeSignal signal, Symbol symbol)
        {
            if (signal.Type == SignalType.None) return;

            if (signal.Type == SignalType.Info)
            {
                await _notifier.SendAsync($"[INFO] {signal.Reason}");
                return;
            }

            // NEW: chặn nếu đã có vị thế hoặc lệnh chờ
            if (await _exchange.HasOpenPositionOrOrderAsync(symbol.Coin))
            {
                await _notifier.SendAsync($"[BLOCKED] - {symbol.Coin} - Already have open position or pending order. Skip new signal : {signal.Reason}");
                return;
            }

            if (!_risk.CanOpenNewTrade())
            {
                await _notifier.SendAsync($"[BLOCKED] - {symbol.Coin} - Not eligible to open a position. Reason: {signal.Reason}");
                return;
            }

            if (signal.EntryPrice is null || signal.StopLoss is null || signal.TakeProfit is null)
            {
                await _notifier.SendAsync($"[WARN] - {symbol.Coin} - The Signal not enough Entry/SL/TP.");
                return;
            }

            var entry = signal.EntryPrice.Value;
            var sl = signal.StopLoss.Value;
            var tp = signal.TakeProfit.Value;

            var qty = _risk.CalculatePositionSize(entry, sl);
            if (qty <= 0)
            {
                await _notifier.SendAsync($"[WARN] - {symbol.Coin} - Position size = 0, signal ignore.");
                return;
            }

            var msg =
    $@"================ SIGNAL ================
Time : {signal.Time:yyyy-MM-dd HH:mm:ss} UTC
Type : {signal.Type}
Symbol: {symbol.Coin}
Entry: {entry}
SL   : {sl}
TP   : {tp}
Size : {qty:F6} {symbol.Coin.Replace("USDT", "")}
Reason: {signal.Reason}
PaperMode: {_config.PaperMode}
========================================";

            await _notifier.SendAsync(msg);

            if (_config.PaperMode)
            {
                return;
            }

            var isOrdered = await _exchange.PlaceFuturesOrderAsync(symbol.Coin, signal.Type, qty, entry, sl, tp, symbol.Leverage, _notifier, marketOrder: false);

            if (isOrdered)
            {
                await _notifier.SendAsync($"[INFO] - {symbol.Coin} - API call sent to place the order (check Binance).");
                await _orderManagerService.MonitorLimitOrderAsync(signal);
            } else
            {
                await _notifier.SendAsync($"[ERROR] - {symbol.Coin} - API calln't sent to place the order (check Binance).");
            }            
        }
    }
}
