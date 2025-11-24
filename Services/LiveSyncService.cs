using FuturesBot.Config;
using FuturesBot.IServices;
using FuturesBot.Models;
using static FuturesBot.Utils.EnumTypesHelper;

namespace FuturesBot.Services
{
    public class LiveSyncService(IExchangeClientService exchange, PnlReporterService pnl)
    {
        private readonly IExchangeClientService _exchange = exchange;
        private readonly PnlReporterService _pnl = pnl;

        private class PositionState
        {
            public PositionInfo LastPosition { get; set; } = new();
            public DateTime LastChangeTime { get; set; } = DateTime.UtcNow;
        }

        private readonly Dictionary<string, PositionState> _states = [];

        public async Task SyncAsync(Symbol[] symbols)
        {
            foreach (var symbol in symbols)
            {
                if (!_states.TryGetValue(symbol.Coin, out var state))
                {
                    state = new PositionState();
                    _states[symbol.Coin] = state;
                }

                var pos = await _exchange.GetPositionAsync(symbol.Coin);

                // TH1: trước có vị thế, giờ hết -> vừa đóng
                bool wasOpen = !state.LastPosition.IsFlat;
                bool nowFlat = pos.IsFlat;

                if (wasOpen && nowFlat)
                {
                    var last = state.LastPosition;

                    // Lấy giá trade cuối cùng kể từ lúc change trước
                    var lastTrade = await _exchange.GetLastUserTradeAsync(
                        symbol.Coin,
                        state.LastChangeTime.AddMinutes(-1)); // buffer

                    decimal exitPrice = lastTrade?.Price ?? pos.MarkPrice;

                    var side = last.IsLong ? SignalType.Long : SignalType.Short;
                    var qty = Math.Abs(last.PositionAmt);

                    var closed = new ClosedTrade
                    {
                        Symbol = symbol.Coin,
                        Side = side,
                        Entry = last.EntryPrice,
                        Exit = exitPrice,
                        Quantity = qty,
                        OpenTime = state.LastChangeTime,
                        CloseTime = DateTime.UtcNow
                    };

                    await _pnl.RegisterClosedTradeAsync(closed);
                }

                // Nếu khác size -> update state
                if (pos.PositionAmt != state.LastPosition.PositionAmt ||
                    pos.EntryPrice != state.LastPosition.EntryPrice)
                {
                    state.LastPosition = pos;
                    state.LastChangeTime = DateTime.UtcNow;
                }
            }
        }
    }
}
