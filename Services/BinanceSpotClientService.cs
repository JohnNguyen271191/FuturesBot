using FuturesBot.Config;
using FuturesBot.IServices;
using FuturesBot.Models;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static FuturesBot.Utils.EnumTypesHelper;

namespace FuturesBot.Services
{
    /// <summary>
    /// Binance Spot implementation.
    ///
    /// Notes:
    /// - Spot has no futures position. We expose holdings via GetHoldingAsync.
    /// - Supports OCO for a long position exit (SELL OCO = TP LIMIT + SL STOP_LIMIT).
    /// - Rounding/validation uses SymbolRulesService (tickSize / stepSize / minQty / minNotional).
    /// </summary>
    public sealed class BinanceSpotClientService : ISpotExchangeService
    {
        private readonly HttpClient _http;
        private readonly BotConfig _config;
        private readonly SymbolRulesService _rulesService;
        private readonly SlackNotifierService _slack;

        private long _serverTimeOffsetMs;
        private DateTime _lastTimeSyncUtc = DateTime.MinValue;

        // Stop-limit price buffer from stop trigger for SELL stop (avoid immediate reject).
        private const decimal DefaultStopLimitBufferPercent = 0.001m; // 0.10%

        public BinanceSpotClientService(BotConfig config)
        {
            _config = config;
            _http = new HttpClient { BaseAddress = new Uri(config.Urls.BaseUrl) };
            _http.DefaultRequestHeaders.Add("X-MBX-APIKEY", config.ApiKey);

            _rulesService = new SymbolRulesService(_http, _config);
            _slack = new SlackNotifierService(_config);
        }

        // =========================
        // Time sync
        // =========================

        private async Task EnsureServerTimeSyncedAsync()
        {
            if (_lastTimeSyncUtc != DateTime.MinValue && (DateTime.UtcNow - _lastTimeSyncUtc) <= TimeSpan.FromMinutes(5))
                return;

            try
            {
                var resp = await _http.GetAsync(_config.Urls.TimeUrl);
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync();

                using var doc = JsonDocument.Parse(json);
                var serverTime = doc.RootElement.GetProperty("serverTime").GetInt64();
                var local = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                _serverTimeOffsetMs = local - serverTime;
                _lastTimeSyncUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                // Best-effort; still allow request
                await _slack.SendAsync($"[SPOT TIME SYNC ERROR] {ex.Message}");
            }
        }

        private long GetBinanceTimestampMs()
        {
            EnsureServerTimeSyncedAsync().GetAwaiter().GetResult();
            var local = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return local - _serverTimeOffsetMs;
        }

        // =========================
        // Signing helpers
        // =========================

        private string Sign(string queryString)
        {
            var keyBytes = Encoding.UTF8.GetBytes(_config.ApiSecret);
            using var hmac = new HMACSHA256(keyBytes);
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(queryString));
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        private string BuildSignedQuery(Dictionary<string, string> parameters)
        {
            parameters["timestamp"] = GetBinanceTimestampMs().ToString(CultureInfo.InvariantCulture);

            // Keep stable ordering for signature
            var qs = string.Join("&", parameters.OrderBy(k => k.Key).Select(kvp => $"{kvp.Key}={kvp.Value}"));
            var sig = Sign(qs);
            return $"{qs}&signature={sig}";
        }

        private async Task<string> SendSignedAsync(HttpMethod method, string path, Dictionary<string, string> parameters)
        {
            var signed = BuildSignedQuery(parameters);
            var url = string.IsNullOrWhiteSpace(path) ? $"?{signed}" : $"{path}?{signed}";

            using var req = new HttpRequestMessage(method, url);
            var resp = await _http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                await _slack.SendAsync($"[SPOT ERROR] {method} {path} => {body}");
            }

            return body;
        }

        // =========================
        // Rounding helpers
        // =========================

        private static decimal RoundDown(decimal value, decimal step)
        {
            return SymbolRulesService.TruncateToStep(value, step);
        }

        private static decimal EnsurePositive(decimal v) => v < 0 ? 0 : v;

        private async Task<(decimal qty, decimal priceForNotional, SymbolRules rules)> RoundAndValidateQtyAsync(
            string symbol,
            decimal rawQty,
            decimal priceForNotional)
        {
            var rules = await _rulesService.GetRulesAsync(symbol);

            var qty = RoundDown(rawQty, rules.QtyStep);
            qty = EnsurePositive(qty);

            if (rules.MinQty > 0 && qty < rules.MinQty)
                return (0m, priceForNotional, rules);

            var notional = qty * priceForNotional;
            if (rules.MinNotional > 0 && notional < rules.MinNotional)
                return (0m, priceForNotional, rules);

            return (qty, priceForNotional, rules);
        }

        private static decimal RoundPriceDown(decimal price, decimal tick)
        {
            if (tick <= 0) return price;
            return Math.Floor(price / tick) * tick;
        }

        // =========================
        // Market data
        // =========================

