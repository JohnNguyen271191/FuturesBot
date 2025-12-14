namespace FuturesBot.Models
{
    public class IncomeRecord
    {
        public string Symbol { get; set; } = "";
        public string IncomeType { get; set; } = ""; // REALIZED_PNL, COMMISSION, FUNDING_FEE...
        public decimal Income { get; set; }
        public DateTime Time { get; set; }          // Thời gian UTC
    }
}
