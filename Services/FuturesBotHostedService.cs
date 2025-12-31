using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FuturesBot.Config;
using FuturesBot.IServices;
using FuturesBot.Models;
using FuturesBot.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FuturesBot.Services
{
    /// <summary>
    /// Runs Futures bot workers for multiple symbols.
    /// Option A: per-coin allocation + risk is applied inside TradeExecutorService/RiskManager.
    ///
    /// Anti-ban patch:
    /// - Reduce REST calls (klines/position) by polling on timeframe schedule + caching
    /// - Remove duplicate GetPositionAsync in the same loop
    /// - Slow down LiveSync loop (it also calls GetPositionAsync)
    /// - Add backoff on HTTP 418/429 / Binance -1003
    /// </summary>
    public sealed class FuturesBotHostedService : BackgroundService
    {
        private readonly IServiceProvider _sp;
        private readonly BotConfig _config;
        private readonly SlackNotifierService _notify;
        private readonly PnlReporterService _pnl;
        private readonly LiveSyncService _live;
        private readonly OrderManagerService _orderManager;

        // backoff throttles (avoid Slack spam)
        private readonly ConcurrentDictionary<string, DateTime> _lastBackoffNotifyUtc = new();

        public FuturesBotHostedService(
            IServiceProvider sp,
            BotConfig config,
            SlackNotifierService notify,
            PnlReporterService pnl,
            LiveSyncService live,
            OrderManagerService orderManager)
        {
            _sp = sp;
            _config = config;
            _notify = notify;
            _pnl = pnl;
            _live = live;
            _orderManager = orderManager;
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
            // IMPORTANT: LiveSync calls GetPositionAsync internally => keep interval SLOW
            var interval = TimeSpan.FromSeconds(60);

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
                    // backoff if rate limited (avoid tight loop)
                    if (IsRateLimitException(ex))
                    {
                        await NotifyBackoffOnce("[FUTURES][RATE_LIMIT] LiveSync hit rate limit (418/429/-1003). Backoff 120s.", "LIVE_SYNC");
                        await SafeDelay(TimeSpan.FromSeconds(120), ct);
                        continue;
                    }

                    Console.WriteLine($"[ERROR] Futures background loop: {ex}");
                }

                await SafeDelay(interval, ct);
            }
        }

        private async Task RunSymbolAsync(CoinInfo coin, CancellationToken ct)
        {
            using var scope = _sp.CreateScope();

            var exchange = scope.ServiceProvider.GetRequiredService<IFuturesExchangeService>();
            var strategy = scope.ServiceProvider.GetRequiredService<IFuturesTradingStrategy>();
            var executor = scope.ServiceProvider.GetRequiredService<TradeExecutorService>();

            var mainSec = Math.Max(1, TimeframeHelper.ParseToSeconds(coin.MainTimeFrame));
            var trendSec = Math.Max(1, TimeframeHelper.ParseToSeconds(coin.TrendTimeFrame));

            // Poll klines by schedule (avoid calling every 2 seconds)
            var mainPoll = ComputePollInterval(mainSec, minSeconds: 10, maxSeconds: 30);    // e.g. 1m -> 10~15s, 5m -> 30s
            var trendPoll = ComputePollInterval(trendSec, minSeconds: 60, maxSeconds: 120); // e.g. 30m -> 120s

            // Position check (manual attach) - keep slow
            var posCheckInterval = TimeSpan.FromSeconds(30);

            // Backoff when rate limited
            var backoffDelay = TimeSpan.FromSeconds(120);

            DateTime lastProcessedCandleOpenTimeUtc = DateTime.MinValue;

            DateTime nextMainFetchUtc = DateTime.MinValue;
            DateTime nextTrendFetchUtc = DateTime.MinValue;
            DateTime nextPosCheckUtc = DateTime.MinValue;

            // Cached data
            System.Collections.Generic.List<Candle>? cachedMain = null;
            System.Collections.Generic.List<Candle>? cachedTrend = null;

            // Cached position from last check
            FuturesPosition cachedPos = default;
            bool hasCachedPos = false;

            // Small loop sleep (doesn't call REST unless due)
            var idleDelay = TimeSpan.FromSeconds(1);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var now = DateTime.UtcNow;

                    // ------------------ Position check (slow) ------------------
                    if (now >= nextPosCheckUtc)
                    {
                        nextPosCheckUtc = now.Add(posCheckInterval);

                        var pos = await exchange.GetPositionAsync(coin.Symbol);
                        cachedPos = pos;
                        hasCachedPos = true;

                        if (pos.PositionAmt != 0)
                        {
                            await _orderManager.AttachManualPositionAsync(pos);
                        }
                    }

                    // ------------------ Cooldown gate ------------------
                    if (_pnl.IsInCooldown())
                    {
                        await SafeDelay(TimeSpan.FromSeconds(3), ct);
                        continue;
                    }

                    // ------------------ Fetch klines by schedule ------------------
                    if (now >= nextMainFetchUtc)
                    {
                        nextMainFetchUtc = now.Add(mainPoll);
                        cachedMain = await exchange.GetRecentCandlesAsync(coin.Symbol, coin.MainTimeFrame, 210);
                    }

                    if (now >= nextTrendFetchUtc)
                    {
                        nextTrendFetchUtc = now.Add(trendPoll);
                        cachedTrend = await exchange.GetRecentCandlesAsync(coin.Symbol, coin.TrendTimeFrame, 210);
                    }

                    // Need both caches
                    if (cachedMain == null || cachedTrend == null || cachedMain.Count < 50 || cachedTrend.Count < 50)
                    {
                        await SafeDelay(idleDelay, ct);
                        continue;
                    }

                    // Use closed candle (Count-2) and only process new candle
                    if (cachedMain.Count < 2)
                    {
                        await SafeDelay(idleDelay, ct);
                        continue;
                    }

                    var lastClosed = cachedMain[^2];
                    if (lastClosed.OpenTime <= lastProcessedCandleOpenTimeUtc)
                    {
                        await SafeDelay(idleDelay, ct);
                        continue;
                    }

                    lastProcessedCandleOpenTimeUtc = lastClosed.OpenTime;

                    // ------------------ Determine if has position ------------------
                    // Use cached position if available; if not, assume no position for now.
                    var hasPosition = hasCachedPos && cachedPos.PositionAmt != 0;

                    // If no position => generate signals
                    if (!hasPosition && !_pnl.IsInCooldown())
                    {
                        var signal = strategy.GenerateSignal(cachedMain, cachedTrend, coin);
                        if (signal.Type != EnumTypesHelper.SignalType.None)
                        {
                            await executor.HandleSignalAsync(signal, coin);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // If rate limited -> backoff hard
                    if (IsRateLimitException(ex))
                    {
                        await NotifyBackoffOnce(
                            $"[FUTURES][RATE_LIMIT] {coin.Symbol} hit rate limit (418/429/-1003). Backoff {backoffDelay.TotalSeconds:0}s.",
                            coin.Symbol);

                        await SafeDelay(backoffDelay, ct);

                        // Also slow down next polls after backoff
                        nextMainFetchUtc = DateTime.UtcNow.Add(mainPoll);
                        nextTrendFetchUtc = DateTime.UtcNow.Add(trendPoll);
                        nextPosCheckUtc = DateTime.UtcNow.Add(posCheckInterval);

                        continue;
                    }

                    Console.WriteLine($"[ERROR] Futures worker {coin.Symbol}: {ex}");
                }

                await SafeDelay(idleDelay, ct);
            }
        }

        // ------------------ Helpers ------------------

        private static TimeSpan ComputePollInterval(int timeframeSeconds, int minSeconds, int maxSeconds)
        {
            // Poll at roughly timeframe/4, clamped
            var sec = timeframeSeconds / 4;
            if (sec < minSeconds) sec = minSeconds;
            if (sec > maxSeconds) sec = maxSeconds;
            return TimeSpan.FromSeconds(sec);
        }

        private static async Task SafeDelay(TimeSpan delay, CancellationToken ct)
        {
            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
        }

        private async Task NotifyBackoffOnce(string message, string key)
        {
            var now = DateTime.UtcNow;
            var last = _lastBackoffNotifyUtc.GetOrAdd(key, _ => DateTime.MinValue);

            // notify at most once per 5 minutes per key
            if (now - last >= TimeSpan.FromMinutes(5))
            {
                _lastBackoffNotifyUtc[key] = now;
                try { await _notify.SendAsync(message); } catch { /* ignore */ }
            }
        }

        private static bool IsRateLimitException(Exception ex)
        {
            // 1) HttpRequestException StatusCode 418/429
            if (ex is HttpRequestException hre)
            {
#if NET6_0_OR_GREATER
                if (hre.StatusCode.HasValue)
                {
                    if (hre.StatusCode.Value == (HttpStatusCode)418) return true;
                    if (hre.StatusCode.Value == (HttpStatusCode)429) return true;
                }
#endif
                // sometimes status is only in Message
                var msg = hre.Message ?? string.Empty;
                if (msg.Contains("418") || msg.Contains("429")) return true;
            }

            // 2) Wrapped exceptions
            var text = ex.ToString();

            // Binance ban/rate-limit markers:
            // - code=-1003 Way too many requests
            // - banned until ...
            // - "I'm a teapot"
            if (text.Contains("code=-1003", StringComparison.OrdinalIgnoreCase)) return true;
            if (text.Contains("Way too many requests", StringComparison.OrdinalIgnoreCase)) return true;
            if (text.Contains("banned until", StringComparison.OrdinalIgnoreCase)) return true;
            if (text.Contains("I'm a teapot", StringComparison.OrdinalIgnoreCase)) return true;
            if (text.Contains("418", StringComparison.OrdinalIgnoreCase)) return true;
            if (text.Contains("429", StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }
    }
}
