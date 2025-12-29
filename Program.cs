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

        // Choose active URL set by market (default: Futures)
        if (botConfig.Market == TradingMarket.Spot && botConfig.SpotUrls != null && !string.IsNullOrWhiteSpace(botConfig.SpotUrls.BaseUrl))
        {
            botConfig.Urls = botConfig.SpotUrls;
        }
        else if (botConfig.FuturesUrls != null && !string.IsNullOrWhiteSpace(botConfig.FuturesUrls.BaseUrl))
        {
            botConfig.Urls = botConfig.FuturesUrls;
        }


        // Singleton services
        // Exchange clients (separated by market domain)
        services.AddSingleton<IFuturesExchangeService, BinanceFuturesClientService>();
        services.AddSingleton<ISpotExchangeService, BinanceSpotClientService>();

        services.AddSingleton<IndicatorService>();
        services.AddSingleton<RiskManager>();
        services.AddSingleton<SlackNotifierService>();
        services.AddSingleton<PnlReporterService>();
        services.AddSingleton<LiveSyncService>();

        // Per-symbol scoped services
        services.AddScoped<OrderManagerService>();
        services.AddScoped<SpotOrderManagerService>();
        // Strategies are market-specific (SOLID: don't mix Spot/Futures assumptions)
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

await notifier.SendAsync($"=== {config.Market}Bot {config.CoinInfos.FirstOrDefault()?.MainTimeFrame.ToUpper()} - {nowVN:dd/MM/yyyy HH:mm:ss} started ===");

// ============================================================================
// GLOBAL COOLDOWN WATCHER
// ============================================================================

if (config.Market != TradingMarket.Spot)
{
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
}

// ============================================================================
// GLOBAL LIVE SYNC LOOP
// ============================================================================

if (config.Market != TradingMarket.Spot)
{
_ = Task.Run(async () =>
{
    while (!lifetime.ApplicationStopping.IsCancellationRequested)
    {
        try
        {
            await liveSync.SyncAsync(config.CoinInfos);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] LiveSyncLoop: {ex}");
        }

        await Task.Delay(TimeSpan.FromSeconds(5), lifetime.ApplicationStopping);
    }
});
}

// ============================================================================
// GLOBAL PNL SUMMARY LOOP
// ============================================================================

