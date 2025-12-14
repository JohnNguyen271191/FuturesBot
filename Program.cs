using FuturesBot.Config;
using FuturesBot.IServices;
using FuturesBot.Services;
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

pnl.SetDailyBaseCapital(config.AccountBalance);

await notifier.SendAsync(
    $"=== FuturesBot {config.Intervals[0].FrameTime.ToUpper()} - {nowVN:dd/MM/yyyy HH:mm:ss} started ==="
);

// ============================================================================
// GLOBAL COOLDOWN WATCHER
// ============================================================================

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

// ============================================================================
// GLOBAL LIVE SYNC LOOP
// ============================================================================

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

// ============================================================================
// GLOBAL PNL SUMMARY LOOP
// ============================================================================

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

// ============================================================================
// LAUNCH PER-SYMBOL WORKERS
// ============================================================================

var tasks = config.CoinInfos
    .Select(s => RunSymbolWorkerAsync(
        s,
        host,
        config,
        pnl,
        lifetime.ApplicationStopping))
    .ToList();

await Task.WhenAll(tasks);

// ============================================================================
// WORKER PER SYMBOL
// - Fix: restart là attach manual NGAY (không chờ nến đóng)
// - Entry vẫn chỉ chạy theo nhịp nến đóng.
// ============================================================================

static async Task RunSymbolWorkerAsync(
    CoinInfo coinInfo,
    IHost host,
    BotConfig config,
    PnlReporterService pnl,
    CancellationToken ct)
{
    using var scope = host.Services.CreateScope();

    var strategy = scope.ServiceProvider.GetRequiredService<TradingStrategy>();
    var executor = scope.ServiceProvider.GetRequiredService<TradeExecutorService>();
    var exchange = scope.ServiceProvider.GetRequiredService<IExchangeClientService>();
    var orderManager = scope.ServiceProvider.GetRequiredService<OrderManagerService>();

    var mainTf = config.Intervals[0].FrameTime;
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
            var candles15m = await exchange.GetRecentCandlesAsync(
                coinInfo.Symbol, config.Intervals[0].FrameTime, 220);

            var candles1h = await exchange.GetRecentCandlesAsync(
                coinInfo.Symbol, config.Intervals[1].FrameTime, 220);

            if (candles15m == null || candles15m.Count < 3 || candles1h == null || candles1h.Count < 3)
                continue;

            var lastClosed = candles15m[^2];

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
                var entrySignal = strategy.GenerateSignal(candles15m, candles1h, coinInfo);
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
