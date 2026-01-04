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

        // =====================================================================
        // NEW: Dynamic Fee Gates (thay cho hardcode trong OMS)
        // =====================================================================
        public decimal ProtectMinNetProfitVsFeeMult { get; init; }
        public decimal QuickMinNetProfitVsFeeMult { get; init; }
        public decimal EarlyExitMinNetProfitVsFeeMult { get; init; }
        public decimal BoundaryExitMinNetProfitVsFeeMult { get; init; }

        // NEW: Fee-safe breakeven buffer (price buffer = feeUsd*mult/qty)
        public decimal FeeBreakevenBufferMult { get; init; }

        // NEW: Trailing safety
        public decimal MinSlDistanceAtrFrac { get; init; }
        public int TrailingMinUpdateIntervalSec { get; init; }
        public decimal TrailingMinStepAtrFrac { get; init; }

        // =====================================================================
        // NEW: AllowTrailing gate (để tránh vừa lời nhỏ đã kéo ATR rồi bị cắn)
        // =====================================================================
        public decimal MinTrailStartRoi { get; init; }
        public decimal MinTrailStartRR { get; init; }
        public decimal TrailMinNetProfitVsFeeMult { get; init; }

        // =====================================================================
        // NEW: “Chốt luôn nếu không ổn” gate (dynamic)
        // =====================================================================
        public decimal QuickTakeNotOkMinRoi { get; init; }
        public decimal QuickTakeNotOkMinRR { get; init; }
        public decimal QuickTakeNotOkMinNetProfitVsFeeMult { get; init; }

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
                    MinProtectRoi = 0.12m,
                    MinQuickTakeRoi = 0.22m,
                    EarlyExitMinRoi = 0.14m,
                    MinBoundaryExitRoi = 0.10m,
                    MinDangerCutAbsLossRoi = 0.15m,

                    SafetyTpRR = 1.30m,
                    EmaBreakTolerance = 0.0012m,
                    LimitTimeout = TimeSpan.FromMinutes(15),

                    // ===== Fee gates (scalp: cần fee-safe mạnh hơn) =====
                    ProtectMinNetProfitVsFeeMult = 3.0m,
                    QuickMinNetProfitVsFeeMult = 3.0m,
                    EarlyExitMinNetProfitVsFeeMult = 1.2m,
                    BoundaryExitMinNetProfitVsFeeMult = 3.0m,

                    FeeBreakevenBufferMult = 1.25m,

                    // ===== Trailing safety =====
                    MinSlDistanceAtrFrac = 0.35m,
                    TrailingMinUpdateIntervalSec = 45,
                    TrailingMinStepAtrFrac = 0.15m,

                    // ===== AllowTrailing gate (scalp: cho BE sớm, ATR lock trễ hơn) =====
                    MinTrailStartRoi = 0.18m,
                    MinTrailStartRR = 0.30m,
                    TrailMinNetProfitVsFeeMult = 3.0m,

                    // ===== QuickTakeIfNotOk (scalp: chốt nhanh khi yếu) =====
                    QuickTakeNotOkMinRoi = 0.16m,
                    QuickTakeNotOkMinRR = 0.28m,
                    QuickTakeNotOkMinNetProfitVsFeeMult = 2.2m,
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

                    // Fee gates
                    ProtectMinNetProfitVsFeeMult = 2.6m,
                    QuickMinNetProfitVsFeeMult = 2.8m,
                    EarlyExitMinNetProfitVsFeeMult = 1.2m,
                    BoundaryExitMinNetProfitVsFeeMult = 1.8m,

                    FeeBreakevenBufferMult = 1.20m,

                    MinSlDistanceAtrFrac = 0.35m,
                    TrailingMinUpdateIntervalSec = 45,
                    TrailingMinStepAtrFrac = 0.15m,

                    MinTrailStartRoi = 0.26m,
                    MinTrailStartRR = 0.35m,
                    TrailMinNetProfitVsFeeMult = 2.4m,

                    QuickTakeNotOkMinRoi = 0.22m,
                    QuickTakeNotOkMinRR = 0.34m,
                    QuickTakeNotOkMinNetProfitVsFeeMult = 2.0m,
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

                    // Fee gates (trend: ít “micro”, vẫn fee-safe)
                    ProtectMinNetProfitVsFeeMult = 2.4m,
                    QuickMinNetProfitVsFeeMult = 2.4m,
                    EarlyExitMinNetProfitVsFeeMult = 1.2m,
                    BoundaryExitMinNetProfitVsFeeMult = 1.8m,

                    FeeBreakevenBufferMult = 1.15m,

                    MinSlDistanceAtrFrac = 0.35m,
                    TrailingMinUpdateIntervalSec = 60,
                    TrailingMinStepAtrFrac = 0.15m,

                    MinTrailStartRoi = 0.36m,
                    MinTrailStartRR = 0.45m,
                    TrailMinNetProfitVsFeeMult = 2.0m,

                    QuickTakeNotOkMinRoi = 0.32m,
                    QuickTakeNotOkMinRR = 0.42m,
                    QuickTakeNotOkMinNetProfitVsFeeMult = 1.8m,
                }
            };
        }
    }
}