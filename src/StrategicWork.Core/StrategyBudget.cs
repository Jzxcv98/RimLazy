namespace StrategicWork.Core;

// Kept separate from the saved allocations so recalculating never changes the player's sliders.
public readonly struct StrategyBudget
{
    public decimal MinimumTotal { get; }
    public bool IsNormalized => MinimumTotal > 1m;
    public decimal RemainingShare => Math.Max(0m, 1m - MinimumTotal);
    public bool HasRelativeWeights => weightTotal > 0;
    private readonly double weightTotal;

    private StrategyBudget(decimal minimumTotal, double weightTotal)
    {
        MinimumTotal = minimumTotal;
        this.weightTotal = weightTotal;
    }

    public static StrategyBudget From(IReadOnlyList<StrategyAllocation> strategies)
    {
        decimal minimum = 0;
        double weight = 0;
        foreach (var strategy in strategies)
        {
            if (strategy.MinimumShare is < 0m or > 1m || strategy.RelativeWeight < 0m)
                throw new ArgumentException("Each minimum must be between 0% and 100%, and relative weights must be nonnegative.");
            minimum += strategy.MinimumShare;
            weight += (double)strategy.RelativeWeight;
        }
        return new(minimum, weight);
    }

    public decimal MinimumFor(StrategyAllocation strategy) =>
        IsNormalized ? strategy.MinimumShare / MinimumTotal : strategy.MinimumShare;

    public double TargetFor(StrategyAllocation strategy) =>
        (double)MinimumFor(strategy) + (HasRelativeWeights
            ? (double)RemainingShare * ((double)strategy.RelativeWeight / weightTotal) : 0);
}
