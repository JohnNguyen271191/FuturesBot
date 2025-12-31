using FuturesBot.Config;
using FuturesBot.IServices;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FuturesBot.Services
{
    /// <summary>
    /// Sends a daily Spot PnL report (quote cashflow based) to Slack.
    ///
    /// ...
    /// </summary>
    public sealed class SpotDailyReportHostedService : BackgroundService
    {
        private readonly BotConfig _config;
        private readonly ISpotExchangeService _spot;
        private readonly SlackNotifierService _notify;

        public SpotDailyReportHostedService(BotConfig config, ISpotExchangeService spot, SlackNotifierService notify)
        {
            _config = config;
            _spot = spot;
            _notify = notify;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_config.Spot.Enabled || !_config.Spot.DailyReportEnabled)
                return;

            var tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");

            DateTime lastSentLocalDate = DateTime.MinValue;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);

                    // send once per day at configured HH:mm
                    if (TimeSpan.TryParse(_config.Spot.DailyReportTimeLocal, out var target)
                        && nowLocal.TimeOfDay >= target
                        && lastSentLocalDate.Date != nowLocal.Date)
                    {
                        var dayLocal = nowLocal.Date;
                        var fromLocal = dayLocal;
                        var toLocal = dayLocal.AddDays(1);

                        var fromUtc = TimeZoneInfo.ConvertTimeToUtc(fromLocal, tz);
                        var toUtc = TimeZoneInfo.ConvertTimeToUtc(toLocal, tz);

                        await SendReportAsync(fromUtc, toUtc, nowLocal, stoppingToken);
                        lastSentLocalDate = nowLocal.Date;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] SpotDailyReportHostedService: {ex}");
                }

                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }

        private async Task SendReportAsync(DateTime fromUtc, DateTime toUtc, DateTime nowLocal, CancellationToken ct)
        {
            var quote = _config.Spot.QuoteAsset;
            var coins = _config.Spot.Coins ?? Array.Empty<CoinInfo>();
            if (coins.Length == 0) return;

            decimal totalDeltaQuote = 0m;
            decimal totalFeeQuote = 0m;
            decimal totalFeeOther = 0m;

            var perSymbol = new List<string>();

            foreach (var c in coins)
            {
                ct.ThrowIfCancellationRequested();

                var trades = await _spot.GetMyTradesAsync(c.Symbol, fromUtc, toUtc, limit: 1000);
                if (trades.Count == 0) continue;

                decimal deltaQuote = 0m;
                decimal feeQuote = 0m;
                decimal feeOther = 0m;

                foreach (var t in trades)
                {
                    // Quote cashflow: BUY spends quote, SELL receives quote
                    deltaQuote += t.IsBuyer ? -t.QuoteQty : t.QuoteQty;

                    if (string.Equals(t.CommissionAsset, quote, StringComparison.OrdinalIgnoreCase))
                        feeQuote += t.Commission;
                    else
                        feeOther += t.Commission;
                }

                deltaQuote -= feeQuote;

                totalDeltaQuote += deltaQuote;
                totalFeeQuote += feeQuote;
                totalFeeOther += feeOther;

                perSymbol.Add($"- {c.Symbol}: Δ{quote}={deltaQuote:0.####} (trades={trades.Count}, fee{quote}={feeQuote:0.####})");
            }

            if (perSymbol.Count == 0) return;

            var header = $"[SPOT][DAILY] {nowLocal:dd/MM/yyyy} quote={quote} Δ={totalDeltaQuote:0.####} fee{quote}={totalFeeQuote:0.####}";
            if (totalFeeOther > 0)
                header += $" (feeOther={totalFeeOther:0.####} - check commissionAsset)";

            var body = header + "\n" + string.Join("\n", perSymbol);
            await _notify.SendAsync(body);
        }
    }
}
