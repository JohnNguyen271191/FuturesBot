using FuturesBot.Config;
using FuturesBot.IServices;
using FuturesBot.Models;
using FuturesBot.Utils;
using System.Text;
using System.Text.Json;
using static FuturesBot.Utils.EnumTypesHelper;

namespace FuturesBot.Services
{
    public class BinanceFuturesClientService : IExchangeClientService
    {
        private readonly HttpClient _http;
        private readonly BotConfig _config;

        public BinanceFuturesClientService(BotConfig config)
        {
            _config = config;
            _http = new HttpClient { BaseAddress = new Uri(config.BaseUrl) };
            _http.DefaultRequestHeaders.Add("X-MBX-APIKEY", config.ApiKey);
        }

        // ==========================
        //        PUBLIC API
        // ==========================

        public async Task<IReadOnlyList<Candle>> GetRecentCandlesAsync(
            string symbol,
            string interval,
            int limit = 200)
        {
            var url = $"/fapi/v1/klines?symbol={symbol}&interval={interval}&limit={limit}";
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

        public async Task PlaceFuturesOrderAsync(
            string symbol,
            SignalType side,
            decimal quantity,
            decimal entryPrice,
            decimal stopLoss,
            decimal takeProfit,
            int leverage,
            bool marketOrder = true)
        {
            // PAPER MODE: chỉ log, không call API
            if (_config.PaperMode)
            {
                Console.WriteLine("[PAPER MODE] Giả lập gửi lệnh futures:");
                Console.WriteLine($" -> {side} {quantity} {symbol} @ {entryPrice}");
                Console.WriteLine($" -> SL (STOP_MARKET) : {stopLoss}");
                Console.WriteLine($" -> TP (TAKE_PROFIT_MARKET) : {takeProfit}");
                return;
            }

            // 1. set leverage
            await SetLeverageAsync(symbol, leverage);

            // 2. gửi lệnh vào (entry)
            string sideStr = side == SignalType.Long ? "BUY" : "SELL";
            string typeStr = marketOrder ? "MARKET" : "LIMIT";

            var entryParams = new Dictionary<string, string>
            {
                ["symbol"] = symbol,
                ["side"] = sideStr,
                ["type"] = typeStr,
                ["quantity"] = quantity.ToString(),
                ["recvWindow"] = "5000"
            };

            if (!marketOrder)
            {
                entryParams["timeInForce"] = "GTC";
                entryParams["price"] = entryPrice.ToString();
            }

            Console.WriteLine("=== SEND ENTRY ORDER ===");
            var entryResp = await SignedPostAsync("/fapi/v1/order", entryParams);
            Console.WriteLine("[ENTRY RESP] " + entryResp);

            // 3. Gửi SL (STOP_MARKET, reduceOnly)
            string closeSideStr = side == SignalType.Long ? "SELL" : "BUY";

            var slParams = new Dictionary<string, string>
            {
                ["symbol"] = symbol,
                ["side"] = closeSideStr,
                ["type"] = "STOP_MARKET",
                ["stopPrice"] = stopLoss.ToString(),
                ["closePosition"] = "true",      // đóng toàn bộ vị thế
                ["timeInForce"] = "GTC",
                ["recvWindow"] = "5000",
                // ["workingType"] = "MARK_PRICE" // nếu muốn dùng giá mark
            };

            Console.WriteLine("=== SEND STOP LOSS ===");
            var slResp = await SignedPostAsync("/fapi/v1/order", slParams);
            Console.WriteLine("[SL RESP] " + slResp);

            // 4. Gửi TP (TAKE_PROFIT_MARKET, reduceOnly)
            var tpParams = new Dictionary<string, string>
            {
                ["symbol"] = symbol,
                ["side"] = closeSideStr,
                ["type"] = "TAKE_PROFIT_MARKET",
                ["stopPrice"] = takeProfit.ToString(),
                ["closePosition"] = "true",
                ["timeInForce"] = "GTC",
                ["recvWindow"] = "5000",
                // ["workingType"] = "MARK_PRICE"
            };

            Console.WriteLine("=== SEND TAKE PROFIT ===");
            var tpResp = await SignedPostAsync("/fapi/v1/order", tpParams);
            Console.WriteLine("[TP RESP] " + tpResp);
        }

        public async Task<PositionInfo> GetPositionAsync(string symbol)
        {
            // /fapi/v2/positionRisk trả danh sách, mình lọc theo symbol
            var json = await SignedGetAsync("/fapi/v2/positionRisk", new Dictionary<string, string>
            {
                ["symbol"] = symbol
            });

            using var doc = JsonDocument.Parse(json);
            var arr = doc.RootElement;

            if (arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
            {
                var el = arr[0];
                return new PositionInfo
                {
                    Symbol = el.GetProperty("symbol").GetString() ?? symbol,
                    PositionAmt = decimal.Parse(el.GetProperty("positionAmt").GetString() ?? "0"),
                    EntryPrice = decimal.Parse(el.GetProperty("entryPrice").GetString() ?? "0"),
                    MarkPrice = decimal.Parse(el.GetProperty("markPrice").GetString() ?? "0"),
                    UpdateTime = DateTimeOffset.FromUnixTimeMilliseconds(
                        el.GetProperty("updateTime").GetInt64()).UtcDateTime
                };
            }

            // Không có position
            return new PositionInfo
            {
                Symbol = symbol,
                PositionAmt = 0,
                EntryPrice = 0,
                MarkPrice = 0,
                UpdateTime = DateTime.UtcNow
            };
        }

        public async Task<bool> HasOpenPositionOrOrderAsync(string symbol)
        {
            // Trong paper mode: coi như không có lệnh/vị thế, cho phép test tự do
            if (_config.PaperMode)
                return false;

            // 1) Check position hiện tại
            var posParams = new Dictionary<string, string>
            {
                ["symbol"] = symbol,
                ["recvWindow"] = "5000"
            };

            var posJson = await SignedGetAsync("/fapi/v2/positionRisk", posParams);

            try
            {
                using (var doc = JsonDocument.Parse(posJson))
                {
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
                                // Đang có vị thế (long hoặc short)
                                return true;
                            }
                        }
                    }
                }
            }
            catch
            {
                // nếu parse lỗi thì log thôi, không crash bot
                Console.WriteLine("[WARN] Cannot parse positionRisk response.");
            }

