using System.Security.Cryptography;
using System.Text;

namespace FuturesBot.Infrastructure.Binance
{
    public interface IBinanceSigner
    {
        string Sign(string queryString);
    }

    public sealed class BinanceSigner : IBinanceSigner
    {
        private readonly byte[] _secretBytes;

        public BinanceSigner(string apiSecret)
        {
            _secretBytes = Encoding.UTF8.GetBytes(apiSecret ?? string.Empty);
        }

        public string Sign(string queryString)
        {
            using var hmac = new HMACSHA256(_secretBytes);
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(queryString ?? string.Empty));
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }
    }
}
