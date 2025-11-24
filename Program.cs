using FuturesBot.Config;
using FuturesBot.IServices;
using FuturesBot.Services;
using Microsoft.Extensions.Configuration;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var config = new ConfigurationBuilder().AddJsonFile("appsettings.json", optional: false, reloadOnChange: false).Build().Get<BotConfig>();
if (config is null)
{
    throw new ArgumentNullException(nameof(config));
}
var indicators = new IndicatorService();
var strategy = new FifteenMinutesStrategy(indicators);
var risk = new RiskManager(config);
var notifier = new SlackNotifierService(config);
IExchangeClientService exchange = new BinanceFuturesClientService(config);
var executor = new TradeExecutorService(exchange, risk, config, notifier);
var pnl = new PnlReporterService(notifier);
var liveSync = new LiveSyncService(exchange, pnl);

await notifier.SendAsync("=== FuturesBot 15M started ===");

while (true)
{
    foreach (var symbol in config.Symbols)
    {
        try
        {
            var candles15m = await exchange.GetRecentCandlesAsync(
                symbol.Coin, config.Intervals[0].FrameTime, 200);
            var candles1h = await exchange.GetRecentCandlesAsync(
                symbol.Coin, config.Intervals[1].FrameTime, 200);

            var signal = strategy.GenerateSignal(candles15m, candles1h);

            await executor.HandleSignalAsync(signal, symbol);
        }
        catch (Exception ex)
        {
            await notifier.SendAsync("[ERROR] " + ex);
        }
    }

    // Đồng bộ vị thế thật -> PnL
    await liveSync.SyncAsync(config.Symbols);

    // gửi summary nếu có
    await pnl.SendQuickDailySummary();
    await Task.Delay(TimeSpan.FromSeconds(30));
}