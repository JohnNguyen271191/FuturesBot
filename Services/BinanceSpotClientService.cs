using FuturesBot.Config;
using FuturesBot.IServices;
using FuturesBot.Models;
using System.Globalization;
using FuturesBot.Infrastructure.Binance;
using System.Text.Json;
using static FuturesBot.Utils.EnumTypesHelper;
using System.Net.Http;

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
        private readonly SpotUrls _urls;
        private readonly SymbolRulesService _rulesService;
        private readonly SlackNotifierService _slack;

        private readonly IBinanceTimeProvider _time;
        private readonly IBinanceSigner _signer;

        // Stop-limit price buffer from stop trigger for SELL stop (avoid immediate reject).
        private const decimal DefaultStopLimitBufferPercent = 0.001m; // 0.10%

        public BinanceSpotClientService(BotConfig config, SlackNotifierService slack)
        {
            _config = config;
            _urls = config.Spot.Urls;
            _http = new HttpClient { BaseAddress = new Uri(_urls.BaseUrl) };
            _http.DefaultRequestHeaders.Add("X-MBX-APIKEY", config.ApiKey);

            _slack = slack;
            _rulesService = new SymbolRulesService(_http, _config.Spot.Urls.ExchangeInfoUrl);
            _signer = new BinanceSigner(_config.ApiSecret);
            _time = new BinanceTimeProvider(_http, _urls.TimeUrl, _slack);
        }

        private static string NormalizeSymbol(string? symbol)
        {
            var s = (symbol ?? string.Empty).Trim().ToUpperInvariant();
            // Common typo guard: FDUSD pair is sometimes mistyped as FDSDT.
            if (s.EndsWith("FDSDT", StringComparison.Ordinal))
                s = s[..^5] + "FDUSD";
            return s;
        }

        // =========================
        // Signing helpers
        // =========================

        private string Sign(string queryString) => _signer.Sign(queryString);

        private string BuildSignedQuery(Dictionary<string, string> parameters)
        {
            parameters["timestamp"] = _time.GetTimestampMsAsync().GetAwaiter().GetResult().ToString(CultureInfo.InvariantCulture);

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
            symbol = NormalizeSymbol(symbol);
            interval = (interval ?? string.Empty).Trim();

            // Build query safely (helps avoid 400 due to stray spaces / bad chars)
            var url = $"{_urls.KlinesUrl}?symbol={Uri.EscapeDataString(symbol)}&interval={Uri.EscapeDataString(interval)}&limit={limit}";
            var resp = await _http.GetAsync(url);

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                // Binance spot returns useful JSON error, e.g. {"code":-1121,"msg":"Invalid symbol."}
                throw new HttpRequestException($"Spot klines failed ({(int)resp.StatusCode} {resp.ReasonPhrase}). url={url}. body={body}");
            }

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
            symbol = NormalizeSymbol(symbol);
            var resp = await _http.GetAsync($"/api/v3/ticker/price?symbol={Uri.EscapeDataString(symbol)}");
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            return decimal.Parse(doc.RootElement.GetProperty("price").GetString()!, CultureInfo.InvariantCulture);
        }

        public async Task<(decimal bid, decimal ask)> GetBestBidAskAsync(string symbol)
        {
            symbol = NormalizeSymbol(symbol);
            var resp = await _http.GetAsync($"/api/v3/ticker/bookTicker?symbol={Uri.EscapeDataString(symbol)}");
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var bid = decimal.Parse(doc.RootElement.GetProperty("bidPrice").GetString()!, CultureInfo.InvariantCulture);
            var ask = decimal.Parse(doc.RootElement.GetProperty("askPrice").GetString()!, CultureInfo.InvariantCulture);
            return (bid, ask);
        }

        // =========================
        // Spot account
        // =========================

        public async Task<SpotHolding> GetHoldingAsync(string asset)
        {
            if (_config.PaperMode)
                return new SpotHolding { Asset = asset.ToUpperInvariant(), Free = 0m, Locked = 0m };

            var body = await SendSignedAsync(HttpMethod.Get, _urls.AccountUrl, new Dictionary<string, string>());
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

            var body = await SendSignedAsync(HttpMethod.Post, _urls.OrderUrl, p);
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

        public async Task<SpotOrderResult> PlaceLimitMakerAsync(string symbol, SignalType side, decimal quantity, decimal price)
        {
            if (_config.PaperMode)
            {
                return new SpotOrderResult { OrderId = "PAPER_LIMIT_MAKER", ExecutedQty = 0m, CummulativeQuoteQty = 0m, RawStatus = "PAPER" };
            }

            symbol = NormalizeSymbol(symbol);
            var rules = await _rulesService.GetRulesAsync(symbol);

            var roundedPrice = RoundDown(price, rules.PriceStep);
            var (roundedQty, _, _) = await RoundAndValidateQtyAsync(symbol, quantity, roundedPrice);
            if (roundedQty <= 0)
            {
                await _slack.SendAsync($"[SPOT] Reject LIMIT_MAKER {symbol} qty too small after rounding (raw={quantity}).");
                return new SpotOrderResult { OrderId = "REJECTED", ExecutedQty = 0m, CummulativeQuoteQty = 0m, RawStatus = "REJECTED" };
            }

            var parameters = new Dictionary<string, string>
            {
                ["symbol"] = symbol,
                ["side"] = side == SignalType.Long ? "BUY" : "SELL",
                ["type"] = "LIMIT_MAKER",
                ["quantity"] = roundedQty.ToString(CultureInfo.InvariantCulture),
                ["price"] = roundedPrice.ToString(CultureInfo.InvariantCulture),
                ["recvWindow"] = "5000",
            };

            var ts = await _time.GetTimestampMsAsync();
            parameters["timestamp"] = ts.ToString(CultureInfo.InvariantCulture);

            var json = await SendSignedAsync(HttpMethod.Post, _urls.OrderUrl, parameters);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new SpotOrderResult
            {
                OrderId = root.TryGetProperty("orderId", out var oid)
                    ? oid.GetInt64().ToString(CultureInfo.InvariantCulture)
                    : "UNKNOWN",
                ExecutedQty = root.TryGetProperty("executedQty", out var eq)
                    ? decimal.Parse(eq.GetString() ?? "0", CultureInfo.InvariantCulture)
                    : 0m,
                CummulativeQuoteQty = root.TryGetProperty("cummulativeQuoteQty", out var cq)
                    ? decimal.Parse(cq.GetString() ?? "0", CultureInfo.InvariantCulture)
                    : 0m,
                RawStatus = root.TryGetProperty("status", out var st) ? (st.GetString() ?? "") : ""
            };
        }

        public async Task<SpotOrderStatus> GetOrderStatusAsync(string symbol, string orderId)
        {
            if (_config.PaperMode)
            {
                return new SpotOrderStatus { OrderId = orderId, Status = "PAPER", OrigQty = 0m, ExecutedQty = 0m, Price = 0m, Side = "", Type = "" };
            }

            symbol = NormalizeSymbol(symbol);
            var body = await SendSignedAsync(HttpMethod.Get, _urls.OrderUrl, new Dictionary<string, string>
            {
                ["symbol"] = symbol,
                ["orderId"] = orderId,
            });

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            return new SpotOrderStatus
            {
                OrderId = orderId,
                Status = root.TryGetProperty("status", out var st) ? (st.GetString() ?? "") : "",
                OrigQty = root.TryGetProperty("origQty", out var oq) ? decimal.Parse(oq.GetString() ?? "0", CultureInfo.InvariantCulture) : 0m,
                ExecutedQty = root.TryGetProperty("executedQty", out var eq) ? decimal.Parse(eq.GetString() ?? "0", CultureInfo.InvariantCulture) : 0m,
                Price = root.TryGetProperty("price", out var px) ? decimal.Parse(px.GetString() ?? "0", CultureInfo.InvariantCulture) : 0m,
                Side = root.TryGetProperty("side", out var sd) ? (sd.GetString() ?? "") : "",
                Type = root.TryGetProperty("type", out var tp) ? (tp.GetString() ?? "") : "",
            };
        }

        public async Task CancelOrderAsync(string symbol, string orderId)
        {
            if (_config.PaperMode)
                return;

            symbol = NormalizeSymbol(symbol);
            await SendSignedAsync(HttpMethod.Delete, _urls.OrderUrl, new Dictionary<string, string>
            {
                ["symbol"] = symbol,
                ["orderId"] = orderId,
            });
        }

        public async Task<SpotOrderResult> PlaceMarketBuyByQuoteAsync(string symbol, decimal quoteOrderQty)
        {
            if (_config.PaperMode)
            {
                return new SpotOrderResult { OrderId = "PAPER_BUY_QUOTE", ExecutedQty = 0m, CummulativeQuoteQty = quoteOrderQty, RawStatus = "PAPER" };
            }

            // BUY MARKET using quoteOrderQty (FDUSD amount) to avoid qty rounding to zero.
            var parameters = new Dictionary<string, string>
            {
                ["symbol"] = symbol,
                ["side"] = "BUY",
                ["type"] = "MARKET",
                ["quoteOrderQty"] = quoteOrderQty.ToString(CultureInfo.InvariantCulture),
                ["recvWindow"] = "5000",
                ["timestamp"] = _time.GetTimestampMsAsync().GetAwaiter().GetResult().ToString(CultureInfo.InvariantCulture),
            };

            var json = await SendSignedAsync(HttpMethod.Post, _urls.OrderUrl, parameters);
            using var doc = JsonDocument.Parse(json);

            // For MARKET orders Binance returns: orderId, executedQty, cummulativeQuoteQty, status...
            var root = doc.RootElement;
            var orderId = root.TryGetProperty("orderId", out var oid)
                ? oid.GetInt64().ToString(CultureInfo.InvariantCulture)
                : "UNKNOWN";

            var executedQty = root.TryGetProperty("executedQty", out var exq)
                ? decimal.Parse(exq.GetString() ?? "0", CultureInfo.InvariantCulture)
                : 0m;

            var cumQuote = root.TryGetProperty("cummulativeQuoteQty", out var cqq)
                ? decimal.Parse(cqq.GetString() ?? "0", CultureInfo.InvariantCulture)
                : quoteOrderQty;

            var status = root.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";

            return new SpotOrderResult
            {
                OrderId = orderId,
                ExecutedQty = executedQty,
                CummulativeQuoteQty = cumQuote,
                RawStatus = status
            };
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

            var body = await SendSignedAsync(HttpMethod.Post, _urls.OcoOrderUrl, p);

            // Response contains orderListId.
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("orderListId", out var listId))
                return listId.GetInt64().ToString(CultureInfo.InvariantCulture);

            return "UNKNOWN";
        }

        public async Task<SpotOrderResult> PlaceLimitBuyAsync(string symbol, decimal quantity, decimal price)
        {
            if (_config.PaperMode)
            {
                return new SpotOrderResult
                {
                    OrderId = "PAPER_LIMIT_BUY",
                    ExecutedQty = 0m,
                    CummulativeQuoteQty = 0m,
                    RawStatus = "PAPER"
                };
            }

            var rules = await _rulesService.GetRulesAsync(symbol);

            // Round price/qty to exchange rules
            var roundedPrice = RoundDown(price, rules.PriceStep);
            var (roundedQty, _, _) = await RoundAndValidateQtyAsync(symbol, quantity, roundedPrice);

            if (roundedQty <= 0)
            {
                await _slack.SendAsync($"[SPOT] Reject order {symbol} qty too small after rounding (raw={quantity}).");
                return new SpotOrderResult { OrderId = "REJECTED", ExecutedQty = 0m, CummulativeQuoteQty = 0m, RawStatus = "REJECTED" };
            }

            var parameters = new Dictionary<string, string>
            {
                ["symbol"] = symbol,
                ["side"] = "BUY",
                ["type"] = "LIMIT",
                ["timeInForce"] = "GTC",
                ["quantity"] = roundedQty.ToString(CultureInfo.InvariantCulture),
                ["price"] = roundedPrice.ToString(CultureInfo.InvariantCulture),
                ["recvWindow"] = "5000",
            };

            var ts = await _time.GetTimestampMsAsync();
            parameters["timestamp"] = ts.ToString(CultureInfo.InvariantCulture);

            var json = await SendSignedAsync(HttpMethod.Post, _urls.OrderUrl, parameters);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new SpotOrderResult
            {
                OrderId = root.TryGetProperty("orderId", out var oid)
                    ? oid.GetInt64().ToString(CultureInfo.InvariantCulture)
                    : "UNKNOWN",
                ExecutedQty = root.TryGetProperty("executedQty", out var eq)
                    ? decimal.Parse(eq.GetString() ?? "0", CultureInfo.InvariantCulture)
                    : 0m,
                CummulativeQuoteQty = root.TryGetProperty("cummulativeQuoteQty", out var cq)
                    ? decimal.Parse(cq.GetString() ?? "0", CultureInfo.InvariantCulture)
                    : 0m,
                RawStatus = root.TryGetProperty("status", out var st) ? (st.GetString() ?? "") : ""
            };
        }

        public async Task CancelAllOpenOrdersAsync(string symbol)
        {
            if (_config.PaperMode)
                return;

            // Spot: DELETE /api/v3/openOrders?symbol=...
            await SendSignedAsync(HttpMethod.Delete, _urls.AllOpenOrdersUrl, new Dictionary<string, string>
            {
                ["symbol"] = symbol
            });
        }

        public async Task<IReadOnlyList<OpenOrderInfo>> GetOpenOrdersAsync(string symbol)
        {
            if (_config.PaperMode)
                return Array.Empty<OpenOrderInfo>();

            var body = await SendSignedAsync(HttpMethod.Get, _urls.OpenOrdersUrl, new Dictionary<string, string>
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
                            : 0m,
                        TimeMs = o.TryGetProperty("time", out var tm) ? tm.GetInt64() : 0L
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