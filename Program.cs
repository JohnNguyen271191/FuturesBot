using FuturesBot.Config;
using FuturesBot.IServices;
using FuturesBot.Services;
using FuturesBot.Strategies;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// ============================================================================
// CREATE HOST
// ============================================================================

var host = Host
    .CreateDefaultBuilder()
    .ConfigureServices((context, services) =>
    {
        var botConfig = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build()
            .Get<BotConfig>() ?? throw new Exception("Cannot load BotConfig");

        services.AddSingleton(botConfig);

        // Exchange clients
        services.AddSingleton<IFuturesExchangeService, BinanceFuturesClientService>();
        services.AddSingleton<ISpotExchangeService, BinanceSpotClientService>();

        // Core services
        services.AddSingleton<IndicatorService>();
        services.AddSingleton<RiskManager>();
        services.AddSingleton<SlackNotifierService>();
        services.AddSingleton<PnlReporterService>();
        services.AddSingleton<LiveSyncService>();

        // OMS & Strategies
        services.AddScoped<OrderManagerService>();            // Futures OMS
        services.AddScoped<SpotOrderManagerService>();        // Spot OMS

        services.AddScoped<IFuturesTradingStrategy, FuturesTrendStrategy>();
        services.AddScoped<ISpotTradingStrategy, SpotScalpStrategy1m>();

        services.AddScoped<TradeExecutorService>();
    })
    .Build();

// ============================================================================
// RESOLVE GLOBAL SERVICES
// ============================================================================

var config = host.Services.GetRequiredService<BotConfig>();
var notifier = host.Services.GetRequiredService<SlackNotifierService>();
var liveSync = host.Services.GetRequiredService<LiveSyncService>();
var pnl = host.Services.GetRequiredService<PnlReporterService>();
var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

var vnTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
var nowVN = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, vnTimeZone);

pnl.SetDailyBaseCapital();

string tfLabel = config.Market == TradingMarket.Spot
    ? (config.Spot.Coins.FirstOrDefault()?.MainTimeFrame ?? "?")
    : (config.Futures.Coins.FirstOrDefault()?.MainTimeFrame ?? "?");

await notifier.SendAsync($"=== {config.Market}Bot {tfLabel.ToUpper()} - {nowVN:dd/MM/yyyy HH:mm:ss} started ===");

// ============================================================================
// FUTURES-ONLY GLOBAL LOOPS
// ============================================================================

if (config.Market == TradingMarket.Futures)
{
    // Cooldown watcher
    _ = Task.Run(async () =>
    {
        bool notified = false;

        while (!lifetime.ApplicationStopping.IsCancellationRequested)
        {
            try
            {
                bool inCooldown = pnl.IsInCooldown();

                if (inCooldown && !notified)
                {
                    notified = true;
                    var remain = pnl.GetCooldownRemaining();
                    var mins = Math.Max(0, remain?.TotalMinutes ?? 0);

                    await notifier.SendAsync($"BOT đang trong COOLDOWN (còn ~{mins:F0} phút) → không mở lệnh mới.");
                }
                else if (!inCooldown && notified)
                {
                    notified = false;
                    await notifier.SendAsync("COOLDOWN kết thúc → BOT tiếp tục tìm ENTRY.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] CooldownWatcher: {ex}");
            }

            await Task.Delay(TimeSpan.FromSeconds(15), lifetime.ApplicationStopping);
        }
    });

    // Live sync loop
    _ = Task.Run(async () =>
    {
        while (!lifetime.ApplicationStopping.IsCancellationRequested)
        {
            try
            {
                await liveSync.SyncAsync(config.Futures.Coins);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] LiveSyncLoop: {ex}");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), lifetime.ApplicationStopping);
        }
    });

    // Daily PnL summary
    _ = Task.Run(async () =>
    {
        while (!lifetime.ApplicationStopping.IsCancellationRequested)
        {
            try
            {
                await pnl.SendQuickDailySummary();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] PnlSummaryLoop: {ex}");
            }

            await Task.Delay(TimeSpan.FromSeconds(10), lifetime.ApplicationStopping);
        }
    });
}

// ============================================================================
// LAUNCH PER-SYMBOL WORKERS
// ============================================================================

List<Task> tasks =
    config.Market == TradingMarket.Spot
        ? config.Spot.Coins.Select(c => RunSpotSymbolWorkerAsync(c, host, config, lifetime.ApplicationStopping)).ToList()
        : config.Futures.Coins.Select(c => RunFuturesSymbolWorkerAsync(c, host, config, pnl, lifetime.ApplicationStopping)).ToList();

await Task.WhenAll(tasks);

// ============================================================================
// FUTURES WORKER
// ============================================================================

