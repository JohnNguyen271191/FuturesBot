using FuturesBot.Config;
using FuturesBot.IServices;
using FuturesBot.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using static FuturesBot.Utils.EnumTypesHelper;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var host = Host
    .CreateDefaultBuilder()
    .ConfigureServices((context, services) =>
    {
        // đọc config một lần
        var botConfig = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build()
            .Get<BotConfig>();

        if (botConfig == null)
            throw new ArgumentNullException(nameof(botConfig));

        // Đăng ký BotConfig cho DI
        services.AddSingleton(botConfig);
        services.AddSingleton<IExchangeClientService, BinanceFuturesClientService>();
        services.AddSingleton<IndicatorService>();
        services.AddSingleton<RiskManager>();
        services.AddSingleton<SlackNotifierService>();
        services.AddSingleton<PnlReporterService>();
        services.AddSingleton<LiveSyncService>();

        services.AddScoped<OrderManagerService>();        
        services.AddScoped<TradingStrategy>();
        services.AddScoped<TradeExecutorService>();        
    })
    .Build();

var config = host.Services.GetRequiredService<BotConfig>();

if (config is null)
{
    throw new ArgumentNullException(nameof(config));
}

var indicators = host.Services.GetRequiredService<IndicatorService>();
var strategy   = host.Services.GetRequiredService<TradingStrategy>();
var risk       = host.Services.GetRequiredService<RiskManager>();
var notifier   = host.Services.GetRequiredService<SlackNotifierService>();
var exchange = host.Services.GetRequiredService<IExchangeClientService>();
var orderManagerService = host.Services.GetRequiredService<OrderManagerService>();
var executor   = host.Services.GetRequiredService<TradeExecutorService>();
var pnl        = host.Services.GetRequiredService<PnlReporterService>();
var liveSync   = host.Services.GetRequiredService<LiveSyncService>();

var vnZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
var nowVN = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, vnZone);

await notifier.SendAsync($"=== FuturesBot {config.Intervals[0].FrameTime.ToUpper()} - {nowVN:dd/MM/yyyy HH:mm:ss} started ===");

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
            var candles15m = await exchange.GetRecentCandlesAsync(symbol.Coin, config.Intervals[0].FrameTime, 200);

            var candles1h = await exchange.GetRecentCandlesAsync(symbol.Coin, config.Intervals[1].FrameTime, 200);

            if (candles15m is null || candles15m.Count == 0)
                continue;

            var last15mCandle = candles15m[^1];
            var last15mTime   = last15mCandle.OpenTime;

            if (lastProcessedCandleTime15m.TryGetValue(symbol.Coin, out var lastTime) && lastTime >= last15mTime)
            {
                continue;
            }

            lastProcessedCandleTime15m[symbol.Coin] = last15mTime;

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

    await liveSync.SyncAsync(config.Symbols);

    await pnl.SendQuickDailySummary();

    await Task.Delay(TimeSpan.FromSeconds(30));
}
