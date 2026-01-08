namespace FuturesBot.Config
{
    /// <summary>
    /// Spot strategy settings (long-only). Values are base-tuned for BaseTfMin then
    /// scaled inside the strategy for other TFs.
    /// </summary>
    public sealed class SpotStrategySettings
    {
        public int BaseTfMin { get; set; } = 5;

        // Indicators
        public int EmaFast { get; set; } = 34;
        public int EmaSlow { get; set; } = 89;
        public int EmaLong { get; set; } = 200;
        public int RsiPeriod { get; set; } = 14;
        public int AtrPeriod { get; set; } = 14;
        public int VolMaPeriod { get; set; } = 20;

        // Minimum bars
        public int MinBarsEntryBase { get; set; } = 200;
        public int MinBarsTrendBase { get; set; } = 120;

        // Global filters
        public decimal MaxDistanceFromEma34Base { get; set; } = 0.0035m;
        public decimal ImpulseBodyToRangeMaxBase { get; set; } = 0.75m;
        public decimal ImpulseRangeAtrMultBase { get; set; } = 1.2m;
        public decimal EntryVolMinFactorBase { get; set; } = 0.60m;
        public decimal BreakVolMinFactorBase { get; set; } = 0.90m;

        // Type A: Retest
        public int RetestLookbackBarsBase { get; set; } = 8;
        public decimal RetestTouchBandBase { get; set; } = 0.0010m;
        public decimal RetestReclaimBufBase { get; set; } = 0.0003m;
        public decimal RsiMinTypeABase { get; set; } = 45m;

        // Type B: Continuation
        public int BaseLookbackBarsBase { get; set; } = 6;
        public decimal BaseMaxRangeAtrBase { get; set; } = 0.85m;
        public decimal BaseMinLowAboveEma34Base { get; set; } = 0.0012m;
        public decimal RsiMinTypeBBase { get; set; } = 42m;

        // Type C: Break & Hold
        public int SwingHighLookbackBarsBase { get; set; } = 40;
        public int HoldConfirmBarsMaxBase { get; set; } = 3;
        public decimal BreakBufferBase { get; set; } = 0.0005m;
        public decimal HoldBelowBufferBase { get; set; } = 0.0005m;
        public decimal RsiMinTypeCBase { get; set; } = 48m;

        // Type D: Sweep-Reversal
        public int SweepLookbackBarsBase { get; set; } = 30;
        public decimal SweepBufferBase { get; set; } = 0.0009m;
        public decimal SweepReclaimBufferBase { get; set; } = 0.0003m;
        public decimal SweepBodyToRangeMinBase { get; set; } = 0.40m;
        public decimal SweepRsiMinBase { get; set; } = 40m;

        // Exit
        public decimal ExitRsiWeakBase { get; set; } = 44m;
        public decimal ExitEmaBreakTolBase { get; set; } = 0.0004m;
    }
}
