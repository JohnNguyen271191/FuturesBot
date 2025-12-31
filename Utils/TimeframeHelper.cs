namespace FuturesBot.Utils
{
    public static class TimeframeHelper
    {
        public static int ParseToSeconds(string tf)
        {
            if (string.IsNullOrWhiteSpace(tf)) return 60;
            tf = tf.Trim().ToLowerInvariant();
            try
            {
                if (tf.EndsWith("m")) return int.Parse(tf[..^1]) * 60;
                if (tf.EndsWith("h")) return int.Parse(tf[..^1]) * 3600;
                if (tf.EndsWith("d")) return int.Parse(tf[..^1]) * 86400;
            }
            catch { }
            return 60;
        }
    }
}
