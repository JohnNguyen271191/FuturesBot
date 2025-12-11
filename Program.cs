using FuturesBot.Config;
using FuturesBot.IServices;
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

pnl.SetDailyBaseCapital(config.AccountBalance); // ví dụ vốn 80 USDT

await notifier.SendAsync(
    $"=== FuturesBot {config.Intervals[0].FrameTime.ToUpper()} - {nowVN:dd/MM/yyyy HH:mm:ss} started ==="
);

// Launch per-symbol workers
var tasks = config.CoinInfos
    .Select(s => RunSymbolWorkerAsync(s, host, config, notifier, liveSync, pnl))
    .ToList();

await Task.WhenAll(tasks);

// ============================================================================
// WORKER PER SYMBOL
// ============================================================================

static async Task RunSymbolWorkerAsync(
    CoinInfo coinInfo,
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
                coinInfo.Symbol, config.Intervals[0].FrameTime, 200);

            var candles1h = await exchange.GetRecentCandlesAsync(
                coinInfo.Symbol, config.Intervals[1].FrameTime, 200);

            if (candles15m?.Count >= 2)
            {
                if (pnl.IsInCooldown())
                {
                    var remain = pnl.GetCooldownRemaining();
                    await notifier.SendAsync(
                        $"BOT đang trong COOLDOWN (còn ~{remain?.TotalMinutes:F0} phút) → không mở lệnh mới.");
                    return;
                }
                var lastCandle = candles15m[^1];

                // Chỉ xử lý khi có nến 15m mới
                if (lastCandle.OpenTime > lastProcessedCandle)
                {
                    lastProcessedCandle = lastCandle.OpenTime;

                    //Lấy position 1 lần
                    var pos = await exchange.GetPositionAsync(coinInfo.Symbol);
                    await orderManager.AttachManualPositionAsync(pos);

                    bool hasLong = pos.PositionAmt > 0;
                    bool hasShort = pos.PositionAmt < 0;
                    //EXIT logic
                    if (hasLong || hasShort)
                    {
                        var exitSignal = strategy.GenerateExitSignal(
                            candles15m, hasLong, hasShort, coinInfo);

                        if (exitSignal.Type == SignalType.CloseLong ||
                            exitSignal.Type == SignalType.CloseShort)
                        {
                            await exchange.ClosePositionAsync(coinInfo.Symbol, pos.PositionAmt);
                            // không continue; vẫn cho liveSync/pnl chạy bên dưới
                        }
                    }

                    // ENTRY logic
                    var entrySignal = strategy.GenerateSignal(candles15m, candles1h, coinInfo);
                    await executor.HandleSignalAsync(entrySignal, coinInfo);
                }
                // nếu chưa có nến mới thì bỏ qua phần xử lý trade,
                // nhưng vẫn chạy liveSync/pnl ở dưới
            }
        }
        catch (Exception ex)
        {
            await notifier.SendAsync($"[ERROR] {coinInfo.Symbol}: {ex}");
        }

        // Các tác vụ chia sẻ, luôn chạy mỗi vòng (khoảng 30s/lần)
        await liveSync.SyncAsync([coinInfo]);
        await pnl.SendQuickDailySummary();

        // Tick interval
        await Task.Delay(TimeSpan.FromSeconds(30));
    }
}