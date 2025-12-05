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

                PositionInfo pos;

                // ===============================
                // 1) LẤY POSITION – SAFE MODE
                // ===============================
                try
                {
                    pos = await exchange.GetPositionAsync(symbol.Coin);
                }
                catch
                {
                    continue; // Không xử lý gì nếu lấy position lỗi
                }

                // ===============================
                // 2) VALIDATE POSITION
                // ===============================
                bool invalid =
                    pos.PositionAmt == 0 &&
                    pos.EntryPrice == 0 &&
                    pos.MarkPrice == 0;

                if (invalid)
                {
                    continue;
                }

                bool wasOpen = !state.LastPosition.IsFlat;
                bool nowFlat = pos.IsFlat;

                // ===============================
                // 3) XỬ LÝ ĐÓNG LỆNH THẬT
                // ===============================
                if (wasOpen && nowFlat)
                {
                    // DOUBLE CONFIRM – tránh close ảo
                    PositionInfo pos2;
                    try
                    {
                        await Task.Delay(80);
                        pos2 = await exchange.GetPositionAsync(symbol.Coin);
                    }
                    catch
                    {
                        continue;
                    }

                    if (!pos2.IsFlat)
                    {
                        continue;
                    }

                    var last = state.LastPosition;

                    var lastTrade = await exchange.GetLastUserTradeAsync(
                        symbol.Coin,
                        state.LastChangeTime.AddMinutes(-1));

                    decimal exitPrice = lastTrade?.Price ?? pos.MarkPrice;

                    var side = last.IsLong ? SignalType.Long : SignalType.Short;
                    var qty  = Math.Abs(last.PositionAmt);

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
                                    : (last.EntryPrice - exitPrice) * qty)
                                    + netPnlAsync.Commission
                    };

                    await pnl.RegisterClosedTradeAsync(closed);
                    await exchange.CancelAllOpenOrdersAsync(symbol.Coin);
                }

                // ===============================
                // 4) CẬP NHẬT TRẠNG THÁI
                // ===============================
                if (pos.PositionAmt != state.LastPosition.PositionAmt ||
                    pos.EntryPrice   != state.LastPosition.EntryPrice)
                {
                    state.LastPosition = pos;
                    state.LastChangeTime = DateTime.UtcNow;
                }
            }
        }
    }
}