        public async Task<IReadOnlyList<Candle>> GetRecentCandlesAsync(string symbol, string interval, int limit = 200)
        {
            var url = $"{_config.Urls.KlinesUrl}?symbol={symbol}&interval={interval}&limit={limit}";
            var resp = await _http.GetAsync(url);
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync();
            var arr = JsonSerializer.Deserialize<List<List<JsonElement>>>(json) ?? [];

            var result = new List<Candle>(arr.Count);
            foreach (var row in arr)
            {
                result.Add(new Candle
                {
                    OpenTime = DateTimeOffset.FromUnixTimeMilliseconds(row[0].GetInt64()).UtcDateTime,
                    Open = decimal.Parse(row[1].GetString()!, CultureInfo.InvariantCulture),
                    High = decimal.Parse(row[2].GetString()!, CultureInfo.InvariantCulture),
                    Low = decimal.Parse(row[3].GetString()!, CultureInfo.InvariantCulture),
                    Close = decimal.Parse(row[4].GetString()!, CultureInfo.InvariantCulture),
                    Volume = decimal.Parse(row[5].GetString()!, CultureInfo.InvariantCulture)
                });
            }
            return result;
        }

        public async Task<decimal> GetLastPriceAsync(string symbol)
        {
            var resp = await _http.GetAsync($"/api/v3/ticker/price?symbol={symbol}");
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            return decimal.Parse(doc.RootElement.GetProperty("price").GetString()!, CultureInfo.InvariantCulture);
        }

        // =========================
        // Spot account
        // =========================

        public async Task<SpotHolding> GetHoldingAsync(string asset)
        {
            if (_config.PaperMode)
                return new SpotHolding { Asset = asset.ToUpperInvariant(), Free = 0m, Locked = 0m };

            var body = await SendSignedAsync(HttpMethod.Get, _config.Urls.AccountUrl, new Dictionary<string, string>());
            using var doc = JsonDocument.Parse(body);

            if (!doc.RootElement.TryGetProperty("balances", out var balances) || balances.ValueKind != JsonValueKind.Array)
                return new SpotHolding { Asset = asset.ToUpperInvariant(), Free = 0m, Locked = 0m };

            foreach (var b in balances.EnumerateArray())
            {
                var a = b.GetProperty("asset").GetString();
                if (!string.Equals(a, asset, StringComparison.OrdinalIgnoreCase))
                    continue;

                var free = decimal.Parse(b.GetProperty("free").GetString() ?? "0", CultureInfo.InvariantCulture);
                var locked = decimal.Parse(b.GetProperty("locked").GetString() ?? "0", CultureInfo.InvariantCulture);
                return new SpotHolding { Asset = asset.ToUpperInvariant(), Free = free, Locked = locked };
            }

            return new SpotHolding { Asset = asset.ToUpperInvariant(), Free = 0m, Locked = 0m };
        }

        // =========================
        // Orders
        // =========================

        public async Task<SpotOrderResult> PlaceSpotOrderAsync(string symbol, SignalType side, decimal quantity, decimal? limitPrice = null)
        {
            if (_config.PaperMode)
                return new SpotOrderResult { OrderId = "PAPER", ExecutedQty = 0m, CummulativeQuoteQty = 0m, RawStatus = "PAPER" };

            var last = await GetLastPriceAsync(symbol);
            var priceForNotional = limitPrice ?? last;
            var (qty, _, rules) = await RoundAndValidateQtyAsync(symbol, quantity, priceForNotional);

            if (qty <= 0)
            {
                await _slack.SendAsync($"[SPOT] Reject order {symbol} qty too small after rounding (raw={quantity}).");
                return new SpotOrderResult { OrderId = "REJECT_QTY", ExecutedQty = 0m, CummulativeQuoteQty = 0m, RawStatus = "REJECT_QTY" };
            }

            var p = new Dictionary<string, string>
            {
                ["symbol"] = symbol,
                ["side"] = side == SignalType.Long ? "BUY" : "SELL",
            };

            if (limitPrice.HasValue)
            {
                var px = RoundPriceDown(limitPrice.Value, rules.PriceStep);
                p["type"] = "LIMIT";
                p["timeInForce"] = "GTC";
                p["price"] = px.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                p["type"] = "MARKET";
            }

            p["quantity"] = qty.ToString(CultureInfo.InvariantCulture);

            var body = await SendSignedAsync(HttpMethod.Post, _config.Urls.OrderUrl, p);
            using var doc = JsonDocument.Parse(body);
            var result = new SpotOrderResult();

            if (doc.RootElement.TryGetProperty("orderId", out var oid))
                result.OrderId = oid.GetInt64().ToString(CultureInfo.InvariantCulture);
            else
                result.OrderId = "UNKNOWN";

            if (doc.RootElement.TryGetProperty("executedQty", out var exQty))
                result.ExecutedQty = decimal.Parse(exQty.GetString() ?? "0", CultureInfo.InvariantCulture);

            if (doc.RootElement.TryGetProperty("cummulativeQuoteQty", out var cq))
                result.CummulativeQuoteQty = decimal.Parse(cq.GetString() ?? "0", CultureInfo.InvariantCulture);

            if (doc.RootElement.TryGetProperty("status", out var st))
                result.RawStatus = st.GetString() ?? "";

            return result;
        }

