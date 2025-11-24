namespace FuturesBot.Config
{
    public class BotConfig
    {
        public Symbol[] Symbols { get; set; } = [];

        // Risk
        public decimal AccountBalance { get; set; } = 200m;      // vốn giả định để tính lot
        public decimal RiskPerTradePercent { get; set; } = 1m;   // 1% / lệnh
        public decimal MaxDailyLossPercent { get; set; } = 5m;   // thua 5%/ngày nghỉ
        public int MaxTradesPerDay { get; set; } = 6;
        public int MaxLosingStreak { get; set; } = 3;
        public TimeSpan CooldownAfterTrade { get; set; } = TimeSpan.FromMinutes(5);

        // Binance
        public string ApiKey { get; set; } = Environment.GetEnvironmentVariable("BINANCE_API_KEY") ?? "";
        public string ApiSecret { get; set; } = Environment.GetEnvironmentVariable("BINANCE_API_SECRET") ?? "";
        public string BaseUrl { get; set; } = "";

        // Mode
        public bool PaperMode { get; set; } = true;   // true = chỉ log, không gửi lệnh thật
        public Item[] Intervals { get; set; } = [];
        public string SlackWebhookUrl { get; set; } = Environment.GetEnvironmentVariable("SLACK_WEBHOOK_URL") ?? "";
    }

    public class Item
    {
        public int Id { get; set; }
        public string FrameTime { get; set; } = "";
    }

    public class Symbol
    {
        public int Id { get; set; }
        public string Coin { get; set; } = "";
        public int Leverage { get; set; } = 50;
    }
}
