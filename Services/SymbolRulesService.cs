using FuturesBot.Config;
using System.Globalization;
using System.Text.Json;

namespace FuturesBot.Services
{
    public class SymbolRules
    {
        public string Symbol { get; set; } = "";
        public decimal PriceStep { get; set; }    // tickSize
        public decimal QtyStep { get; set; }      // stepSize
        public decimal MinQty { get; set; }       // minQty
        public decimal MinNotional { get; set; }  // MIN_NOTIONAL.notional
    }

    public class SymbolRulesService
    {
        private readonly HttpClient _http;
        private readonly BotConfig _config;
        private readonly Dictionary<string, SymbolRules> _cache = new();

        public SymbolRulesService(HttpClient http, BotConfig config)
        {
            _http = http;
            _config = config;
        }

        public async Task<SymbolRules> GetRulesAsync(string symbol)
        {
            if (_cache.TryGetValue(symbol, out var rules))
                return rules;

            var resp = await _http.GetAsync($"{_config.BaseUrl}/fapi/v1/exchangeInfo?symbol={symbol}");
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var symbols = doc.RootElement.GetProperty("symbols");
            if (symbols.GetArrayLength() == 0)
                throw new Exception($"No symbol info returned for {symbol}");

            decimal priceStep = 0, qtyStep = 0, minQty = 0, minNotional = 0;

            foreach (var s in symbols.EnumerateArray())
            {
                var sym = s.GetProperty("symbol").GetString();
                if (!string.Equals(sym, symbol, StringComparison.OrdinalIgnoreCase))
                    continue;

                var contractType = s.GetProperty("contractType").GetString();
                if (contractType == "PERPETUAL")
                {
                    foreach (var f in s.GetProperty("filters").EnumerateArray())
                    {
                        var type = f.GetProperty("filterType").GetString();
                        switch (type)
                        {
                            case "PRICE_FILTER":
                                priceStep = decimal.Parse(
                                    f.GetProperty("tickSize").GetString()!,
                                    CultureInfo.InvariantCulture);
                                break;

                            case "LOT_SIZE":
                                qtyStep = decimal.Parse(
                                    f.GetProperty("stepSize").GetString()!,
                                    CultureInfo.InvariantCulture);
                                minQty = decimal.Parse(
                                    f.GetProperty("minQty").GetString()!,
                                    CultureInfo.InvariantCulture);
                                break;

                            case "MIN_NOTIONAL":
                                minNotional = decimal.Parse(
                                    f.GetProperty("notional").GetString()!,
                                    CultureInfo.InvariantCulture);
                                break;
                        }
                    }
                    break;
                }
            }            

            rules = new SymbolRules
            {
                Symbol = symbol,
                PriceStep = priceStep,
                QtyStep = qtyStep,
                MinQty = minQty,
                MinNotional = minNotional
            };

            _cache[symbol] = rules;
            return rules;
        }

        public static decimal TruncateToStep(decimal value, decimal step)
        {
            if (step <= 0) return value;
            return Math.Floor(value / step) * step;
        }
    }
}
