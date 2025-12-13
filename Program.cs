using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FuturesBot.Config;
using FuturesBot.IServices;
using FuturesBot.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using static FuturesBot.Utils.EnumTypesHelper;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// ============================================================================
// CREATE HOST
// ============================================================================

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
// GLOBAL COOLDOWN WATCHER (NOTIFY 1 LẦN)
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

                await notifier.SendAsync(
                    $"BOT đang trong COOLDOWN (còn ~{mins:F0} phút) → không mở lệnh mới.");
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
// GLOBAL LIVE SYNC LOOP (chạy 1 lần, không spam theo coin)
// ============================================================================

_ = Task.Run(async () =>
{
    while (!lifetime.ApplicationStopping.IsCancellationRequested)
    {
        try
        {
            // Sync toàn bộ coin 1 lượt
            await liveSync.SyncAsync(config.CoinInfos);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] LiveSyncLoop: {ex}");
        }

        // Tần suất sync tuỳ mày, 10-20s thường đủ
        await Task.Delay(TimeSpan.FromSeconds(5), lifetime.ApplicationStopping);
    }
});

// ============================================================================
// GLOBAL PNL SUMMARY LOOP (không gọi trong worker per-coin nữa)
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

        // Đừng spam: 3-10 phút tuỳ nhu cầu
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
// WORKER PER SYMBOL (tối ưu: chỉ chạy theo nhịp nến đóng)
// - Worker KHÔNG tự ClosePosition nữa để tránh double-close.
// - Exit/Trailing/AutoTP giao hết cho OrderManagerService monitor.
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

    // interval chính để trigger (vd 15m)
    var mainTf = config.Intervals[0].FrameTime;
    var mainSpan = ParseFrameTime(mainTf);

    DateTime lastProcessedCandleOpenTimeUtc = DateTime.MinValue;

    // delay nhỏ để đợi sàn “ra nến” ổn định
    var afterCloseDelay = TimeSpan.FromSeconds(2);

    while (!ct.IsCancellationRequested)
    {
        try
        {
            // 1) ngủ đến gần lúc nến đóng (giảm poll 30s/coin)
            var sleep = GetDelayToNextCloseUtc(mainSpan) + afterCloseDelay;
            if (sleep < TimeSpan.FromMilliseconds(200))
                sleep = TimeSpan.FromMilliseconds(200);

            await Task.Delay(sleep, ct);

            // 2) fetch candles (chỉ khi tới nhịp nến)
            var candles15m = await exchange.GetRecentCandlesAsync(
                coinInfo.Symbol, config.Intervals[0].FrameTime, 220);

            var candles1h = await exchange.GetRecentCandlesAsync(
                coinInfo.Symbol, config.Intervals[1].FrameTime, 220);

            if (candles15m == null || candles15m.Count < 3 || candles1h == null || candles1h.Count < 3)
                continue;

            // FIX: dùng nến ĐÃ ĐÓNG
            var lastClosed = candles15m[^2];

            // Chỉ process mỗi nến 1 lần
            if (lastClosed.OpenTime <= lastProcessedCandleOpenTimeUtc)
                continue;

            lastProcessedCandleOpenTimeUtc = lastClosed.OpenTime;

            // 3) Sync position + attach manual (nếu có)
            var pos = await exchange.GetPositionAsync(coinInfo.Symbol);
            await orderManager.AttachManualPositionAsync(pos);

            bool hasPosition = pos.PositionAmt != 0;

            // 4) ENTRY LOGIC: chỉ tìm entry khi:
            //    - không cooldown
            //    - không có position
            if (!hasPosition && !pnl.IsInCooldown())
            {
                var entrySignal = strategy.GenerateSignal(candles15m, candles1h, coinInfo);
                await executor.HandleSignalAsync(entrySignal, coinInfo);
            }

            // NOTE: EXIT LOGIC đã giao cho OrderManagerService.MonitorPositionAsync
            // => tránh double-close.
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
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

    // formats: "15m", "1h", "4h"
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
    // Align theo Unix epoch để khớp với candle exchanges (Binance)
    long nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    long intSec = (long)Math.Max(1, interval.TotalSeconds);

    long nextCloseSec = ((nowSec / intSec) + 1) * intSec;
    long delta = nextCloseSec - nowSec;

    // Nếu delta=0 nghĩa là đúng khoảnh khắc đóng nến → return rất nhỏ
    if (delta < 0) delta = 0;

    return TimeSpan.FromSeconds(delta);
}
