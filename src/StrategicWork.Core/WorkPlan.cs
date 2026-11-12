namespace StrategicWork.Core;

// Labor and capacity use the same unit: one colonist available for the planning horizon.
public sealed record WorkColumn(string Id, string? StrategyId, int NaturalPriority = 0, double? Capacity = null);
public sealed record PawnInput(string Id, IReadOnlyDictionary<string, WorkCell> Cells, double Labor = 1);
public sealed record WorkCell(int Current, bool Eligible, double Suitability, bool Locked = false);
public sealed record PlannedCell(string PawnId, string WorkId, int Before, int After, double EstimatedLabor);
public enum ShortfallReason { None, NoMappedWork, NoEligiblePawn, Capacity, PriorityOrder }
public sealed record StrategyEstimate(string Id, double TargetLabor, double EstimatedLabor, ShortfallReason Reason);
public sealed record WorkPlan(
    IReadOnlyList<PlannedCell> Cells,
    IReadOnlyList<StrategyEstimate> Strategies,
    double UnassignedLabor,
    double UnmappedLabor);
