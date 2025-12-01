using System.Net.Http;
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

    // Báo 1 lần khi worker cho coin này được khởi động
    await notifier.SendAsync($"[INFO] Worker started for {symbol.Coin}");

    // Delay random lúc start để các symbol không bắn API cùng lúc
    var startJitterMs = Random.Shared.Next(1000, 8000);
    await Task.Delay(startJitterMs);

    // Chu kỳ loop phụ thuộc timeframe (3m/5m -> ~60s, v.v.)
    var loopDelay = GetLoopDelayFromConfig(config);

    DateTime lastProcessedCandle = DateTime.MinValue;

    while (true)
    {
        try
        {
            // Lấy nến: timeframe nhanh + chậm
            var candlesFast = await exchange.GetRecentCandlesAsync(
                symbol.Coin, config.Intervals[0].FrameTime, 200);
            var candlesSlow = await exchange.GetRecentCandlesAsync(
                symbol.Coin, config.Intervals[1].FrameTime, 200);

            if (candlesFast is null || candlesFast.Count < 2)
            {
                await Task.Delay(loopDelay);
                continue;
            }

            var lastCandle = candlesFast[^1];

            // Không xử lý lại cùng 1 cây
            if (lastCandle.OpenTime <= lastProcessedCandle)
            {
                await Task.Delay(loopDelay);
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
                var exitSignal = strategy.GenerateExitSignal(candlesFast, hasLong, hasShort, symbol);

                if (exitSignal.Type == SignalType.CloseLong ||
                    exitSignal.Type == SignalType.CloseShort)
                {
                    await exchange.ClosePositionAsync(symbol.Coin, pos.PositionAmt);

                    // chờ cây mới rồi hãy xử lý tiếp
                    await Task.Delay(loopDelay);
                    continue;
                }
            }

            // ENTRY logic
            var entrySignal = strategy.GenerateSignal(candlesFast, candlesSlow, symbol);
            await executor.HandleSignalAsync(entrySignal, symbol);
        }
        catch (Exception ex)
        {
            // Phân loại lỗi rate-limit 418 / 429
            var msg = ex.ToString();

            bool is418 = msg.Contains("418", StringComparison.OrdinalIgnoreCase)
                         && msg.Contains("teapot", StringComparison.OrdinalIgnoreCase);
            bool is429 = msg.Contains("429", StringComparison.OrdinalIgnoreCase)
                         || msg.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase);

            string type;
            TimeSpan delay;

            if (is418)
            {
                type = "RATE_LIMIT_418";
                delay = TimeSpan.FromSeconds(60); // bị block cứng, nghỉ lâu
            }
            else if (is429)
            {
                type = "RATE_LIMIT_429";
                delay = TimeSpan.FromSeconds(20); // soft limit, nghỉ vừa vừa
            }
            else
            {
                type = "ERROR";
                delay = TimeSpan.FromSeconds(10);
            }

            await notifier.SendAsync($"[{type}] {symbol.Coin}: {ex.Message}");

            await Task.Delay(delay);
            continue; // skip delay ở cuối vòng, vì đã delay ở đây rồi
        }

        // Tick interval cho mỗi symbol (thêm jitter nhẹ tránh trùng)
        var jitterMs = Random.Shared.Next(0, 2000);
        await Task.Delay(loopDelay + TimeSpan.FromMilliseconds(jitterMs));
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

        // Sync / PnL mỗi 30s
        await Task.Delay(TimeSpan.FromSeconds(30));
    }
}

// ============================================================================
// HELPERS: chuyển timeframe -> delay hợp lý
// ============================================================================

static TimeSpan GetLoopDelayFromConfig(BotConfig config)
{
    // Lấy thời gian nhỏ nhất trong 2 timeframe (vd 3m và 5m -> 3m)
    var tfSeconds = config.Intervals
        .Select(i => ParseFrameTimeToSeconds(i.FrameTime))
        .Where(s => s > 0)
        .DefaultIfEmpty(30)
        .Min();

    // Poll khoảng 1/3 timeframe, clamp trong [10s, 60s]
    var pollSeconds = Math.Clamp(tfSeconds / 3, 10, 60);

    return TimeSpan.FromSeconds(pollSeconds);
}

static int ParseFrameTimeToSeconds(string frame)
{
    if (string.IsNullOrWhiteSpace(frame)) return 0;

    frame = frame.Trim().ToLowerInvariant();

    return frame switch
    {
        "1m" or "m1" => 60,
        "3m" or "m3" => 3 * 60,
        "5m" or "m5" => 5 * 60,
        "15m" or "m15" => 15 * 60,
        "30m" or "m30" => 30 * 60,

        "1h" or "h1" => 60 * 60,
        "2h" or "h2" => 2 * 60 * 60,
        "4h" or "h4" => 4 * 60 * 60,

        "1d" or "d1" => 24 * 60 * 60,

        _ => 0 // nếu không parse được thì để 0, sẽ dùng default 30s
    };
}
