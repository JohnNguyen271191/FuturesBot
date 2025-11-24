using System.Security.Cryptography;
using System.Text;

namespace FuturesBot.Utils
{
    public static class BinanceSignatureHelper
    {
        public static string Sign(string queryString, string secret)
        {
            var keyBytes = Encoding.UTF8.GetBytes(secret);
            var queryBytes = Encoding.UTF8.GetBytes(queryString);

            using var hmac = new HMACSHA256(keyBytes);
            var hash = hmac.ComputeHash(queryBytes);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }
    }
}
