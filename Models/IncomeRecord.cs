namespace FuturesBot.Models
{
    public class IncomeRecord
    {
        public string Symbol { get; set; } = "";
        public string IncomeType { get; set; } = ""; // REALIZED_PNL, COMMISSION, FUNDING_FEE...
        public decimal Income { get; set; }          // Số tiền +/-
        public string? Asset { get; set; }           // Thường là USDT
        public long TranId { get; set; }            // Id giao dịch bên Binance
        public long? TradeId { get; set; }          // Id lệnh (có thể null)
        public string? Info { get; set; }           // Mô tả thêm
        public DateTime Time { get; set; }          // Thời gian UTC
    }
}
