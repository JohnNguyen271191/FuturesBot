using FuturesBot.Config;
using System.Text;
using System.Text.Json;

namespace FuturesBot.Services
{
    public class SlackNotifierService(BotConfig config)
    {
        private readonly string? _webhookUrl = config.SlackWebhookUrl;
        private readonly HttpClient _http = new();

        public async Task SendAsync(string message)
        {
            // Không cấu hình Slack thì fallback về console
            if (string.IsNullOrWhiteSpace(_webhookUrl))
            {
                Console.WriteLine(message);
                return;
            }

            try
            {
                var payload = new
                {
                    text = message
                };

                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var resp = await _http.PostAsync(_webhookUrl, content);
                var body = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[SLACK ERROR] {resp.StatusCode}: {body}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[SLACK EXCEPTION] " + ex.Message);
            }
        }
    }
}
