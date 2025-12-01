using FuturesBot.Config;
using FuturesBot.IServices;
using FuturesBot.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using static FuturesBot.Utils.EnumTypesHelper;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// ============================================================================
// BUILD HOST + DI
// ============================================================================

var host = Host
    .CreateDefaultBuilder()
    .ConfigureServices((context, services) =>
    {
        // Load config
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

        // Scoped per symbol
        services.AddScoped<OrderManagerService>();
        services.AddScoped<TradingStrategy>();
        services.AddScoped<TradeExecutorService>();
    })
    .Build();

// ============================================================================
// GLOBAL SERVICES
// ============================================================================

var config = host.Services.GetRequiredService<BotConfig>();
var notifier = host.Services.GetRequiredService<SlackNotifierService>();
var liveSync = host.Services.GetRequiredService<LiveSyncService>();
var pnl = host.Services.GetRequiredService<PnlReporterService>();

var vnTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
var nowVN = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, vnTimeZone);

await notifier.SendAsync(
    $"=== FuturesBot {config.Intervals[0].FrameTime.ToUpper()} - {nowVN:dd/MM/yyyy HH:mm:ss} started ===");

// ============================================================================
// START WORKERS
// ============================================================================

// 1 worker cho mỗi symbol
var workerTasks = config.Symbols
    .Select(s => RunSymbolWorkerAsync(s, host, config, notifier))
    .ToList();

// 1 housekeeping loop dùng chung cho toàn bot
var housekeepingTask = RunHousekeepingLoopAsync(config.Symbols, liveSync, pnl, notifier);

// Chờ tất cả
workerTasks.Add(housekeepingTask);
await Task.WhenAll(workerTasks);


// ============================================================================
// WORKER PER SYMBOL
// ============================================================================

static async Task RunSymbolWorkerAsync(
    Symbol symbol,
    IHost host,
    BotConfig config,
    SlackNotifierService notifier)
{
    using var scope = host.Services.CreateScope();

    var strategy = scope.ServiceProvider.GetRequiredService<TradingStrategy>();
    var executor = scope.ServiceProvider.GetRequiredService<TradeExecutorService>();
    var exchange = scope.ServiceProvider.GetRequiredService<IExchangeClientService>();

    DateTime lastProcessedCandle = DateTime.MinValue;

    while (true)
    {
        try
        {
            // Lấy nến
            var candles15m = await exchange.GetRecentCandlesAsync(symbol.Coin, config.Intervals[0].FrameTime, 200);
            var candles1h = await exchange.GetRecentCandlesAsync(symbol.Coin, config.Intervals[1].FrameTime, 200);

            if (candles15m is null || candles15m.Count < 2)
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                continue;
            }

            var lastCandle = candles15m[^1];

            // Không xử lý lại cùng 1 cây
            if (lastCandle.OpenTime <= lastProcessedCandle)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                continue;
            }

            lastProcessedCandle = lastCandle.OpenTime;

            // Vị thế hiện tại
            var pos = await exchange.GetPositionAsync(symbol.Coin);
            bool hasLong = pos.PositionAmt > 0;
            bool hasShort = pos.PositionAmt < 0;

            // EXIT logic
            if (hasLong || hasShort)
            {
                var exitSignal = strategy.GenerateExitSignal(candles15m, hasLong, hasShort, symbol);

                if (exitSignal.Type == SignalType.CloseLong ||
                    exitSignal.Type == SignalType.CloseShort)
                {
                    await exchange.ClosePositionAsync(symbol.Coin, pos.PositionAmt);
                    continue;
                }
            }

            // ENTRY logic
            var entrySignal = strategy.GenerateSignal(candles15m, candles1h, symbol);
            await executor.HandleSignalAsync(entrySignal, symbol);
        }
        catch (Exception ex)
        {
            // Quan trọng: catch mọi exception trong worker để loop không chết
            await notifier.SendAsync($"[ERROR] {symbol.Coin}: {ex}");
            await Task.Delay(TimeSpan.FromSeconds(10));
        }

        // Tick interval cho mỗi symbol
        await Task.Delay(TimeSpan.FromSeconds(30));
    }
}

// ============================================================================
// HOUSEKEEPING: LiveSync + PnL dùng chung
// ============================================================================

static async Task RunHousekeepingLoopAsync(
    Symbol[] symbols,
    LiveSyncService liveSync,
    PnlReporterService pnl,
    SlackNotifierService notifier)
{
    while (true)
    {
        try
        {
            await liveSync.SyncAsync(symbols);
            await pnl.SendQuickDailySummary();
        }
        catch (Exception ex)
        {
            await notifier.SendAsync($"[HOUSEKEEPING ERROR] {ex}");
        }

        // Sync / PnL mỗi 30s (m muốn có thể tăng lên 60s cho nhẹ)
        await Task.Delay(TimeSpan.FromSeconds(30));
    }
}