using FuturesBot.Config;
using FuturesBot.IServices;
using FuturesBot.Models;
using FuturesBot.Utils;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static FuturesBot.Utils.EnumTypesHelper;

namespace FuturesBot.Services
{
    public class BinanceFuturesClientService : IExchangeClientService
    {
        private readonly HttpClient _http;
        private readonly BotConfig _config;
        private long _serverTimeOffsetMs = 0;
        private DateTime _lastTimeSync = DateTime.MinValue;
        private readonly SymbolRulesService _rulesService;

        public BinanceFuturesClientService(BotConfig config)
        {
            _config = config;
            _http = new HttpClient { BaseAddress = new Uri(config.Urls.BaseUrl) };
            _http.DefaultRequestHeaders.Add("X-MBX-APIKEY", config.ApiKey);
            _rulesService = new SymbolRulesService(_http, _config);
        }

        // ==========================
        //        PUBLIC API
        // ==========================

        public async Task<IReadOnlyList<Candle>> GetRecentCandlesAsync(string symbol, string interval, int limit = 200)
        {
            var url = $"{_config.Urls.KlinesUrl}?symbol={symbol}&interval={interval}&limit={limit}";
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    var resp = await _http.GetAsync(url);
                    resp.EnsureSuccessStatusCode();

                    var json = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);

                    var list = new List<Candle>();

                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        long openTimeMs = item[0].GetInt64();
                        var openTime = DateTimeOffset.FromUnixTimeMilliseconds(openTimeMs).UtcDateTime;

                        list.Add(new Candle
                        {
                            OpenTime = openTime,
                            Open = decimal.Parse(item[1].GetString()!),
                            High = decimal.Parse(item[2].GetString()!),
                            Low = decimal.Parse(item[3].GetString()!),
                            Close = decimal.Parse(item[4].GetString()!),
                            Volume = decimal.Parse(item[5].GetString()!)
                        });
                    }