static async Task RunFuturesSymbolWorkerAsync(
    FuturesCoinConfig coin,
    IHost host,
    BotConfig config,
    PnlReporterService pnl,
    CancellationToken ct)
{
    using var scope = host.Services.CreateScope();

    var strategy = scope.ServiceProvider.GetRequiredService<IFuturesTradingStrategy>();
    var executor = scope.ServiceProvider.GetRequiredService<TradeExecutorService>();
    var exchange = scope.ServiceProvider.GetRequiredService<IFuturesExchangeService>();
    var orderManager = scope.ServiceProvider.GetRequiredService<OrderManagerService>();

    var mainSpan = ParseFrameTime(coin.MainTimeFrame);
    DateTime lastProcessedCandleOpenTimeUtc = DateTime.MinValue;

    // Immediate manual attach (restart safety)
    try
    {
        var posNow = await exchange.GetPositionAsync(coin.Symbol);
        await orderManager.AttachManualPositionAsync(posNow);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[ERROR] ImmediateAttach {coin.Symbol}: {ex}");
    }

    var posCheckInterval = TimeSpan.FromSeconds(8);
    var lastPosCheckUtc = DateTime.MinValue;
    var afterCloseDelay = TimeSpan.FromSeconds(2);

    while (!ct.IsCancellationRequested)
    {
        try
        {
            // Fast position watcher
            if (DateTime.UtcNow - lastPosCheckUtc >= posCheckInterval)
            {
                lastPosCheckUtc = DateTime.UtcNow;

                var pos = await exchange.GetPositionAsync(coin.Symbol);
                if (pos.PositionAmt != 0)
                    await orderManager.AttachManualPositionAsync(pos);
            }

            // Sleep until candle close
            var sleep = GetDelayToNextCloseUtc(mainSpan) + afterCloseDelay;
            if (sleep < TimeSpan.FromMilliseconds(200))
                sleep = TimeSpan.FromMilliseconds(200);

            await Task.Delay(sleep, ct);

            var candlesMain = await exchange.GetRecentCandlesAsync(coin.Symbol, coin.MainTimeFrame, 220);
            var candlesTrend = await exchange.GetRecentCandlesAsync(coin.Symbol, coin.TrendTimeFrame, 220);

            if (candlesMain.Count < 3 || candlesTrend.Count < 3)
                continue;

            var lastClosed = candlesMain[^2];
            if (lastClosed.OpenTime <= lastProcessedCandleOpenTimeUtc)
                continue;

            lastProcessedCandleOpenTimeUtc = lastClosed.OpenTime;

            var pos2 = await exchange.GetPositionAsync(coin.Symbol);
            await orderManager.AttachManualPositionAsync(pos2);

            if (pos2.PositionAmt == 0 && !pnl.IsInCooldown())
            {
                var signal = strategy.GenerateSignal(candlesMain, candlesTrend, coin);
                await executor.HandleSignalAsync(signal, coin);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] FuturesWorker {coin.Symbol}: {ex}");
        }
    }
}

// ============================================================================
// SPOT WORKER
// ============================================================================

static async Task RunSpotSymbolWorkerAsync(
    SpotCoinConfig coin,
    IHost host,
    BotConfig config,
    CancellationToken ct)
{
    using var scope = host.Services.CreateScope();

    var strategy = scope.ServiceProvider.GetRequiredService<ISpotTradingStrategy>();
    var spot = scope.ServiceProvider.GetRequiredService<ISpotExchangeService>();
    var oms = scope.ServiceProvider.GetRequiredService<SpotOrderManagerService>();

    // Resume spot position on startup
    await oms.RecoverAsync(new[] { coin }, ct);

    var mainSpan = ParseFrameTime(coin.MainTimeFrame);
    DateTime lastProcessedCandleOpenTimeUtc = DateTime.MinValue;
    var afterCloseDelay = TimeSpan.FromSeconds(2);

    while (!ct.IsCancellationRequested)
    {
        try
        {
            var sleep = GetDelayToNextCloseUtc(mainSpan) + afterCloseDelay;
            if (sleep < TimeSpan.FromMilliseconds(200))
                sleep = TimeSpan.FromMilliseconds(200);

            await Task.Delay(sleep, ct);

            var candlesMain = await spot.GetRecentCandlesAsync(coin.Symbol, coin.MainTimeFrame, 220);
            var candlesTrend = await spot.GetRecentCandlesAsync(coin.Symbol, coin.TrendTimeFrame, 220);

            if (candlesMain.Count < 3 || candlesTrend.Count < 3)
                continue;

            var lastClosed = candlesMain[^2];
            if (lastClosed.OpenTime <= lastProcessedCandleOpenTimeUtc)
                continue;

            lastProcessedCandleOpenTimeUtc = lastClosed.OpenTime;

            var signal = strategy.GenerateSignal(candlesMain, candlesTrend, coin);
            await oms.TickAsync(signal, coin, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] SpotWorker {coin.Symbol}: {ex}");
        }
    }
}

// ============================================================================
// HELPERS
// ============================================================================

static TimeSpan ParseFrameTime(string frameTime)
{
    if (string.IsNullOrWhiteSpace(frameTime))
        throw new ArgumentException("frameTime is empty");

    frameTime = frameTime.Trim().ToLowerInvariant();

    if (frameTime.EndsWith("m") && int.TryParse(frameTime[..^1], out int mins) && mins > 0)
        return TimeSpan.FromMinutes(mins);

    if (frameTime.EndsWith("h") && int.TryParse(frameTime[..^1], out int hours) && hours > 0)
        return TimeSpan.FromHours(hours);

    throw new ArgumentException($"Unsupported FrameTime: {frameTime}");
}

static TimeSpan GetDelayToNextCloseUtc(TimeSpan interval)
{
    long nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    long intSec = (long)Math.Max(1, interval.TotalSeconds);

    long nextCloseSec = ((nowSec / intSec) + 1) * intSec;
    long delta = nextCloseSec - nowSec;
    if (delta < 0) delta = 0;

    return TimeSpan.FromSeconds(delta);
}
