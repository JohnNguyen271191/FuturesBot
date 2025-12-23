using static FuturesBot.Utils.EnumTypesHelper;

namespace FuturesBot.Models
{
    public sealed class ModeProfile
    {
        public TradeMode Mode { get; init; }

        public decimal ProtectAtRR { get; init; }
        public decimal BreakEvenBufferR { get; init; }
        public decimal QuickTakeMinRR { get; init; }
        public decimal QuickTakeGoodRR { get; init; }
        public decimal DangerCutIfRRBelow { get; init; }
        public int TimeStopBars { get; init; }
        public decimal TimeStopMinRR { get; init; }

        public decimal MinProtectProfitUsd { get; init; }
        public decimal MinQuickTakeProfitUsd { get; init; }
        public decimal MinDangerCutAbsLossUsd { get; init; }

        public int EarlyExitBars { get; init; }
        public decimal EarlyExitMinRR { get; init; }
        public decimal EarlyExitMinProfitUsd { get; init; }

        public decimal SafetyTpRR { get; init; }
        public decimal EmaBreakTolerance { get; init; }
        public TimeSpan LimitTimeout { get; init; }

        public string Tag => Mode.ToString();

        public static ModeProfile For(TradeMode mode)
        {
            return mode switch
            {
                TradeMode.Scalp => new ModeProfile
                {
                    Mode = mode,
                    ProtectAtRR = 0.22m,
                    BreakEvenBufferR = 0.06m,
                    QuickTakeMinRR = 0.35m,
                    QuickTakeGoodRR = 0.60m,
                    DangerCutIfRRBelow = -0.25m,
                    TimeStopBars = 15,
                    TimeStopMinRR = 0.3m,
                    SafetyTpRR = 1.30m,
                    EmaBreakTolerance = 0.0012m,
                    LimitTimeout = TimeSpan.FromMinutes(20),

                    MinProtectProfitUsd = 0.10m,
                    MinQuickTakeProfitUsd = 0.15m,
                    MinDangerCutAbsLossUsd = 0.12m,

                    EarlyExitBars = 8,
                    EarlyExitMinRR = 0.1m,
                    EarlyExitMinProfitUsd = 0.12m,
                },

                TradeMode.Mode2_Continuation => new ModeProfile
                {
                    Mode = mode,
                    ProtectAtRR = 0.25m,
                    BreakEvenBufferR = 0.05m,
                    QuickTakeMinRR = 0.40m,
                    QuickTakeGoodRR = 0.70m,
                    DangerCutIfRRBelow = -0.30m,
                    TimeStopBars = 15,
                    TimeStopMinRR = 0.3m,
                    SafetyTpRR = 1.60m,
                    EmaBreakTolerance = 0.0010m,
                    LimitTimeout = TimeSpan.FromMinutes(15),

                    MinProtectProfitUsd = 0.15m,
                    MinQuickTakeProfitUsd = 0.25m,
                    MinDangerCutAbsLossUsd = 0.18m,

                    EarlyExitBars = 8,
                    EarlyExitMinRR = 0.13m,
                    EarlyExitMinProfitUsd = 0.18m,
                },

                _ => new ModeProfile
                {
                    Mode = TradeMode.Trend,
                    ProtectAtRR = 0.30m,
                    BreakEvenBufferR = 0.05m,
                    QuickTakeMinRR = 0.45m,
                    QuickTakeGoodRR = 0.75m,
                    DangerCutIfRRBelow = -0.35m,
                    TimeStopBars = 25,
                    TimeStopMinRR = 0.4m,
                    SafetyTpRR = 2.0m,
                    EmaBreakTolerance = 0.001m,
                    LimitTimeout = TimeSpan.FromMinutes(30),

                    MinProtectProfitUsd = 0.25m,
                    MinQuickTakeProfitUsd = 0.40m,
                    MinDangerCutAbsLossUsd = 0.30m,

                    EarlyExitBars = 15,
                    EarlyExitMinRR = 0.2m,
                    EarlyExitMinProfitUsd = 0.25m,
                }
            };
        }
    }
}
