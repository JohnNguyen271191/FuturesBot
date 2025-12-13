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
// LAUNCH PER-SYMBOL WORKERS
// ============================================================================

var tasks = config.CoinInfos
    .Select(s => RunSymbolWorkerAsync(
        s,
        host,
        config,
        liveSync,
        pnl,
        lifetime.ApplicationStopping))
    .ToList();

await Task.WhenAll(tasks);

// ============================================================================
// WORKER PER SYMBOL
// ============================================================================

static async Task RunSymbolWorkerAsync(
    CoinInfo coinInfo,
    IHost host,
    BotConfig config,
    LiveSyncService liveSync,
    PnlReporterService pnl,
    CancellationToken ct)
{
    using var scope = host.Services.CreateScope();

    var strategy = scope.ServiceProvider.GetRequiredService<TradingStrategy>();
    var executor = scope.ServiceProvider.GetRequiredService<TradeExecutorService>();
    var exchange = scope.ServiceProvider.GetRequiredService<IExchangeClientService>();
    var orderManager = scope.ServiceProvider.GetRequiredService<OrderManagerService>();

    DateTime lastProcessedCandleUtc = DateTime.MinValue;

    while (!ct.IsCancellationRequested)
    {
        try
        {
            var candles15m = await exchange.GetRecentCandlesAsync(
                coinInfo.Symbol, config.Intervals[0].FrameTime, 200);

            var candles1h = await exchange.GetRecentCandlesAsync(
                coinInfo.Symbol, config.Intervals[1].FrameTime, 200);

            if (candles15m != null && candles15m.Count >= 2)
            {
                // FIX: dùng nến ĐÃ ĐÓNG
                var lastClosed = candles15m[^2];

                if (lastClosed.OpenTime > lastProcessedCandleUtc)
                {
                    lastProcessedCandleUtc = lastClosed.OpenTime;

                    // ================= POSITION SYNC =================
                    var pos = await exchange.GetPositionAsync(coinInfo.Symbol);
                    await orderManager.AttachManualPositionAsync(pos);

                    bool hasLong = pos.PositionAmt > 0;
                    bool hasShort = pos.PositionAmt < 0;

                    // ================= EXIT LOGIC (LUÔN CHẠY) =================
                    if (hasLong || hasShort)
                    {
                        var exitSignal = strategy.GenerateExitSignal(
                            candles15m, hasLong, hasShort, coinInfo);

                        if (exitSignal.Type == SignalType.CloseLong ||
                            exitSignal.Type == SignalType.CloseShort)
                        {
                            await exchange.ClosePositionAsync(
                                coinInfo.Symbol, pos.PositionAmt);
                        }
                    }

                    // ================= ENTRY LOGIC (KHÔNG COOLDOWN) =================
                    if (!pnl.IsInCooldown())
                    {
                        var entrySignal = strategy.GenerateSignal(
                            candles15m, candles1h, coinInfo);

                        await executor.HandleSignalAsync(entrySignal, coinInfo);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Không spam Slack, log local đủ để debug
            Console.WriteLine($"[ERROR] Worker {coinInfo.Symbol}: {ex}");
        }

        // ================= SHARED TASKS =================
        await liveSync.SyncAsync([coinInfo]);
        await pnl.SendQuickDailySummary();

        await Task.Delay(TimeSpan.FromSeconds(30), ct);
    }
}
