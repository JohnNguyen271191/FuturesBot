using FuturesBot.Config;
using FuturesBot.IServices;
using FuturesBot.Models;
using static FuturesBot.Utils.EnumTypesHelper;

namespace FuturesBot.Services
{
    public class LiveSyncService(IExchangeClientService exchange, PnlReporterService pnl)
    {
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

                var pos = await exchange.GetPositionAsync(symbol.Coin);

                bool wasOpen = !state.LastPosition.IsFlat;
                bool nowFlat = pos.IsFlat;

                if (wasOpen && nowFlat)
                {
                    var last = state.LastPosition;

                    var lastTrade = await exchange.GetLastUserTradeAsync(
                        symbol.Coin,
                        state.LastChangeTime.AddMinutes(-1));

                    decimal exitPrice = lastTrade?.Price ?? pos.MarkPrice;

                    var side = last.IsLong ? SignalType.Long : SignalType.Short;
                    var qty = Math.Abs(last.PositionAmt);

                    var netPnlAsync = await exchange.GetNetPnlAsync(symbol.Coin);

                    var closed = new ClosedTrade
                    {
                        Symbol = symbol.Coin,
                        Side = side,
                        Entry = last.EntryPrice,
                        Exit = exitPrice,
                        Quantity = qty,
                        OpenTime = state.LastChangeTime,
                        CloseTime = DateTime.UtcNow,
                        PnlUSDT = (side == SignalType.Long
                                    ? (exitPrice - last.EntryPrice) * qty
                                    : (last.EntryPrice - exitPrice) * qty) + netPnlAsync.Commission
                    };

                    await pnl.RegisterClosedTradeAsync(closed);
                    await exchange.CancelAllOpenOrdersAsync(symbol.Coin);
                }

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
