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

        // Spot quote asset (e.g., FDUSD)
        public string SpotQuoteAsset { get; set; } = "FDUSD";

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
        /// Minimal quote notional to enter a new position (e.g., FDUSD amount).
        /// Prevents tiny entries that would round quantity to zero.
        /// </summary>
        public decimal MinEntryNotionalUsd { get; set; } = 10m;

        /// <summary>
        /// For MAX entry, we spend quoteFree * (1 - buffer). Example: 0.02 = use 98%.
        /// </summary>
        public decimal EntryQuoteBufferPercent { get; set; } = 0.02m;

        

        /// <summary>
        /// Maker entry offset (percent). Example: 0.0003 = 0.03% below last price for BUY LIMIT.
        /// </summary>
        public decimal EntryMakerOffsetPercent { get; set; } = 0.0003m;

        /// <summary>
        /// StopLimit maker buffer (percent). Example: 0.0003 = 0.03% above stopPrice for SELL STOP_LIMIT.
        /// This makes the triggered limit order non-marketable (maker) but may not fill in fast drops.
        /// </summary>
        public decimal SlMakerBufferPercent { get; set; } = 0.0003m;

        /// <summary>
        /// If maker entry is not filled within this many seconds, cancel and reprice.
        /// </summary>
        public int EntryRepriceSeconds { get; set; } = 60;

        // =============================
        // EXIT MANAGEMENT (NO-OCO)
        // =============================

        /// <summary>
        /// If true, Spot OMS will create OCO exits (legacy mode).
        /// If false, Spot OMS will self-manage exits: TP as maker LIMIT and SL as soft maker -> fallback market.
        /// Recommended: false.
        /// </summary>
        public bool UseOcoExitOrders { get; set; } = false;

        /// <summary>
        /// When in position, ensure there is always a maker TAKE-PROFIT LIMIT SELL at entryRef*(1+tp%).
        /// If strategy provides TakeProfit, OMS may prefer it.
        /// </summary>
        public bool MaintainMakerTakeProfit { get; set; } = true;

        /// <summary>
        /// TP manager: if existing TP distance to lastPrice is below this, TP is too close and will be replaced.
        /// Example: 0.0015 = 0.15%
        /// </summary>
        public decimal TpMinDistancePercent { get; set; } = 0.0015m;

        /// <summary>
        /// TP manager: if existing TP distance to lastPrice is above this, TP is too far and will be replaced.
        /// Example: 0.006 = 0.6%
        /// </summary>
        public decimal TpMaxDistancePercent { get; set; } = 0.006m;

        /// <summary>
        /// How often (seconds) to re-check and potentially replace TP.
        /// </summary>
        public int TpRecheckSeconds { get; set; } = 5;

        /// <summary>
        /// Soft SL maker: place LIMIT_MAKER SELL inside the spread, closer to bid to increase fill probability.
        /// 0 = at ask, 1 = 10% into spread from bid, etc.
        /// Example: 0.2 means bid + 20%*(ask-bid).
        /// </summary>
        public decimal SoftSlInsideSpreadRatio { get; set; } = 0.2m;

        /// <summary>
        /// Soft SL maker: when exiting (STOP/early-exit), place LIMIT_MAKER SELL first,
        /// wait SoftSlWaitSeconds; if not filled then cancel and fallback to MARKET SELL.
        /// </summary>
        public int SoftSlWaitSeconds { get; set; } = 10;

        /// <summary>
        /// Soft SL maker price uses current bestAsk (preferred) or lastPrice * (1 + SoftSlMakerOffsetPercent).
        /// Example: 0.0001 = +0.01%.
        /// </summary>
        public decimal SoftSlMakerOffsetPercent { get; set; } = 0.0001m;

        /// <summary>
        /// Extra guard: if price is dumping fast (e.g., lastPrice below stop by this percent),
        /// skip soft maker and go straight to MARKET SELL.
        /// Example: 0.001 = 0.1%.
        /// </summary>
        public decimal SoftSlSkipIfWorseThanStopByPercent { get; set; } = 0.001m;

        /// <summary>
        /// Time-stop: if holding exists for too long without reaching TP, exit via soft SL.
        /// Set 0 to disable.
        /// </summary>
        public int TimeStopSeconds { get; set; } = 0;

        /// <summary>
        /// Cooldown after closing a spot position, to prevent immediate re-entry spam.
        /// </summary>
        public int PostExitCooldownSeconds { get; set; } = 5;

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