            // 2) Check open orders
            var orderParams = new Dictionary<string, string>
            {
                ["symbol"] = symbol,
                ["recvWindow"] = "5000"
            };

            var ordersJson = await SignedGetAsync("/fapi/v1/openOrders", orderParams);

            try
            {
                using (var doc = JsonDocument.Parse(ordersJson))
                {
                    // nếu có ít nhất 1 phần tử trong mảng -> đang có lệnh chờ
                    if (doc.RootElement.ValueKind == JsonValueKind.Array &&
                        doc.RootElement.GetArrayLength() > 0)
                    {
                        return true;
                    }
                }
            }
            catch
            {
                Console.WriteLine("[WARN] Cannot parse openOrders response.");
            }

            // Không có vị thế & không có lệnh chờ
            return false;
        }

        public async Task<UserTradeInfo?> GetLastUserTradeAsync(string symbol, DateTime since)
        {
            long fromMs = new DateTimeOffset(since).ToUnixTimeMilliseconds();

            var json = await SignedGetAsync("/fapi/v1/userTrades", new Dictionary<string, string>
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
                Time = DateTimeOffset.FromUnixTimeMilliseconds(
                    last.GetProperty("time").GetInt64()).UtcDateTime,
                Price = decimal.Parse(last.GetProperty("price").GetString() ?? "0"),
                Qty = decimal.Parse(last.GetProperty("qty").GetString() ?? "0"),
                IsBuyer = last.GetProperty("buyer").GetBoolean()
            };
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
                ["recvWindow"] = "5000"
            };

            var resp = await SignedPostAsync("/fapi/v1/leverage", param);
            Console.WriteLine("[LEVERAGE RESP] " + resp);
        }

        private async Task<string> SignedPostAsync(
            string path,
            IDictionary<string, string> parameters)
        {
            long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var sb = new StringBuilder();

            // build queryString
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
                Console.WriteLine($"[BINANCE ERROR] {resp.StatusCode}: {body}");
            }

            return body;
        }

        private async Task<string> SignedGetAsync(string path, IDictionary<string, string> parameters)
        {
            long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
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
    }
}