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
        // ROI = netPnlUsd / initialMarginUsd (có leverage)
        // Ví dụ ROI=0.30 => +30%
        public decimal MinProtectRoi { get; init; }
        public decimal MinQuickTakeRoi { get; init; }
        public decimal EarlyExitMinRoi { get; init; }
        public decimal MinBoundaryExitRoi { get; init; }

        // Danger cut: yêu cầu thua lỗ tối thiểu theo ROI để tránh cắt vì noise
        // absLossRoi = (-netPnlUsd)/initialMarginUsd
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
                    ProtectAtRR = 0.22m,
                    BreakEvenBufferR = 0.06m,
                    QuickTakeMinRR = 0.35m,
                    QuickTakeGoodRR = 0.60m,
                    DangerCutIfRRBelow = -0.25m,
                    TimeStopBars = 15,
                    TimeStopMinRR = 0.30m,

                    EarlyExitBars = 8,
                    EarlyExitMinRR = 0.10m,

                    // ROI-based (NEW) - mày có thể chỉnh đúng khẩu vị
                    MinProtectRoi = 0.20m,         // +20% margin => bắt đầu protect
                    MinQuickTakeRoi = 0.30m,       // +30% margin => quick take nếu weakening
                    EarlyExitMinRoi = 0.18m,       // +18% margin => early-exit nếu stall/weak
                    MinBoundaryExitRoi = 0.12m,    // +12% margin => exit nếu boundary break confirmed
                    MinDangerCutAbsLossRoi = 0.15m,// -15% margin => mới cho danger cut (tránh noise)

                    SafetyTpRR = 1.30m,
                    EmaBreakTolerance = 0.0012m,
                    LimitTimeout = TimeSpan.FromMinutes(20),
                },

                TradeMode.Mode2_Continuation => new ModeProfile
                {
                    Mode = mode,

                    // R-based
                    ProtectAtRR = 0.25m,
                    BreakEvenBufferR = 0.05m,
                    QuickTakeMinRR = 0.40m,
                    QuickTakeGoodRR = 0.70m,
                    DangerCutIfRRBelow = -0.30m,
                    TimeStopBars = 15,
                    TimeStopMinRR = 0.30m,

                    EarlyExitBars = 8,
                    EarlyExitMinRR = 0.13m,

                    // ROI-based (NEW)
                    MinProtectRoi = 0.22m,
                    MinQuickTakeRoi = 0.32m,
                    EarlyExitMinRoi = 0.20m,
                    MinBoundaryExitRoi = 0.14m,
                    MinDangerCutAbsLossRoi = 0.16m,

                    SafetyTpRR = 1.60m,
                    EmaBreakTolerance = 0.0010m,
                    LimitTimeout = TimeSpan.FromMinutes(15),
                },

                _ => new ModeProfile
                {
                    Mode = TradeMode.Trend,

                    // R-based
                    ProtectAtRR = 0.30m,
                    BreakEvenBufferR = 0.05m,
                    QuickTakeMinRR = 0.45m,
                    QuickTakeGoodRR = 0.75m,
                    DangerCutIfRRBelow = -0.35m,
                    TimeStopBars = 25,
                    TimeStopMinRR = 0.40m,

                    EarlyExitBars = 15,
                    EarlyExitMinRR = 0.20m,

                    // ROI-based (NEW)
                    MinProtectRoi = 0.25m,
                    MinQuickTakeRoi = 0.40m,
                    EarlyExitMinRoi = 0.22m,
                    MinBoundaryExitRoi = 0.16m,
                    MinDangerCutAbsLossRoi = 0.20m,

                    SafetyTpRR = 2.0m,
                    EmaBreakTolerance = 0.001m,
                    LimitTimeout = TimeSpan.FromMinutes(30),
                }
            };
        }
    }
}