                    return list;
                }
                catch (IOException ex) when (attempt < 3)
                {
                    Console.WriteLine($"[WARN] IO error when calling Binance (attempt {attempt}): {ex.Message}");
                    await Task.Delay(500 * attempt);
                }
                catch (HttpRequestException ex) when (attempt < 3)
                {
                    Console.WriteLine($"[WARN] HTTP error when calling Binance (attempt {attempt}): {ex.Message}");
                    await Task.Delay(500 * attempt);
                }
            }
            throw new Exception("Failed to get candles from Binance after 3 attempts.");
        }

        public async Task<bool> PlaceFuturesOrderAsync(
            string symbol,
            SignalType side,
            decimal quantity,
            decimal entryPrice,
            decimal stopLoss,
            decimal takeProfit,
            int leverage,
            SlackNotifierService slackNotifierService,
            bool marketOrder = true)
        {
            var rules = await _rulesService.GetRulesAsync(symbol);

            var qty = SymbolRulesService.TruncateToStep(quantity, rules.QtyStep);
            if (qty < rules.MinQty)
                qty = rules.MinQty;

            decimal notional = entryPrice * qty;
            if (notional < rules.MinNotional)
            {
                qty = SymbolRulesService.TruncateToStep(rules.MinNotional, rules.QtyStep);
                if (qty < rules.MinQty)
                    qty = rules.MinQty;
            }

            var entry = SymbolRulesService.TruncateToStep(entryPrice, rules.PriceStep);
            var sl = SymbolRulesService.TruncateToStep(stopLoss, rules.PriceStep);
            var tp = SymbolRulesService.TruncateToStep(takeProfit, rules.PriceStep);

            // PAPER MODE: chỉ log, không call API
            if (_config.PaperMode)
            {
                Console.WriteLine("[PAPER MODE] Giả lập gửi lệnh futures:");
                Console.WriteLine($" -> {side} {quantity} {symbol} @ {entryPrice}");
                Console.WriteLine($" -> SL (STOP_MARKET) : {stopLoss}");
                Console.WriteLine($" -> TP (TAKE_PROFIT_MARKET) : {takeProfit}");
                return true;
            }

            // 1. set leverage
            await SetLeverageAsync(symbol, leverage);

            // 2. gửi lệnh vào (entry)
            string sideStr = side == SignalType.Long ? "BUY" : "SELL";
            string closeSideStr = side == SignalType.Long ? "SELL" : "BUY";
            string positionSide = side == SignalType.Long ? "LONG" : "SHORT";
            string typeStr = marketOrder ? "MARKET" : "LIMIT";

            var entryParams = new Dictionary<string, string>
            {
                ["symbol"] = symbol,
                ["side"] = sideStr,
                ["type"] = typeStr,
                ["quantity"] = qty.ToString(CultureInfo.InvariantCulture),
                ["recvWindow"] = "60000",
                ["positionSide"] = positionSide,
            };

            if (!marketOrder)
            {
                //entryParams["timeInForce"] = "GTC";
                entryParams["price"] = entry.ToString(CultureInfo.InvariantCulture);
            }

            await slackNotifierService.SendAsync("=== SEND ENTRY ORDER ===");
            var entryResp = await SignedPostAsync($"{_config.Urls.OrderUrl}", entryParams);
            if (entryResp.Contains("[BINANCE ERROR]"))
            {
                await slackNotifierService.SendAsync($"Send order: symbol={symbol}, side={sideStr}, qty={qty} - {rules.QtyStep}, price={entry} - {rules.PriceStep}, sl={sl}, tp={tp}");
                await slackNotifierService.SendAsync(entryResp);
                return false;
            }
            await slackNotifierService.SendAsync($"[ENTRY RESP] {entryResp}");

            // 3. Đặt STOP_MARKET (Stop Loss) qua Algo Order API (algoType=CONDITIONAL)
            var slAlgoParams = new Dictionary<string, string>
            {
                ["symbol"] = symbol,
                ["side"] = closeSideStr,                      // SELL để đóng long, BUY để đóng short
                ["positionSide"] = positionSide,              // chỉ dùng khi Hedge mode; nếu One-way mode có thể vẫn gửi (server sẽ ignore) atau omit nếu muốn
                ["algoType"] = "CONDITIONAL",                 // bắt buộc cho conditional algo orders
                ["type"] = "STOP_MARKET",                     // loại order bên trong algo API
                ["triggerPrice"] = sl.ToString(CultureInfo.InvariantCulture), // trigger price
                ["closePosition"] = "true",                   // đóng toàn bộ vị thế
                ["recvWindow"] = "60000"
            };

            await slackNotifierService.SendAsync("=== SEND STOP LOSS (ALGO) ===");
            var slResp = await SignedPostAsync($"{_config.Urls.AlgoOrderUrl}", slAlgoParams);
            await slackNotifierService.SendAsync($"[SL RESP] {slResp}");

            // 4. Đặt TAKE_PROFIT_MARKET (Take Profit) qua Algo Order API
            var tpAlgoParams = new Dictionary<string, string>
            {
                ["symbol"] = symbol,
                ["side"] = closeSideStr,                      // SELL để đóng long, BUY để đóng short
                ["positionSide"] = positionSide,
                ["algoType"] = "CONDITIONAL",
                ["type"] = "TAKE_PROFIT_MARKET",
                ["triggerPrice"] = tp.ToString(CultureInfo.InvariantCulture),
                ["closePosition"] = "true",
                ["recvWindow"] = "60000"
            };

            await slackNotifierService.SendAsync("=== SEND TAKE PROFIT (ALGO) ===");
            var tpResp = await SignedPostAsync($"{_config.Urls.AlgoOrderUrl}", tpAlgoParams);
            await slackNotifierService.SendAsync($"[TP RESP] {tpResp}");

            return true;
        }

        public async Task<PositionInfo> GetPositionAsync(string symbol)
        {
            string json;
            try
            {
                json = await SignedGetAsync($"{_config.Urls.PositionRiskUrl}", new Dictionary<string, string>
                {
                    ["symbol"] = symbol
                });
            }
            catch (Exception ex)
            {
                throw new Exception($"GetPositionAsync API error for {symbol}: {ex.Message}");
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            {
                throw new Exception($"GetPositionAsync empty array for {symbol}. Raw json: {json}");
            }

            JsonElement? chosen = null;

            foreach (var el in root.EnumerateArray())
            {
                var sym = el.GetProperty("symbol").GetString();
                if (!string.Equals(sym, symbol, StringComparison.OrdinalIgnoreCase))
                    continue;

                var amtStr = el.GetProperty("positionAmt").GetString() ?? "0";
                var amt = decimal.Parse(amtStr, CultureInfo.InvariantCulture);

                if (amt != 0)
                {
                    chosen = el;
                    break;
                }

                chosen ??= el; // giữ lại nếu không có positionAmt != 0
            }

            if (!chosen.HasValue)
                throw new Exception($"GetPositionAsync cannot find symbol {symbol} in array.");

            var elx = chosen.Value;

            decimal positionAmt = decimal.Parse(elx.GetProperty("positionAmt").GetString() ?? "0", CultureInfo.InvariantCulture);
            decimal entryPrice = decimal.Parse(elx.GetProperty("entryPrice").GetString() ?? "0", CultureInfo.InvariantCulture);
            decimal markPrice = decimal.Parse(elx.GetProperty("markPrice").GetString() ?? "0", CultureInfo.InvariantCulture);
            long updateMs = elx.GetProperty("updateTime").GetInt64();

            return new PositionInfo
            {
                Symbol = symbol,
                PositionAmt = positionAmt,
                EntryPrice = entryPrice,
                MarkPrice = markPrice,
                UpdateTime = DateTimeOffset.FromUnixTimeMilliseconds(updateMs).UtcDateTime
            };
        }

        public async Task<bool> HasOpenPositionOrOrderAsync(string symbol)
        {
            if (_config.PaperMode)
                return false;

            var posParams = new Dictionary<string, string>
            {
                ["symbol"] = symbol,
                ["recvWindow"] = "60000"
            };

            var posJson = await SignedGetAsync($"{_config.Urls.PositionRiskUrl}", posParams);

            try
            {
                using var doc = JsonDocument.Parse(posJson);
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var sym = el.GetProperty("symbol").GetString();
                    if (!string.Equals(sym, symbol, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var positionAmtStr = el.GetProperty("positionAmt").GetString() ?? "0";
                    if (decimal.TryParse(positionAmtStr, out var positionAmt))
                    {
                        if (positionAmt != 0)
                        {
                            return true;
                        }
                    }
                }
            }
            catch
            {
                Console.WriteLine("[WARN] Cannot parse positionRisk response.");
            }

            var orderParams = new Dictionary<string, string>
            {
                ["symbol"] = symbol,
                ["recvWindow"] = "60000"
            };

            var ordersJson = await SignedGetAsync($"{_config.Urls.OpenOrdersUrl}", orderParams);

            try
            {
                using var doc = JsonDocument.Parse(ordersJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                {
                    return true;
                }
            }
            catch
            {
                Console.WriteLine("[WARN] Cannot parse openOrders response.");
            }

            return false;
        }

        public async Task<UserTradeInfo?> GetLastUserTradeAsync(string symbol, DateTime since)
        {
            long fromMs = new DateTimeOffset(since).ToUnixTimeMilliseconds();

            var json = await SignedGetAsync($"{_config.Urls.UserTradesUrl}", new Dictionary<string, string>
            {
                ["symbol"] = symbol,
                ["startTime"] = fromMs.ToString(),
                ["limit"] = "50"
            });

            using var doc = JsonDocument.Parse(json);
            var arr = doc.RootElement;

            if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
                return null;

            // Lấy trade mới nhất
            var last = arr[arr.GetArrayLength() - 1];

            return new UserTradeInfo
            {
                Symbol = last.GetProperty("symbol").GetString() ?? symbol,
                Id = last.GetProperty("id").GetInt64(),
                Time = DateTimeOffset.FromUnixTimeMilliseconds(last.GetProperty("time").GetInt64()).UtcDateTime,
                Price = decimal.Parse(last.GetProperty("price").GetString() ?? "0"),
                Qty = decimal.Parse(last.GetProperty("qty").GetString() ?? "0"),
                IsBuyer = last.GetProperty("buyer").GetBoolean()
            };
        }

        public async Task SyncServerTimeAsync()
        {
            try
            {
                var resp = await _http.GetAsync($"{_config.Urls.BaseUrl}{_config.Urls.TimeUrl}");
                resp.EnsureSuccessStatusCode();

                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                var serverTime = doc.RootElement.GetProperty("serverTime").GetInt64();
                var localTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                _serverTimeOffsetMs = localTime - serverTime;
                _lastTimeSync = DateTime.UtcNow;

                Console.WriteLine($"[TIME OFFSET] => {_serverTimeOffsetMs} ms");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[TIME SYNC ERROR] " + ex.Message);
            }
        }

        public async Task CancelAllOpenOrdersAsync(string symbol)
        {
            long ts = GetBinanceTimestamp();

            var qs = $"symbol={symbol}&timestamp={ts}";

            var keyBytes = Encoding.UTF8.GetBytes(_config.ApiSecret);
            using var hmac = new HMACSHA256(keyBytes);
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(qs));
            var signature = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();

            string url = $"{_config.Urls.AllOpenOrdersUrl}?{qs}&signature={signature}";

            var req = new HttpRequestMessage(HttpMethod.Delete, url);

            var resp = await _http.SendAsync(req);

            string content = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"[CancelAllOpenOrdersAsync ERROR] {content}");
            }

            Console.WriteLine($"[CancelAllOpenOrdersAsync OK] {symbol} => {content}");
        }

        public async Task ClosePositionAsync(string symbol, decimal quantity)
        {
            var side = quantity > 0 ? "SELL" : "BUY";
            var positionSide = quantity > 0 ? "LONG" : "SHORT";
            await SignedPostAsync($"{_config.Urls.OrderUrl}", new Dictionary<string, string>
            {
                ["symbol"] = symbol,
                ["side"] = side,
                ["type"] = "MARKET",
                ["quantity"] = Math.Abs(quantity).ToString(CultureInfo.InvariantCulture),
                ["recvWindow"] = "60000",
                ["positionSide"] = positionSide
            });
        }

        public async Task<IReadOnlyList<OpenOrderInfo>> GetOpenOrdersAsync(string symbol)
        {
            if (_config.PaperMode)
                return Array.Empty<OpenOrderInfo>();

            var json = await SignedGetAsync($"{_config.Urls.OpenOrdersUrl}", new Dictionary<string, string>
            {
                ["symbol"] = symbol,
                ["recvWindow"] = "60000"
            });

            var list = new List<OpenOrderInfo>();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var el in root.EnumerateArray())
            {
                list.Add(new OpenOrderInfo
                {
                    Symbol = el.GetProperty("symbol").GetString() ?? symbol,
                    ClientOrderId = el.GetProperty("clientOrderId").GetString() ?? string.Empty,
                    OrderId = el.GetProperty("orderId").GetInt64().ToString(),
                    Side = el.GetProperty("side").GetString() ?? string.Empty,
                    Type = el.GetProperty("type").GetString() ?? string.Empty,
                    Price = decimal.Parse(el.GetProperty("price").GetString() ?? "0", CultureInfo.InvariantCulture),
                    OrigQty = decimal.Parse(el.GetProperty("origQty").GetString() ?? "0", CultureInfo.InvariantCulture),
                    ExecutedQty = decimal.Parse(el.GetProperty("executedQty").GetString() ?? "0", CultureInfo.InvariantCulture),
                    StopPrice = decimal.Parse(el.GetProperty("stopPrice").GetString() ?? "0", CultureInfo.InvariantCulture),
                });
            }

            return list;
        }

        public async Task<bool> PlaceStopOnlyAsync(string symbol, string side, string positionSide, decimal quantity, decimal stopPrice)
        {
            var rules = await _rulesService.GetRulesAsync(symbol);

            var qty = SymbolRulesService.TruncateToStep(quantity, rules.QtyStep);
            if (qty < rules.MinQty)
                qty = rules.MinQty;

            var stop = SymbolRulesService.TruncateToStep(stopPrice, rules.PriceStep);

            // PAPER MODE
            if (_config.PaperMode)
            {
                Console.WriteLine($"[PAPER MODE] PlaceStopOnly {side} {symbol} qty={qty} stop={stop}");
                return true;
            }

            var param = new Dictionary<string, string>
            {
                ["symbol"] = symbol,
                ["side"] = side,                      // SELL để đóng long, BUY để đóng short
                ["positionSide"] = positionSide,              // chỉ dùng khi Hedge mode; nếu One-way mode có thể vẫn gửi (server sẽ ignore) atau omit nếu muốn
                ["algoType"] = "CONDITIONAL",                 // bắt buộc cho conditional algo orders
                ["type"] = "STOP_MARKET",                     // loại order bên trong algo API
                ["triggerPrice"] = stop.ToString(CultureInfo.InvariantCulture), // trigger price
                ["closePosition"] = "true",                   // đóng toàn bộ vị thế
                ["recvWindow"] = "60000"
            };
            var resp = await SignedPostAsync($"{_config.Urls.AlgoOrderUrl}", param);

            if (resp.Contains("[BINANCE ERROR]"))
            {
                Console.WriteLine("[PlaceStopOnly ERROR] " + resp);
                return false;
            }

            return true;
        }

        public async Task<NetPnlResult> GetNetPnlAsync(string symbol, DateTime fromUtc, DateTime? toUtc = null)
        {
            var result = new NetPnlResult();

            var to = toUtc ?? DateTime.UtcNow;

            // Lấy income trong [from, to]
            var incomeList = await GetIncomeAsync(symbol, fromUtc, to);

            foreach (var i in incomeList)
            {
                // Lọc thêm lần nữa cho chắc,
                // phòng khi Binance trả dư (do startTime/endTime bị làm tròn)
                if (i.Time < fromUtc || i.Time > to)
                    continue;

                switch (i.IncomeType)
                {
                    case "REALIZED_PNL":
                        result.Realized += i.Income;
                        break;

                    case "COMMISSION":
                        result.Commission += i.Income; // commission luôn âm
                        break;

                    case "FUNDING_FEE":
                        result.Funding += i.Income; // funding âm hoặc dương
                        break;
                }
            }

            return result;
        }

        public async Task CancelStopLossOrdersAsync(string symbol)
        {
            // PAPER MODE: chỉ log, không gọi API thật
            if (_config.PaperMode)
            {
                Console.WriteLine($"[PAPER MODE] CancelStopLossOrdersAsync for {symbol}");
                return;
            }

            // 1) Lấy toàn bộ open orders
            var orders = await GetOpenOrdersAsync(symbol);
            if (orders == null || orders.Count == 0)
            {
                Console.WriteLine($"[{symbol}] Không có open orders để cancel SL.");
                return;
            }

            // 2) Các type được coi là StopLoss
            string[] slKeywords = ["STOP"];

            // 3) Lọc ra các SL order
            var slOrders = orders
                .Where(o => slKeywords.Any(k =>
                    o.Type.Contains(k, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (slOrders.Count == 0)
            {
                Console.WriteLine($"[{symbol}] Không có StopLoss orders để cancel.");
                return;
            }

            // 4) Cancel từng SL order theo orderId
            foreach (var order in slOrders)
            {
                await CancelOrderByIdAsync(symbol, order.OrderId);
            }
        }

        // ==========================
        //       PRIVATE HELPERS
        // ==========================

        private async Task SetLeverageAsync(string symbol, int leverage)
        {
            var param = new Dictionary<string, string>
            {
                ["symbol"] = symbol,
                ["leverage"] = leverage.ToString(),
                ["recvWindow"] = "60000"
            };

            var resp = await SignedPostAsync($"{_config.Urls.LeverageUrl}", param);
            Console.WriteLine("[LEVERAGE RESP] " + resp);
        }

        private async Task<string> SignedPostAsync(string path, IDictionary<string, string> parameters)
        {
            long ts = GetBinanceTimestamp();
            parameters["timestamp"] = ts.ToString();
            var sb = new StringBuilder();

            foreach (var kv in parameters)
            {
                if (sb.Length > 0) sb.Append('&');
                sb.Append(kv.Key).Append('=').Append(kv.Value);
            }

            if (sb.Length > 0) sb.Append('&');
            sb.Append("timestamp=").Append(ts);

            var queryString = sb.ToString();
            var signature = BinanceSignatureHelper.Sign(queryString, _config.ApiSecret);
            queryString += "&signature=" + signature;

            var url = path + "?" + queryString;
            var content = new StringContent("", Encoding.UTF8, "application/x-www-form-urlencoded");

            var resp = await _http.PostAsync(url, content);
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                return ($"[BINANCE ERROR] {resp.StatusCode}: {body}");
            }

            return body;
        }

        private async Task<string> SignedGetAsync(string path, IDictionary<string, string> parameters)
        {
            long ts = GetBinanceTimestamp();
            parameters["timestamp"] = ts.ToString();
            var sb = new StringBuilder();

            foreach (var kv in parameters)
            {
                if (sb.Length > 0) sb.Append('&');
                sb.Append(kv.Key).Append('=').Append(kv.Value);
            }

            if (sb.Length > 0) sb.Append('&');
            sb.Append("timestamp=").Append(ts);

            var queryString = sb.ToString();
            var signature = BinanceSignatureHelper.Sign(queryString, _config.ApiSecret);
            queryString += "&signature=" + signature;

            var url = path + "?" + queryString;

            var resp = await _http.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"[BINANCE ERROR] {resp.StatusCode}: {body}");
            }

            return body;
        }

        private long GetBinanceTimestamp()
        {
            // lần đầu hoặc sau 5 phút thì sync lại
            if (_lastTimeSync == DateTime.MinValue ||
                (DateTime.UtcNow - _lastTimeSync) > TimeSpan.FromMinutes(5))
            {
                SyncServerTimeAsync().GetAwaiter().GetResult();
            }

            var local = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return local - _serverTimeOffsetMs;
        }

        private async Task<List<IncomeRecord>> GetIncomeAsync(string symbol, DateTime startTimeUtc, DateTime endTimeUtc)
        {
            var query = new Dictionary<string, string>
            {
                ["symbol"] = symbol,
                ["limit"] = "100",
                ["startTime"] = new DateTimeOffset(startTimeUtc)
                                    .ToUnixTimeMilliseconds()
                                    .ToString(),
                ["endTime"] = new DateTimeOffset(endTimeUtc)
                                    .ToUnixTimeMilliseconds()
                                    .ToString()
            };

            var json = await SignedGetAsync($"{_config.Urls.IncomeUrl}", query);

            var list = new List<IncomeRecord>();
            using var doc = JsonDocument.Parse(json);

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                list.Add(new IncomeRecord
                {
                    Symbol = el.GetProperty("symbol").GetString() ?? "",
                    IncomeType = el.GetProperty("incomeType").GetString() ?? "",
                    Income = decimal.Parse(
                                el.GetProperty("income").GetString() ?? "0",
                                CultureInfo.InvariantCulture),
                    Time = DateTimeOffset.FromUnixTimeMilliseconds(
                                el.GetProperty("time").GetInt64()).UtcDateTime
                });
            }

            return list;
        }

        private async Task CancelOrderByIdAsync(string symbol, string orderId)
        {
            // PAPER MODE
            if (_config.PaperMode)
            {
                Console.WriteLine($"[PAPER MODE] CancelOrderById {symbol} orderId={orderId}");
                return;
            }

            long ts = GetBinanceTimestamp();

            var qs = $"symbol={symbol}&orderId={orderId}&timestamp={ts}";
            var signature = BinanceSignatureHelper.Sign(qs, _config.ApiSecret);

            string url = $"{_config.Urls.OrderUrl}?{qs}&signature={signature}";
            var req = new HttpRequestMessage(HttpMethod.Delete, url);

            var resp = await _http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"[CancelOrderByIdAsync ERROR] {symbol} orderId={orderId} => {body}");
            }
            else
            {
                Console.WriteLine($"[CancelOrderByIdAsync OK] {symbol} orderId={orderId} => {body}");
            }
        }
    }
}
