namespace FuturesBot.Config
{
    /// <summary>
    /// Futures strategy settings for FuturesTrendStrategy.
    /// Defaults match previous hardcoded constants so existing behavior is preserved.
    /// </summary>
    public sealed class FuturesStrategySettings
    {
        public int MinBars { get; set; } = 120;

        public decimal EmaRetestBand { get; set; } = 0.002m;
        public decimal StopBufferPercent { get; set; } = 0.005m;

        public decimal RiskReward { get; set; } = 1.5m;
        public decimal RiskRewardSideway { get; set; } = 1m;
        public decimal RiskRewardMajor { get; set; } = 2.0m;
        public decimal RiskRewardSidewayMajor { get; set; } = 1.0m;

        public decimal RsiBullThreshold { get; set; } = 55m;
        public decimal RsiBearThreshold { get; set; } = 45m;
        public decimal ExtremeRsiHigh { get; set; } = 75m;
        public decimal ExtremeRsiLow { get; set; } = 30m;
        public decimal ExtremeEmaBoost { get; set; } = 0.01m;

        public decimal TrendRetestRsiMaxForLong { get; set; } = 68m;
        public decimal TrendRetestRsiMinForShort { get; set; } = 32m;

        public bool EnableAutoEntryOffset { get; set; } = true;
        public decimal AutoGapSmall { get; set; } = 0.0015m;
        public decimal AutoGapBig { get; set; } = 0.0030m;
        public decimal AutoTrendOffset_SmallGap { get; set; } = 0.0020m;
        public decimal AutoTrendOffset_MidGap { get; set; } = 0.0015m;
        public decimal AutoTrendOffset_BigGap { get; set; } = 0.0010m;
        public decimal AutoScalpOffset_SmallGap { get; set; } = 0.0012m;
        public decimal AutoScalpOffset_MidGap { get; set; } = 0.0010m;
        public decimal AutoScalpOffset_BigGap { get; set; } = 0.0008m;
        public decimal AutoOffsetMin { get; set; } = 0.0008m;
        public decimal AutoOffsetMax { get; set; } = 0.0022m;

        public decimal AnchorSlBufferPercent { get; set; } = 0.0015m;

        public bool UseSwingForTrendStop { get; set; } = true;
        public int SwingLookback { get; set; } = 5;
        public decimal SwingStopExtraBufferPercent { get; set; } = 0.0010m;

        public int ClimaxLookback { get; set; } = 20;
        public decimal ClimaxBodyMultiplier { get; set; } = 1.8m;
        public decimal ClimaxVolumeMultiplier { get; set; } = 1.5m;

        public decimal OverextendedFromEmaPercent { get; set; } = 0.01m;
    }
}
