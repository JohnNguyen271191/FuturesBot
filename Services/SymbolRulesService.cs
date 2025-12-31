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
        private readonly Dictionary<string, string> _baseAssetCache = new();

        public SymbolRulesService(HttpClient http, BotConfig config)
        {
            _http = http;
            _config = config;
        }

        
        public async Task<string> GetBaseAssetAsync(string symbol)
        {
            if (_baseAssetCache.TryGetValue(symbol, out var cached))
                return cached;

            // Best-effort: try exchangeInfo
            try
            {
                var resp = await _http.GetAsync($"{_config.Urls.ExchangeInfoUrl}?symbol={symbol}");
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                var symbols = doc.RootElement.GetProperty("symbols");
                if (symbols.GetArrayLength() > 0)
                {
                    var s = symbols[0];
                    if (s.TryGetProperty("baseAsset", out var ba))
                    {
                        var baseAsset = ba.GetString() ?? symbol;
                        _baseAssetCache[symbol] = baseAsset;
                        return baseAsset;
                    }
                }
            }
            catch
            {
                // ignore
            }

            // Fallback parse: BTCUSDT -> BTC (common quote assets)
            var quotes = new[] { "USDT", "USDC", "BUSD", "FDUSD", "TUSD", "BTC", "ETH" };
            foreach (var q in quotes)
            {
                if (symbol.EndsWith(q, StringComparison.OrdinalIgnoreCase))
                {
                    var baseGuess = symbol.Substring(0, symbol.Length - q.Length);
                    _baseAssetCache[symbol] = baseGuess;
                    return baseGuess;
                }
            }

            _baseAssetCache[symbol] = symbol;
            return symbol;
        }

        public async Task<SymbolRules> GetRulesAsync(string symbol)
        {
            if (_cache.TryGetValue(symbol, out var rules))
                return rules;

            // HttpClient already has BaseAddress = config.Urls.BaseUrl in clients.
            var resp = await _http.GetAsync($"{_config.Urls.ExchangeInfoUrl}?symbol={symbol}");
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

                // Futures exchangeInfo entries contain contractType; Spot entries usually don't.
                var isFuturesPerp = s.TryGetProperty("contractType", out var ctProp) &&
                                    string.Equals(ctProp.GetString(), "PERPETUAL", StringComparison.OrdinalIgnoreCase);

                if (!s.TryGetProperty("filters", out var filters) || filters.ValueKind != JsonValueKind.Array)
                    throw new Exception($"No filters returned for {symbol}");

                foreach (var f in filters.EnumerateArray())
                {
                    var type = f.GetProperty("filterType").GetString();
                    switch (type)
                    {
                        case "PRICE_FILTER":
                            priceStep = decimal.Parse(
                                f.GetProperty("tickSize").GetString()!,
                                CultureInfo.InvariantCulture);
                            break;

                        // Spot sometimes uses LOT_SIZE, Futures uses LOT_SIZE too.
                        case "LOT_SIZE":
                            qtyStep = decimal.Parse(
                                f.GetProperty("stepSize").GetString()!,
                                CultureInfo.InvariantCulture);
                            minQty = decimal.Parse(
                                f.GetProperty("minQty").GetString()!,
                                CultureInfo.InvariantCulture);
                            break;

                        // Spot might have MIN_NOTIONAL or NOTIONAL filter depending on symbol.
                        case "MIN_NOTIONAL":
                        case "NOTIONAL":
                            {
                                string? raw = null;

                                if (f.TryGetProperty("notional", out var notionalProp))
                                    raw = notionalProp.GetString();
                                else if (f.TryGetProperty("minNotional", out var minNotionalProp))
                                    raw = minNotionalProp.GetString();
                                else if (f.TryGetProperty("minNotional", out var mn2))
                                    raw = mn2.GetString();
                                else if (f.TryGetProperty("minNotional", out _))
                                    raw = null;

                                if (!string.IsNullOrWhiteSpace(raw))
                                    minNotional = decimal.Parse(raw, CultureInfo.InvariantCulture);
                            }
                            break;
                    }
                }

                // Some futures symbols might not be PERPETUAL; we only use this for trading.
                if (isFuturesPerp || !s.TryGetProperty("contractType", out _))
                    break;
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
