namespace FuturesBot.Config
{
    public enum TradingMarket
    {
        Futures,
        Spot
    }

    // =========================
    // ROOT CONFIG
    // =========================
    public sealed class BotConfig
    {
        // Mode
        public TradingMarket Market { get; set; } = TradingMarket.Futures;
        public bool PaperMode { get; set; } = true;

        // API keys
        public string ApiKey { get; set; } = Environment.GetEnvironmentVariable("BINANCE_API_KEY") ?? "";
        public string ApiSecret { get; set; } = Environment.GetEnvironmentVariable("BINANCE_API_SECRET") ?? "";

        // Slack
        public string SlackWebhookUrl { get; set; } = Environment.GetEnvironmentVariable("SLACK_WEBHOOK_URL") ?? "";

        // Global
        public GlobalConfig Global { get; set; } = new();

        // Market-specific
        public SpotConfig Spot { get; set; } = new();
        public FuturesConfig Futures { get; set; } = new();
    }

    // =========================
    // GLOBAL
    // =========================
    public sealed class GlobalConfig
    {
        public decimal AccountBalance { get; set; } = 200m;
        public decimal MaxDailyLossPercent { get; set; } = 5m;
        public double CooldownDuration { get; set; } = 1;
    }

    // =========================
    // SPOT
    // =========================
    public sealed class SpotConfig
    {
        public string QuoteAsset { get; set; } = "FDUSD";
        public SpotCoinConfig[] Coins { get; set; } = Array.Empty<SpotCoinConfig>();
        public SpotOmsConfig Oms { get; set; } = new();
        public SpotUrls Urls { get; set; } = new();
    }

    public sealed class SpotCoinConfig
    {
        public int Id { get; set; }
        public string Symbol { get; set; } = "";

        // percent of quote balance to use (MAX entry style)
        public decimal RiskPerTradePercent { get; set; } = 1m;

        public string MainTimeFrame { get; set; } = "1m";
        public string TrendTimeFrame { get; set; } = "5m";
    }

    // =========================
    // FUTURES
    // =========================
    public sealed class FuturesConfig
    {
        public FuturesCoinConfig[] Coins { get; set; } = Array.Empty<FuturesCoinConfig>();
        public FuturesUrls Urls { get; set; } = new();
    }

    public sealed class FuturesCoinConfig
    {
        public int Id { get; set; }
        public string Symbol { get; set; } = "";

        public int Leverage { get; set; } = 20;
        public bool IsMajor { get; set; }

        public decimal RiskPerTradePercent { get; set; } = 1m;

        public string MainTimeFrame { get; set; } = "5m";
        public string TrendTimeFrame { get; set; } = "30m";

        public decimal MinVolumeUsdTrend { get; set; } = 600_000m;
    }

    // =========================
    // SPOT OMS CONFIG
    // =========================
    public sealed class SpotOmsConfig
    {
        public decimal MinEntryNotionalUsd { get; set; } = 10m;
        public decimal EntryQuoteBufferPercent { get; set; } = 0.02m;
        public decimal EntryMakerOffsetPercent { get; set; } = 0.0003m;

        public decimal MinHoldingNotionalUsd { get; set; } = 10m;

        public decimal DefaultTakeProfitPercent { get; set; } = 0.004m;
        public decimal DefaultStopLossPercent { get; set; } = 0.003m;

        public decimal StopLimitBufferPercent { get; set; } = 0.001m;

        // OCO legacy (should be false in V2+)
        public bool UseOcoExitOrders { get; set; } = false;

        // TP manager (1 TP only)
        public bool MaintainMakerTakeProfit { get; set; } = true;
        public decimal TpMinDistancePercent { get; set; } = 0.0015m;
        public decimal TpMaxDistancePercent { get; set; } = 0.006m;
        public int TpRecheckSeconds { get; set; } = 5;

        // Soft SL maker-first
        public decimal SoftSlInsideSpreadRatio { get; set; } = 0.2m;
        public int SoftSlWaitSeconds { get; set; } = 10;
        public decimal SoftSlMakerOffsetPercent { get; set; } = 0.0001m;
        public decimal SoftSlSkipIfWorseThanStopByPercent { get; set; } = 0.001m;

        // optional time-stop
        public int TimeStopSeconds { get; set; } = 0;

        public int PostExitCooldownSeconds { get; set; } = 5;
        public int MinSecondsBetweenActions { get; set; } = 5;

        // Entry maker reprice timeout
        public int EntryRepriceSeconds { get; set; } = 60;
    }

    // =========================
    // URL MODELS
    // =========================
    public sealed class SpotUrls
    {
        public string BaseUrl { get; set; } = "";
        public string AccountUrl { get; set; } = "";
        public string OrderUrl { get; set; } = "";
        public string OcoOrderUrl { get; set; } = "";
        public string KlinesUrl { get; set; } = "";
        public string OpenOrdersUrl { get; set; } = "";
        public string UserTradesUrl { get; set; } = "";
        public string TimeUrl { get; set; } = "";
        public string AllOpenOrdersUrl { get; set; } = "";
        public string ExchangeInfoUrl { get; set; } = "";
    }

    public sealed class FuturesUrls
    {
        public string BaseUrl { get; set; } = "";
        public string AlgoOrderUrl { get; set; } = "";
        public string OrderUrl { get; set; } = "";
        public string KlinesUrl { get; set; } = "";
        public string PositionRiskUrl { get; set; } = "";
        public string OpenOrdersUrl { get; set; } = "";
        public string UserTradesUrl { get; set; } = "";
        public string TimeUrl { get; set; } = "";
        public string AllOpenOrdersUrl { get; set; } = "";
        public string LeverageUrl { get; set; } = "";
        public string IncomeUrl { get; set; } = "";
        public string ExchangeInfoUrl { get; set; } = "";
        public string OpenAlgoOrdersUrl { get; set; } = "";
        public string AlgoOpenOrdersUrl { get; set; } = "";
    }
}
