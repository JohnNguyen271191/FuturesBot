using FuturesBot.Config;
using FuturesBot.IServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FuturesBot.Services
{
    /// <summary>
    /// Runs Spot bot workers for multiple symbols (long-only spot).
    /// </summary>
    public sealed class SpotBotHostedService : BackgroundService
    {
        private readonly IServiceProvider _sp;
        private readonly BotConfig _config;
        private readonly SlackNotifierService _notify;

        public SpotBotHostedService(IServiceProvider sp, BotConfig config, SlackNotifierService notify)
        {
            _sp = sp;
            _config = config;
            _notify = notify;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_config.Spot.Enabled)
                return;

            var coins = _config.Spot.Coins ?? [];
            if (coins.Length == 0)
                return;

            // ensure legacy mirrors (some older services still read SpotQuoteAsset)
            _config.SpotQuoteAsset = _config.Spot.QuoteAsset;

            await _notify.SendAsync($"[SPOT] HostedService started. coins={coins.Length}, quote={_config.Spot.QuoteAsset}, cap={_config.Spot.WalletCapUsd}, paper={_config.PaperMode}");

            var workers = coins.Select(c => RunSymbolAsync(c, stoppingToken)).ToArray();
            await Task.WhenAll(workers);
        }

        private async Task RunSymbolAsync(CoinInfo coin, CancellationToken ct)
        {
            using var scope = _sp.CreateScope();
            var spot = scope.ServiceProvider.GetRequiredService<ISpotExchangeService>();
            var strategy = scope.ServiceProvider.GetRequiredService<ISpotTradingStrategy>();
            var oms = scope.ServiceProvider.GetRequiredService<SpotOrderManagerService>();

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var candlesMain = await spot.GetRecentCandlesAsync(coin.Symbol, coin.MainTimeFrame, 210);
                    var candlesTrend = await spot.GetRecentCandlesAsync(coin.Symbol, coin.TrendTimeFrame, 210);

                    if (candlesMain.Count < 50 || candlesTrend.Count < 50)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), ct);
                        continue;
                    }

                    var signal = strategy.GenerateSignal(candlesMain, candlesTrend, coin);
                    await oms.TickAsync(signal, coin, candlesMain, ct);

                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] Spot worker {coin.Symbol}: {ex}");
                }

                await Task.Delay(TimeSpan.FromSeconds(60), ct);
            }
        }
    }
}
