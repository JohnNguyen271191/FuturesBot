namespace FuturesBot.Utils
{
    public static class EnumTypesHelper
    {
        public enum SignalType
        {
            None,
            // SPOT: Open position (BUY). Futures uses Long.
            Open,
            Long,
            Short,
            Close,
            Info,
            CloseShort,
            CloseLong,

            // ALERT/ANTICIPATION
            AnticipationLongAlert,
            AnticipationShortAlert,
            CapitulationLongAlert
        }

        public enum TradeMode
        {
            None = 0,

            // trend chính
            Trend = 1,

            // scalp sideway
            Scalp = 2,

            // market on strong reject
            Mode1_StrongReject = 3,

            // pullback -> continuation
            Mode2_Continuation = 4
        }
    }
}
