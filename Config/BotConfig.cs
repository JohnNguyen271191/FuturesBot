namespace FuturesBot.Config
{
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
        public Urls Urls { get; set; } = new Urls();

        // Mode
        public bool PaperMode { get; set; } = true;
        public string SlackWebhookUrl { get; set; } = Environment.GetEnvironmentVariable("SLACK_WEBHOOK_URL") ?? "";
    }

    public class CoinInfo
    {
        public int Id { get; set; }
        public string Symbol { get; set; } = "";
        public int Leverage { get; set; } = 50;
        public bool IsMajor { get; set; } = false;
        public decimal RiskPerTradePercent { get; set; } = 1m;
        public string MainTimeFrame { get; set; } = "15m";
        public string TrendTimeFrame { get; set; } = "1h";
    }

    public class Urls
    {
        public string BaseUrl { get; set; } = string.Empty;
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
        public string OpenAlgoOrdersUrl { get; set; } = string.Empty;
        public string AlgoOpenOrdersUrl { get; set; } = string.Empty;
    }
}
