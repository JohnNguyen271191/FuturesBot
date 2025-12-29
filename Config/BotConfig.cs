using System;

namespace FuturesBot.Config
{
    public enum TradingMarket
    {
        Futures,
        Spot
    }

    public class BotConfig
    {
        public CoinInfo[] CoinInfos { get; set; } = [];

        // Risk
        public decimal AccountBalance { get; set; } = 200m;
        public decimal MaxDailyLossPercent { get; set; } = 5m;
        public double CooldownDuration { get; set; } = 1;

        // Binance
        public string ApiKey { get; set; } = Environment.GetEnvironmentVariable("BINANCE_API_KEY") ?? "";
        public string ApiSecret { get; set; } = Environment.GetEnvironmentVariable("BINANCE_API_SECRET") ?? "";

        // Market (Futures / Spot)
        public TradingMarket Market { get; set; } = TradingMarket.Futures;

        // Spot OMS (OCO management)
        public SpotOmsConfig SpotOms { get; set; } = new SpotOmsConfig();

        // Url sets (so you can switch by Market)
        public Urls FuturesUrls { get; set; } = new Urls();
        public Urls SpotUrls { get; set; } = new Urls();

        // Back-compat: some older code reads config.Urls
        public Urls Urls { get; set; } = new Urls();

        // Mode
        public bool PaperMode { get; set; } = true;
        public string SlackWebhookUrl { get; set; } = Environment.GetEnvironmentVariable("SLACK_WEBHOOK_URL") ?? "";
    }

    public sealed class SpotOmsConfig
    {
        /// <summary>
        /// Minimal holding notional (baseFree * lastPrice) to be considered "in a spot position".
        /// Prevents tiny dust from triggering OMS logic.
        /// </summary>
        public decimal MinHoldingNotionalUsd { get; set; } = 10m;

        /// <summary>
        /// If we detect holdings but no exit orders, we will place a rescue OCO with these defaults.
        /// Example: 0.004 = +0.4% TP
        /// </summary>
        public decimal DefaultTakeProfitPercent { get; set; } = 0.004m;

        /// <summary>
        /// Example: 0.003 = -0.3% SL
        /// </summary>
        public decimal DefaultStopLossPercent { get; set; } = 0.003m;

        /// <summary>
        /// STOP_LIMIT price buffer below STOP trigger for SELL OCO.
        /// Example: 0.001 = 0.1%
        /// </summary>
        public decimal StopLimitBufferPercent { get; set; } = 0.001m;

        /// <summary>
        /// Throttle placing/canceling orders to avoid spam.
        /// </summary>
        public int MinSecondsBetweenActions { get; set; } = 5;
    }

    public class CoinInfo
    {
        public int Id { get; set; }
        public string Symbol { get; set; } = "";
        public int Leverage { get; set; } = 50;
        public bool IsMajor { get; set; }
        public decimal RiskPerTradePercent { get; set; } = 1m;
        public string MainTimeFrame { get; set; } = "15m";
        public string TrendTimeFrame { get; set; } = "1h";
        public decimal MinVolumeUsdTrend { get; set; } = 600_000m;
    }

    public class Urls
    {
        public string BaseUrl { get; set; } = string.Empty;

        // Spot-specific
        public string AccountUrl { get; set; } = string.Empty;
        public string OcoOrderUrl { get; set; } = string.Empty;

        // Shared-ish
        public string AlgoOrderUrl { get; set; } = string.Empty;
        public string OrderUrl { get; set; } = string.Empty;
        public string KlinesUrl { get; set; } = string.Empty;
        public string PositionRiskUrl { get; set; } = string.Empty;
        public string OpenOrdersUrl { get; set; } = string.Empty;
        public string UserTradesUrl { get; set; } = string.Empty;
        public string TimeUrl { get; set; } = string.Empty;
        public string AllOpenOrdersUrl { get; set; } = string.Empty;
        public string LeverageUrl { get; set; } = string.Empty;
        public string IncomeUrl { get; set; } = string.Empty;
        public string ExchangeInfoUrl { get; set; } = string.Empty;

        // Futures algo endpoints
        public string OpenAlgoOrdersUrl { get; set; } = string.Empty;
        public string AlgoOpenOrdersUrl { get; set; } = string.Empty;
    }
}
