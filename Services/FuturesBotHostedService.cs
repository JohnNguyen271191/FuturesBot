using FuturesBot.Config;
using FuturesBot.IServices;
using FuturesBot.Models;
using FuturesBot.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace FuturesBot.Services
{
    /// <summary>
    /// Runs Futures bot workers for multiple symbols.
    /// Option A: per-coin allocation + risk is applied inside TradeExecutorService/RiskManager.
    /// </summary>
    public sealed class FuturesBotHostedService : BackgroundService
    {
        private readonly IServiceProvider _sp;
        private readonly BotConfig _config;
        private readonly SlackNotifierService _notify;
        private readonly PnlReporterService _pnl;
        private readonly LiveSyncService _live;

        public FuturesBotHostedService(
            IServiceProvider sp,
            BotConfig config,
            SlackNotifierService notify,
            PnlReporterService pnl,
            LiveSyncService live)
        {
            _sp = sp;
            _config = config;
            _notify = notify;
            _pnl = pnl;
            _live = live;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_config.Futures.Enabled)
                return;

            // daily base capital for summary + cooldown (use Futures wallet cap)
            var baseCap = _config.Futures.WalletCapUsd > 0 ? _config.Futures.WalletCapUsd : _config.AccountBalance;
            _pnl.SetDailyBaseCapital(baseCap);

            var coins = (_config.Futures.Coins?.Length > 0)
                ? _config.Futures.Coins
                : _config.CoinInfos; // back-compat

            if (coins.Length == 0)
                return;

            await _notify.SendAsync($"[FUTURES] HostedService started. coins={coins.Length}, cap={_config.Futures.WalletCapUsd}, paper={_config.PaperMode}");

            // Per-symbol workers
            var workers = coins.Select(c => RunSymbolAsync(c, stoppingToken)).ToArray();

            // Background periodic tasks
            var background = RunBackgroundLoopsAsync(coins, stoppingToken);

            await Task.WhenAll(workers.Concat(new[] { background }));
        }

        private async Task RunBackgroundLoopsAsync(CoinInfo[] coins, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // daily summary + cooldown messaging
                    await _pnl.SendQuickDailySummary();

                    // sync positions/orders from exchange for safety
                    await _live.SyncAsync(coins);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] Futures background loop: {ex}");
                }

                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
        }

        private async Task RunSymbolAsync(CoinInfo coin, CancellationToken ct)
        {
            // Each symbol gets its own DI scope for scoped services
            using var scope = _sp.CreateScope();

            var exchange = scope.ServiceProvider.GetRequiredService<IFuturesExchangeService>();
            var strategy = scope.ServiceProvider.GetRequiredService<IFuturesTradingStrategy>();
            var executor = scope.ServiceProvider.GetRequiredService<TradeExecutorService>();

            var mainSec = TimeframeHelper.ParseToSeconds(coin.MainTimeFrame);
            var trendSec = TimeframeHelper.ParseToSeconds(coin.TrendTimeFrame);

            // simple cadence: check every 2 seconds, but use closed candles (Count-2)
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (_pnl.IsInCooldown())
                    {
                        await Task.Delay(TimeSpan.FromSeconds(3), ct);
                        continue;
                    }

                    var candlesMain = await exchange.GetRecentCandlesAsync(coin.Symbol, coin.MainTimeFrame, 210);
                    var candlesTrend = await exchange.GetRecentCandlesAsync(coin.Symbol, coin.TrendTimeFrame, 210);

                    if (candlesMain.Count < 50 || candlesTrend.Count < 50)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), ct);
                        continue;
                    }

                    var signal = strategy.GenerateSignal(candlesMain, candlesTrend, coin);
                    if (signal.Type != EnumTypesHelper.SignalType.None)
                    {
                        await executor.HandleSignalAsync(signal, coin);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] Futures worker {coin.Symbol}: {ex}");
                }

                // small delay
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }
}
