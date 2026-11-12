namespace StrategicWork.Core;

// Labor and capacity use the same unit: one colonist available for the planning horizon.
// SharedSiteIds is the sorted, pipe-separated set of stable station IDs for a fully observed column.
// An omitted set means no additional cross-column station constraint is known.
public sealed record WorkColumn(string Id, string? StrategyId, int NaturalPriority = 0, double? Capacity = null,
    string? WorkFamily = null, double? OutputDemand = null, string? SharedSiteIds = null);
public sealed record PawnInput(string Id, IReadOnlyDictionary<string, WorkCell> Cells, double Labor = 1);
// Suitability is productive performance per unit of labor in this column.
// TrainingGain is useful growth per unit of labor; it must not include present-day throughput.
public sealed record WorkCell(int Current, bool Eligible, double Suitability, bool Locked = false,
    double TrainingGain = 0, bool PerformanceComparable = true);
public sealed record PlannedCell(string PawnId, string WorkId, int Before, int After, double EstimatedLabor,
    int? ReferencePriority = null, double ReferenceLabor = 0);
public enum ShortfallReason { None, NoMappedWork, NoEligiblePawn, Capacity, PriorityOrder }
public sealed record StrategyEstimate(string Id, double TargetLabor, double EstimatedLabor, ShortfallReason Reason,
    double ReferencePerformance = 0, double PlannedPerformance = 0, double ReferenceTraining = 0,
    double PlannedTraining = 0, bool PerformanceComparable = false);
public sealed record WorkPlan(
    IReadOnlyList<PlannedCell> Cells,
    IReadOnlyList<StrategyEstimate> Strategies,
    double UnassignedLabor,
    double UnmappedLabor, bool TrainingApplied = false);
