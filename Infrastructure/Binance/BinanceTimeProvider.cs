using System.Text.Json;
using FuturesBot.Services;

namespace FuturesBot.Infrastructure.Binance
{
    public interface IBinanceTimeProvider
    {
        Task<long> GetTimestampMsAsync();
    }

    /// <summary>
    /// Binance server time sync (best-effort). Caches offset for a short period to avoid spamming /time.
    /// </summary>
    public sealed class BinanceTimeProvider : IBinanceTimeProvider
    {
        private readonly HttpClient _http;
        private readonly string _timePath;
        private readonly SlackNotifierService _slack;

        private long _serverTimeOffsetMs;
        private DateTime _lastSyncUtc = DateTime.MinValue;

        public BinanceTimeProvider(HttpClient http, string timePath, SlackNotifierService slack)
        {
            _http = http;
            _timePath = timePath;
            _slack = slack;
        }

        public async Task<long> GetTimestampMsAsync()
        {
            await EnsureSyncedAsync();
            var local = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return local - _serverTimeOffsetMs;
        }

        private async Task EnsureSyncedAsync()
        {
            if (_lastSyncUtc != DateTime.MinValue && (DateTime.UtcNow - _lastSyncUtc) <= TimeSpan.FromMinutes(5))
                return;

            try
            {
                var resp = await _http.GetAsync(_timePath);
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync();

                using var doc = JsonDocument.Parse(json);
                var serverTime = doc.RootElement.GetProperty("serverTime").GetInt64();
                var local = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                _serverTimeOffsetMs = local - serverTime;
                _lastSyncUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                await _slack.SendAsync($"[TIME SYNC ERROR] {_timePath} => {ex.Message}");
                _lastSyncUtc = DateTime.UtcNow; // avoid tight loop
            }
        }
    }
}
