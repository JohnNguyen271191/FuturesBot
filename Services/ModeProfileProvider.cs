using FuturesBot.Config;
using FuturesBot.IServices;
using FuturesBot.Models;
using static FuturesBot.Utils.EnumTypesHelper;

namespace FuturesBot.Services
{
    /// <summary>
    /// Loads Futures exit/management profiles from appsettings.json (BotConfig.ModeProfiles).
    /// Falls back to ModeProfile.For(mode) defaults when not present.
    /// </summary>
    public sealed class ModeProfileProvider : IModeProfileProvider
    {
        private readonly Dictionary<TradeMode, ModeProfile> _map;

        public ModeProfileProvider(BotConfig config)
        {
            _map = new Dictionary<TradeMode, ModeProfile>();

            var fromCfg = config?.ModeProfiles ?? [];
            foreach (var p in fromCfg)
            {
                _map[p.Mode] = p;
            }
        }

        public ModeProfile Get(TradeMode mode)
        {
            if (_map.TryGetValue(mode, out var p) && p != null)
                return p;

            return ModeProfile.For(mode);
        }
    }
}
