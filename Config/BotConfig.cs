namespace FuturesBot.Config
{
    /// <summary>
    /// Root configuration.
    ///
    /// IMPORTANT:
    /// - New schema supports running Spot + Futures in parallel with multiple coins.
    /// - We keep a few legacy fields for backward compatibility with older code paths.
    /// </summary>
    public sealed class BotConfig
    {
        // ============================
        // NEW SCHEMA (recommended)
        // ============================

        public SpotSettings Spot { get; set; } = new();
        public FuturesSettings Futures { get; set; } = new();

        /// <summary>
        /// Optional: Strategy mode profiles for Futures OMS (exit/management).
        /// If empty, code falls back to built-in defaults in <see cref="Models.ModeProfile"/>.
        /// </summary>
        public Models.ModeProfile[] ModeProfiles { get; set; } = [];

        // ============================
        // LEGACY SCHEMA (back-compat)
        // ============================

        /// <summary>
        /// Legacy: used when bot was running a single market at a time.
        /// </summary>
        public CoinInfo[] CoinInfos { get; set; } = [];

        /// <summary>
        /// Legacy spot quote asset.
        /// </summary>
        public string SpotQuoteAsset { get; set; } = "FDUSD";

        /// <summary>
        /// Legacy futures account balance used by old RiskManager.
        /// New sizing uses Spot/Futures WalletCapUsd instead.
        /// </summary>
        public decimal AccountBalance { get; set; } = 200m;

        public decimal MaxDailyLossPercent { get; set; } = 5m;
        public double CooldownDuration { get; set; } = 1;

        // Binance
        public string ApiKey { get; set; } = Environment.GetEnvironmentVariable("BINANCE_API_KEY") ?? "";
        public string ApiSecret { get; set; } = Environment.GetEnvironmentVariable("BINANCE_API_SECRET") ?? "";

        // Spot OMS (legacy)
        public SpotOmsConfig SpotOms { get; set; } = new();

        // Url sets
        public Urls FuturesUrls { get; set; } = new();
        public Urls SpotUrls { get; set; } = new();

        // Back-compat: some old code reads config.Urls
        public Urls Urls { get; set; } = new();

        // Mode
        public bool PaperMode { get; set; } = true;
        public string SlackWebhookUrl { get; set; } = Environment.GetEnvironmentVariable("SLACK_WEBHOOK_URL") ?? "";
    }

    public sealed class SpotSettings
    {
        public bool Enabled { get; set; } = false;
        public string QuoteAsset { get; set; } = "FDUSD";

        /// <summary>
        /// Total capital cap the Spot bot is allowed to use (in QuoteAsset terms).
        /// Used with AllocationPercent per coin (Option A).
        /// </summary>
        public decimal WalletCapUsd { get; set; } = 50m;

        /// <summary>
        /// Default risk% applied per trade on the per-coin allocated cap.
        /// If a coin has RiskPerTradePercent set, it overrides this.
        /// </summary>
        public decimal DefaultRiskPerTradePercent { get; set; } = 20m;

        public CoinInfo[] Coins { get; set; } = [];

        /// <summary>
        /// Spot strategy parameters (all configurable via appsettings.json).
        /// </summary>
        public SpotStrategySettings Strategy { get; set; } = new();

        /// <summary>Daily report enabled for spot.</summary>
        public bool DailyReportEnabled { get; set; } = true;
        /// <summary>VN time (HH:mm) to send daily report. Default: 23:59.</summary>
        public string DailyReportTimeLocal { get; set; } = "23:59";
    }

    public sealed class FuturesSettings
    {
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Total capital cap the Futures bot is allowed to use (in margin terms, typically USDT).
        /// Used with AllocationPercent per coin (Option A).
        /// </summary>
        public decimal WalletCapUsd { get; set; } = 30m;

        public decimal DefaultRiskPerTradePercent { get; set; } = 1m;

        public CoinInfo[] Coins { get; set; } = [];

        /// <summary>
        /// Futures strategy parameters (all configurable via appsettings.json).
        /// </summary>
        public FuturesStrategySettings Strategy { get; set; } = new();
    }

    public sealed class SpotOmsConfig
    {
        public decimal MinHoldingNotionalUsd { get; set; } = 10m;
        public decimal DefaultTakeProfitPercent { get; set; } = 0.004m;
        public decimal DefaultStopLossPercent { get; set; } = 0.003m;
        public decimal StopLimitBufferPercent { get; set; } = 0.001m;
        public decimal MinEntryNotionalUsd { get; set; } = 10m;
        public decimal EntryQuoteBufferPercent { get; set; } = 0.02m;
        public decimal EntryMakerOffsetPercent { get; set; } = 0.0003m;
        public decimal SlMakerBufferPercent { get; set; } = 0.0003m;
        public int EntryRepriceSeconds { get; set; } = 60;
        public int MinSecondsBetweenActions { get; set; } = 5;
    }

    public sealed class CoinInfo
    {
        public int Id { get; set; }
        public string Symbol { get; set; } = "";
        public int Leverage { get; set; } = 50;
        public bool IsMajor { get; set; }

        /// <summary>
        /// Risk percent per trade. If 0 or negative, the domain default is used.
        /// </summary>
        public decimal RiskPerTradePercent { get; set; } = 0m;

        /// <summary>
        /// Option A: allocation percent of the domain WalletCapUsd reserved for this coin.
        /// Sum of allocations should be 100 per domain.
        /// </summary>
        public decimal AllocationPercent { get; set; } = 100m;

        public string MainTimeFrame { get; set; } = "15m";
        public string TrendTimeFrame { get; set; } = "1h";
        public decimal MinVolumeUsdTrend { get; set; } = 600_000m;
    }

    public sealed class Urls
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
