using FuturesBot.Config;
using FuturesBot.IServices;
using FuturesBot.Services;
using FuturesBot.Strategies;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build()
            .Get<BotConfig>() ?? throw new Exception("Cannot load BotConfig");

        // Back-compat: map legacy url blocks if present
        if (string.IsNullOrWhiteSpace(config.Urls.BaseUrl) && !string.IsNullOrWhiteSpace(config.FuturesUrls.BaseUrl))
            config.Urls = config.FuturesUrls;

        // Back-compat: legacy SpotOms is still used by SpotOrderManagerService and Spot client.
        // Prefer new schema Spot.Oms.
        if (config.Spot != null && config.Spot.Oms != null)
            config.SpotOms = config.Spot.Oms;

        services.AddSingleton(config);

        // shared
        services.AddSingleton<SlackNotifierService>();
        services.AddSingleton<IndicatorService>();

        // Futures services
        services.AddSingleton<IFuturesExchangeService, BinanceFuturesClientService>();
        services.AddSingleton<PnlReporterService>();
        services.AddSingleton<LiveSyncService>();
        services.AddSingleton<RiskManager>();

        services.AddScoped<OrderManagerService>();
        services.AddScoped<TradeExecutorService>();
        services.AddScoped<IFuturesTradingStrategy, FuturesTrendStrategy>();

        // Spot services
        services.AddSingleton<ISpotExchangeService, BinanceSpotClientService>();
        services.AddScoped<SpotOrderManagerService>();
        // Spot Strategy V2 (3 entry types, rule rõ ràng)
        services.AddScoped<ISpotTradingStrategy, SpotStrategy>();

        // Hosted services (run in parallel)
        services.AddHostedService<FuturesBotHostedService>();
        services.AddHostedService<SpotBotHostedService>();
        services.AddHostedService<SpotDailyReportHostedService>();
    })
    .Build();

await host.RunAsync();