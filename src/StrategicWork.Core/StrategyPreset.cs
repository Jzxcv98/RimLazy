namespace StrategicWork.Core;

// Snapshot contract only. Fractions describe available labor time within the planning horizon.
public sealed record StrategyPreset(
    int SchemaVersion,
    string PresetId,
    IReadOnlyList<StrategyAllocation> Strategies,
    UnweightedRemainder UnweightedRemainder,
    decimal TrainingLossLimit = 0m);

public sealed record StrategyAllocation(string Id, decimal MinimumShare, decimal RelativeWeight);

public enum UnweightedRemainder { PreserveExisting, LeaveUnassigned }
