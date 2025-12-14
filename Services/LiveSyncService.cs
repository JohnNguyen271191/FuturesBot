using FuturesBot.Config;
using FuturesBot.IServices;
using FuturesBot.Models;
using static FuturesBot.Utils.EnumTypesHelper;

namespace FuturesBot.Services
{
    public class LiveSyncService(IExchangeClientService exchange, PnlReporterService pnl, OrderManagerService orderManagerService)
    {
        private class PositionState
        {
            public PositionInfo LastPosition { get; set; } = new();
            public DateTime LastChangeTime { get; set; } = DateTime.UtcNow;
        }

        private readonly Dictionary<string, PositionState> _states = [];

        public async Task SyncAsync(CoinInfo[] coinInfos)
        {
            foreach (var coinInfo in coinInfos)
            {
                if (!_states.TryGetValue(coinInfo.Symbol, out var state))
                {
                    state = new PositionState();
                    _states[coinInfo.Symbol] = state;
                }

                PositionInfo pos;

                // ===============================
                // 1) LẤY POSITION – SAFE MODE
                // ===============================
                try
                {
                    pos = await exchange.GetPositionAsync(coinInfo.Symbol);
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
                        pos2 = await exchange.GetPositionAsync(coinInfo.Symbol);
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
                        coinInfo.Symbol,
                        state.LastChangeTime.AddMinutes(-1));

                    decimal exitPrice = lastTrade?.Price ?? pos.MarkPrice;

                    var side = last.IsLong ? SignalType.Long : SignalType.Short;
                    var qty  = Math.Abs(last.PositionAmt);

                    var openTime = state.LastChangeTime;
                    var closeTime = DateTime.UtcNow;
                    var netPnlAsync = await exchange.GetNetPnlAsync(coinInfo.Symbol, openTime, closeTime);

                    var closed = new ClosedTrade
                    {
                        Symbol = coinInfo.Symbol,
                        Side = side,
                        Entry = last.EntryPrice,
                        Exit = exitPrice,
                        Quantity = qty,
                        OpenTime = openTime,
                        CloseTime = closeTime,
                        PnlUSDT = netPnlAsync.Net
                    };

                    await pnl.RegisterClosedTradeAsync(closed);
                    await exchange.CancelAllOpenOrdersAsync(coinInfo.Symbol);
                    await orderManagerService.ClearMonitoringTrigger(coinInfo.Symbol);
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
