using FuturesBot.Config;
using FuturesBot.IServices;
using FuturesBot.Models;
using FuturesBot.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using static FuturesBot.Utils.EnumTypesHelper;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// Create Host
var host = Host
    .CreateDefaultBuilder()
    .ConfigureServices((context, services) =>
    {
        // Load config once
        var botConfig = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build()
            .Get<BotConfig>() ?? throw new Exception("Cannot load BotConfig");

        services.AddSingleton(botConfig);

        // Singleton services
        services.AddSingleton<IExchangeClientService, BinanceFuturesClientService>();
        services.AddSingleton<IndicatorService>();
        services.AddSingleton<RiskManager>();
        services.AddSingleton<SlackNotifierService>();
        services.AddSingleton<PnlReporterService>();
        services.AddSingleton<LiveSyncService>();

        // Per-symbol scoped services
        services.AddScoped<OrderManagerService>();
        services.AddScoped<TradingStrategy>();
        services.AddScoped<TradeExecutorService>();
    })
    .Build();

// Resolve global services
var config = host.Services.GetRequiredService<BotConfig>();
var notifier = host.Services.GetRequiredService<SlackNotifierService>();
var liveSync = host.Services.GetRequiredService<LiveSyncService>();
var pnl = host.Services.GetRequiredService<PnlReporterService>();

var vnTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
var nowVN = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, vnTimeZone);

await notifier.SendAsync(
    $"=== FuturesBot {config.Intervals[0].FrameTime.ToUpper()} - {nowVN:dd/MM/yyyy HH:mm:ss} started ==="
);

// Launch per-symbol workers
var tasks = config.Symbols
    .Select(s => RunSymbolWorkerAsync(s, host, config, notifier, liveSync, pnl))
    .ToList();

await Task.WhenAll(tasks);

// ============================================================================
// WORKER PER SYMBOL
// ============================================================================

static async Task RunSymbolWorkerAsync(
    Symbol symbol,
    IHost host,
    BotConfig config,
    SlackNotifierService notifier,
    LiveSyncService liveSync,
    PnlReporterService pnl)
{
    // Each symbol has its own scope + own strategy / executor / order manager
    using var scope = host.Services.CreateScope();

    var strategy = scope.ServiceProvider.GetRequiredService<TradingStrategy>();
    var executor = scope.ServiceProvider.GetRequiredService<TradeExecutorService>();
    var exchange = scope.ServiceProvider.GetRequiredService<IExchangeClientService>();
    var orderManager = scope.ServiceProvider.GetRequiredService<OrderManagerService>();

    DateTime lastProcessedCandle = DateTime.MinValue;

    while (true)
    {
        try
        {
            // Lấy candles 1 lần cho vòng lặp này
            var candles15m = await exchange.GetRecentCandlesAsync(
                symbol.Coin, config.Intervals[0].FrameTime, 200);

            var candles1h = await exchange.GetRecentCandlesAsync(
                symbol.Coin, config.Intervals[1].FrameTime, 200);

            if (candles15m?.Count >= 2)
            {
                var lastCandle = candles15m[^1];

                // Chỉ xử lý khi có nến 15m mới
                if (lastCandle.OpenTime > lastProcessedCandle)
                {
                    lastProcessedCandle = lastCandle.OpenTime;

                    //Lấy position 1 lần
                    var pos = await exchange.GetPositionAsync(symbol.Coin);
                    await orderManager.AttachManualPositionAsync(pos);

                    bool hasLong = pos.PositionAmt > 0;
                    bool hasShort = pos.PositionAmt < 0;
                    //EXIT logic
                    if (hasLong || hasShort)
                    {
                        var exitSignal = strategy.GenerateExitSignal(
                            candles15m, hasLong, hasShort, symbol);

                        if (exitSignal.Type == SignalType.CloseLong ||
                            exitSignal.Type == SignalType.CloseShort)
                        {
                            await exchange.ClosePositionAsync(symbol.Coin, pos.PositionAmt);
                            // không continue; vẫn cho liveSync/pnl chạy bên dưới
                        }
                    }                    

                    // ENTRY logic
                    var entrySignal = strategy.GenerateSignal(candles15m, candles1h, symbol);
                    await executor.HandleSignalAsync(entrySignal, symbol);
                }
                // nếu chưa có nến mới thì bỏ qua phần xử lý trade,
                // nhưng vẫn chạy liveSync/pnl ở dưới
            }
        }
        catch (Exception ex)
        {
            await notifier.SendAsync($"[ERROR] {symbol.Coin}: {ex}");
        }

        // Các tác vụ chia sẻ, luôn chạy mỗi vòng (khoảng 30s/lần)
        await liveSync.SyncAsync(new[] { symbol });
        await pnl.SendQuickDailySummary();

        // Tick interval
        await Task.Delay(TimeSpan.FromSeconds(30));
    }
}