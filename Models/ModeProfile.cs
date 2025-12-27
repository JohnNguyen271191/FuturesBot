using static FuturesBot.Utils.EnumTypesHelper;

namespace FuturesBot.Models
{
    public sealed class ModeProfile
    {
        public TradeMode Mode { get; init; }

        // ==== R-based core (giữ lại) ====
        public decimal ProtectAtRR { get; init; }
        public decimal BreakEvenBufferR { get; init; }
        public decimal QuickTakeMinRR { get; init; }
        public decimal QuickTakeGoodRR { get; init; }
        public decimal DangerCutIfRRBelow { get; init; }
        public int TimeStopBars { get; init; }
        public decimal TimeStopMinRR { get; init; }

        // ==== ROI-based gates (NEW) ====
        public decimal MinProtectRoi { get; init; }
        public decimal MinQuickTakeRoi { get; init; }
        public decimal EarlyExitMinRoi { get; init; }
        public decimal MinBoundaryExitRoi { get; init; }

        public decimal MinDangerCutAbsLossRoi { get; init; }

        public int EarlyExitBars { get; init; }
        public decimal EarlyExitMinRR { get; init; }

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

                    // R-based
                    ProtectAtRR = 0.28m,
                    BreakEvenBufferR = 0.06m,
                    QuickTakeMinRR = 0.35m,
                    QuickTakeGoodRR = 0.60m,
                    DangerCutIfRRBelow = -0.25m,
                    TimeStopBars = 8,
                    TimeStopMinRR = 0.30m,

                    EarlyExitBars = 8,
                    EarlyExitMinRR = 0.10m,

                    // ROI-based
                    // NOTE: HẠ để phù hợp scalp major/fees -> lock sớm hơn, ít bỏ kèo
                    MinProtectRoi = 0.12m,         // cũ 0.20
                    MinQuickTakeRoi = 0.22m,       // cũ 0.30
                    EarlyExitMinRoi = 0.14m,       // cũ 0.18
                    MinBoundaryExitRoi = 0.10m,    // cũ 0.12
                    MinDangerCutAbsLossRoi = 0.15m,// giữ

                    SafetyTpRR = 1.30m,
                    EmaBreakTolerance = 0.0012m,
                    LimitTimeout = TimeSpan.FromMinutes(15),
                },

                TradeMode.Mode2_Continuation => new ModeProfile
                {
                    Mode = mode,

                    // R-based
                    ProtectAtRR = 0.28m,
                    BreakEvenBufferR = 0.05m,
                    QuickTakeMinRR = 0.40m,
                    QuickTakeGoodRR = 0.70m,
                    DangerCutIfRRBelow = -0.30m,
                    TimeStopBars = 8,
                    TimeStopMinRR = 0.30m,

                    EarlyExitBars = 8,
                    EarlyExitMinRR = 0.13m,

                    // ROI-based
                    MinProtectRoi = 0.22m,
                    MinQuickTakeRoi = 0.32m,
                    EarlyExitMinRoi = 0.20m,
                    MinBoundaryExitRoi = 0.14m,
                    MinDangerCutAbsLossRoi = 0.16m,

                    SafetyTpRR = 1.60m,
                    EmaBreakTolerance = 0.0010m,
                    LimitTimeout = TimeSpan.FromMinutes(10),
                },

                _ => new ModeProfile
                {
                    Mode = TradeMode.Trend,

                    // R-based
                    ProtectAtRR = 0.35m,
                    BreakEvenBufferR = 0.05m,
                    QuickTakeMinRR = 0.45m,
                    QuickTakeGoodRR = 0.75m,
                    DangerCutIfRRBelow = -0.35m,
                    TimeStopBars = 15,
                    TimeStopMinRR = 0.40m,

                    EarlyExitBars = 15,
                    EarlyExitMinRR = 0.20m,

                    // ROI-based
                    MinProtectRoi = 0.30m,
                    MinQuickTakeRoi = 0.50m,
                    EarlyExitMinRoi = 0.22m,
                    MinBoundaryExitRoi = 0.16m,
                    MinDangerCutAbsLossRoi = 0.20m,

                    SafetyTpRR = 2.0m,
                    EmaBreakTolerance = 0.001m,
                    LimitTimeout = TimeSpan.FromMinutes(40),
                }
            };
        }
    }
}