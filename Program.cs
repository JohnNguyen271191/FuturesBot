using FuturesBot.Config;
using FuturesBot.IServices;
using FuturesBot.Services;
using Microsoft.Extensions.Configuration;
using static FuturesBot.Utils.EnumTypesHelper;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .Build()
    .Get<BotConfig>();

if (config is null)
{
    throw new ArgumentNullException(nameof(config));
}

var indicators = new IndicatorService();
var strategy   = new TradingStrategy(indicators);
var risk       = new RiskManager(config);
var notifier   = new SlackNotifierService(config);
IExchangeClientService exchange = new BinanceFuturesClientService(config);
var executor   = new TradeExecutorService(exchange, risk, config, notifier);
var pnl        = new PnlReporterService(notifier);
var liveSync   = new LiveSyncService(exchange, pnl);

await notifier.SendAsync($"=== FuturesBot {config.Intervals[0].FrameTime.ToUpper()} - {DateTime.Now:dd/MM/yyyy HH:mm:ss} started ===");

// Lưu thời gian cây 15m cuối cùng đã xử lý cho mỗi symbol
// Key: symbol.Coin (vd: "BTCUSDT"), Value: thời gian đóng nến 15m cuối cùng đã xử lý
var lastProcessedCandleTime15m = config.Symbols.ToDictionary(s => s.Coin, _ => DateTime.MinValue);

while (true)
{
    foreach (var symbol in config.Symbols)
    {
        try
        {
            // Lấy 200 cây nến gần nhất
            var candles15m = await exchange.GetRecentCandlesAsync(
                symbol.Coin,
                config.Intervals[0].FrameTime,
                200);

            var candles1h = await exchange.GetRecentCandlesAsync(
                symbol.Coin,
                config.Intervals[1].FrameTime,
                200);

            if (candles15m is null || candles15m.Count == 0)
                continue;

            // ===== XÁC ĐỊNH CÂY 15M ĐÃ ĐÓNG GẦN NHẤT =====
            var last15mCandle = candles15m[^1];
            var last15mTime   = last15mCandle.OpenTime;

            // Nếu nến này đã được xử lý rồi -> bỏ qua, không generate signal nữa
            if (lastProcessedCandleTime15m.TryGetValue(symbol.Coin, out var lastTime) && lastTime >= last15mTime)
            {
                continue;
            }

            // Cập nhật lại thời gian đã xử lý cho symbol này
            lastProcessedCandleTime15m[symbol.Coin] = last15mTime;

            // LẤY TRẠNG THÁI VỊ THẾ HIỆN TẠI
            var pos = await exchange.GetPositionAsync(symbol.Coin);
            bool hasLongPosition = pos.PositionAmt > 0;
            bool hasShortPosition = pos.PositionAmt < 0;

            if (pos.IsLong || pos.IsShort)
            {
                var exitSignal = strategy.GenerateExitSignal(candles15m, hasLongPosition, hasShortPosition, symbol);

                if (exitSignal.Type == SignalType.CloseLong || exitSignal.Type == SignalType.CloseShort)
                {
                    await exchange.ClosePositionAsync(symbol.Coin, pos.PositionAmt);
                    continue;
                }
            }

            var entrySignal = strategy.GenerateSignal(candles15m, candles1h, symbol);

            await executor.HandleSignalAsync(entrySignal, symbol);
        }
        catch (Exception ex)
        {
            await notifier.SendAsync("[ERROR] " + ex);
        }
    }

    // Đồng bộ vị thế thật -> PnL (vẫn 30s 1 lần)
    await liveSync.SyncAsync(config.Symbols);

    // Gửi summary nếu có
    await pnl.SendQuickDailySummary();

    // Loop 30s
    await Task.Delay(TimeSpan.FromSeconds(30));
}