        public async Task<string> PlaceOcoSellAsync(string symbol, decimal quantity, decimal takeProfitPrice, decimal stopPrice, decimal stopLimitPrice)
        {
            if (_config.PaperMode)
                return "PAPER_OCO";

            // Binance: OCO is only supported for SPOT.
            // We place SELL OCO (TP LIMIT + SL STOP_LIMIT).

            var last = await GetLastPriceAsync(symbol);
            var (qty, _, rules) = await RoundAndValidateQtyAsync(symbol, quantity, last);
            if (qty <= 0)
            {
                await _slack.SendAsync($"[SPOT] Reject OCO {symbol} qty too small after rounding (raw={quantity}).");
                return "REJECT_QTY";
            }

            // Round prices
            var tp = RoundPriceDown(takeProfitPrice, rules.PriceStep);
            var stop = RoundPriceDown(stopPrice, rules.PriceStep);

            // Ensure stopLimitPrice is below stop (SELL) and rounded.
            var suggestedStopLimit = stopLimitPrice;
            if (suggestedStopLimit <= 0)
                suggestedStopLimit = stop * (1m - DefaultStopLimitBufferPercent);

            var stopLimit = RoundPriceDown(suggestedStopLimit, rules.PriceStep);
            if (stopLimit >= stop)
            {
                stopLimit = RoundPriceDown(stop * (1m - DefaultStopLimitBufferPercent), rules.PriceStep);
            }

            if (tp <= 0 || stop <= 0 || stopLimit <= 0)
                return "REJECT_PRICE";

            var p = new Dictionary<string, string>
            {
                ["symbol"] = symbol,
                ["side"] = "SELL",
                ["quantity"] = qty.ToString(CultureInfo.InvariantCulture),
                ["price"] = tp.ToString(CultureInfo.InvariantCulture),
                ["stopPrice"] = stop.ToString(CultureInfo.InvariantCulture),
                ["stopLimitPrice"] = stopLimit.ToString(CultureInfo.InvariantCulture),
                ["stopLimitTimeInForce"] = "GTC",
            };

            var body = await SendSignedAsync(HttpMethod.Post, _config.Urls.OcoOrderUrl, p);

            // Response contains orderListId.
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("orderListId", out var listId))
                return listId.GetInt64().ToString(CultureInfo.InvariantCulture);

            return "UNKNOWN";
        }

        public async Task CancelAllOpenOrdersAsync(string symbol)
        {
            if (_config.PaperMode)
                return;

            // Spot: DELETE /api/v3/openOrders?symbol=...
            await SendSignedAsync(HttpMethod.Delete, _config.Urls.AllOpenOrdersUrl, new Dictionary<string, string>
            {
                ["symbol"] = symbol
            });
        }

        public async Task<IReadOnlyList<OpenOrderInfo>> GetOpenOrdersAsync(string symbol)
        {
            if (_config.PaperMode)
                return Array.Empty<OpenOrderInfo>();

            var body = await SendSignedAsync(HttpMethod.Get, _config.Urls.OpenOrdersUrl, new Dictionary<string, string>
            {
                ["symbol"] = symbol
            });

            // spot openOrders returns array
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    return Array.Empty<OpenOrderInfo>();

                var list = new List<OpenOrderInfo>();
                foreach (var o in doc.RootElement.EnumerateArray())
                {
                    list.Add(new OpenOrderInfo
                    {
                        Symbol = o.GetProperty("symbol").GetString() ?? symbol,
                        // Model uses string OrderId for cross-market compatibility
                        OrderId = o.GetProperty("orderId").GetInt64().ToString(CultureInfo.InvariantCulture),
                        Side = o.GetProperty("side").GetString() ?? "",
                        Type = o.GetProperty("type").GetString() ?? "",
                        Price = decimal.Parse(o.GetProperty("price").GetString() ?? "0", CultureInfo.InvariantCulture),
                        OrigQty = decimal.Parse(o.GetProperty("origQty").GetString() ?? "0", CultureInfo.InvariantCulture),
                        ExecutedQty = decimal.Parse(o.GetProperty("executedQty").GetString() ?? "0", CultureInfo.InvariantCulture),
                        // spot openOrders includes stopPrice for stop/oco legs
                        StopPrice = o.TryGetProperty("stopPrice", out var sp)
                            ? decimal.Parse(sp.GetString() ?? "0", CultureInfo.InvariantCulture)
                            : 0m
                    });
                }

                return list;
            }
            catch
            {
                return Array.Empty<OpenOrderInfo>();
            }
        }
    }
}