if (config.Market != TradingMarket.Spot)
{
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

var tasks = config.CoinInfos
    .Select(s => config.Market == TradingMarket.Spot
        ? RunSpotSymbolWorkerAsync(s, host, config, lifetime.ApplicationStopping)
        : RunFuturesSymbolWorkerAsync(s, host, config, pnl, lifetime.ApplicationStopping))
    .ToList();

await Task.WhenAll(tasks);

// ============================================================================
// WORKER PER SYMBOL
// - Fix: restart là attach manual NGAY (không chờ nến đóng)
// - Entry vẫn chỉ chạy theo nhịp nến đóng.
// ============================================================================

static async Task RunFuturesSymbolWorkerAsync(
    CoinInfo coinInfo,
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

    var mainTf = coinInfo.MainTimeFrame;
    var mainSpan = ParseFrameTime(mainTf);

    DateTime lastProcessedCandleOpenTimeUtc = DateTime.MinValue;

    // ==============================
    // (A) IMMEDIATE MANUAL ATTACH (FIX)
    // ==============================
    try
    {
        var posNow = await exchange.GetPositionAsync(coinInfo.Symbol);
        await orderManager.AttachManualPositionAsync(posNow);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[ERROR] ImmediateAttach {coinInfo.Symbol}: {ex}");
    }

    // ==============================
    // (B) POSITION WATCHER TICK (FIX)
    // - chạy nhanh để restart/redeploy không bị “đợi nến”
    // - chỉ attach manual nếu có position
    // ==============================
    var lastPosCheckUtc = DateTime.MinValue;
    var posCheckInterval = TimeSpan.FromSeconds(8);

    var afterCloseDelay = TimeSpan.FromSeconds(2);

    while (!ct.IsCancellationRequested)
    {
        try
        {
            // 1) position watcher tick nhanh (không phụ thuộc nến)
            if (DateTime.UtcNow - lastPosCheckUtc >= posCheckInterval)
            {
                lastPosCheckUtc = DateTime.UtcNow;

                var pos = await exchange.GetPositionAsync(coinInfo.Symbol);
                if (pos.PositionAmt != 0)
                {
                    await orderManager.AttachManualPositionAsync(pos);
                }
            }

            // 2) ngủ tới gần lúc nến đóng (để entry chạy theo nhịp nến)
            var sleep = GetDelayToNextCloseUtc(mainSpan) + afterCloseDelay;
            if (sleep < TimeSpan.FromMilliseconds(200))
                sleep = TimeSpan.FromMilliseconds(200);

            await Task.Delay(sleep, ct);

            // 3) fetch candles (chỉ khi tới nhịp nến)
            var candlesMainTf = await exchange.GetRecentCandlesAsync(
                coinInfo.Symbol, coinInfo.MainTimeFrame, 220);

            var candlesTrendTf = await exchange.GetRecentCandlesAsync(
                coinInfo.Symbol, coinInfo.TrendTimeFrame, 220);

            if (candlesMainTf == null || candlesMainTf.Count < 3 || candlesTrendTf == null || candlesTrendTf.Count < 3)
                continue;

            var lastClosed = candlesMainTf[^2];

            if (lastClosed.OpenTime <= lastProcessedCandleOpenTimeUtc)
                continue;

            lastProcessedCandleOpenTimeUtc = lastClosed.OpenTime;

            // 4) Attach manual thêm 1 lần nữa tại nhịp nến (phòng case watcher miss)
            var pos2 = await exchange.GetPositionAsync(coinInfo.Symbol);
            await orderManager.AttachManualPositionAsync(pos2);

            bool hasPosition = pos2.PositionAmt != 0;

            // 5) ENTRY LOGIC: chỉ khi không có position và không cooldown
            if (!hasPosition && !pnl.IsInCooldown())
            {
                var entrySignal = strategy.GenerateSignal(candlesMainTf, candlesTrendTf, coinInfo);
                await executor.HandleSignalAsync(entrySignal, coinInfo);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Worker {coinInfo.Symbol}: {ex}");
        }
    }
}

// ============================================================================
// SPOT WORKER (simple, long-only by holdings)
// - Strategy is reused, but execution is spot-native (no "position" mapping).
// - BUY/SELL MARKET only for now.
// ============================================================================

static async Task RunSpotSymbolWorkerAsync(
    CoinInfo coinInfo,
    IHost host,
    BotConfig config,
    CancellationToken ct)
{
    using var scope = host.Services.CreateScope();

    var strategy = scope.ServiceProvider.GetRequiredService<ISpotTradingStrategy>();
    var spot = scope.ServiceProvider.GetRequiredService<ISpotExchangeService>();
    var oms = scope.ServiceProvider.GetRequiredService<SpotOrderManagerService>();

    var mainTf = coinInfo.MainTimeFrame;
    var mainSpan = ParseFrameTime(mainTf);

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

            var candlesMainTf = await spot.GetRecentCandlesAsync(coinInfo.Symbol, coinInfo.MainTimeFrame, 220);
            var candlesTrendTf = await spot.GetRecentCandlesAsync(coinInfo.Symbol, coinInfo.TrendTimeFrame, 220);

            if (candlesMainTf == null || candlesMainTf.Count < 3 || candlesTrendTf == null || candlesTrendTf.Count < 3)
                continue;

            var lastClosed = candlesMainTf[^2];
            if (lastClosed.OpenTime <= lastProcessedCandleOpenTimeUtc)
                continue;

            lastProcessedCandleOpenTimeUtc = lastClosed.OpenTime;

            var signal = strategy.GenerateSignal(candlesMainTf, candlesTrendTf, coinInfo);
            await oms.TickAsync(signal, coinInfo, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] SpotWorker {coinInfo.Symbol}: {ex}");
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

    if (frameTime.EndsWith("m"))
    {
        if (int.TryParse(frameTime[..^1], out int mins) && mins > 0)
            return TimeSpan.FromMinutes(mins);
    }

    if (frameTime.EndsWith("h"))
    {
        if (int.TryParse(frameTime[..^1], out int hours) && hours > 0)
            return TimeSpan.FromHours(hours);
    }

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
