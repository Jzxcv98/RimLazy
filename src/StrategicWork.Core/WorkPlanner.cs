namespace StrategicWork.Core;

public static partial class WorkPlanner
{
    internal const double Epsilon = 1e-8;

    public static WorkPlan Plan(IReadOnlyList<PawnInput> pawns, IReadOnlyList<WorkColumn> columns,
        StrategyPreset preset, int maxPriority, bool rush = false)
    {
        if (maxPriority < 1) throw new ArgumentException("At least one enabled priority is required.");
        if (preset.TrainingLossLimit is < 0m or > 1m)
            throw new ArgumentOutOfRangeException(nameof(preset), "Training loss limit must be between zero and one.");
        var people = pawns.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
        var works = columns.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
        var strategies = preset.Strategies.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
        Validate(people, works, strategies);
        var total = people.Sum(x => x.Labor);
        var budget = StrategyBudget.From(strategies);
        var minimums = strategies.Select(s => (double)budget.MinimumFor(s) * total).ToArray();
        var targets = strategies.Select(s => budget.TargetFor(s) * total).ToArray();
        var groupIds = strategies.Select((x, i) => (x.Id, i)).ToDictionary(x => x.Id, x => x.i, StringComparer.Ordinal);
        int[] groups = works.Select(w => w.StrategyId is not null && groupIds.TryGetValue(w.StrategyId, out int g) ? g : -1).ToArray();
        var cells = new WorkCell?[people.Length, works.Length];
        var potential = new double[works.Length];
        foreach (var (p, i) in people.Select((p, i) => (p, i)))
        {
            for (int w = 0; w < works.Length; w++)
            {
                if (!p.Cells.TryGetValue(works[w].Id, out var cell)) continue;
                if (!double.IsFinite(cell.Suitability)) throw new ArgumentException("Suitability must be finite.");
                if (!double.IsFinite(cell.TrainingGain) || cell.TrainingGain < 0)
                    throw new ArgumentException("Training potential must be finite and nonnegative.");
                cells[i, w] = cell;
                if (CanWork(cell)) potential[w] += p.Labor;
            }
        }
        // An unmapped, preserved column (e.g. firefighting) has no assumed routine workload.
        var limits = works.Select((w, i) => w.OutputDemand == 0 ? 0 :
            Math.Min(potential[i], w.Capacity ?? (groups[i] >= 0 ? total : 0))).ToArray();
        var model = new Model(people, works, cells, groups, limits, targets, minimums, maxPriority);
        bool preserveRemainder = !budget.HasRelativeWeights && total * (double)budget.RemainingShare > Epsilon &&
            preset.UnweightedRemainder == UnweightedRemainder.PreserveExisting;
        var reference = model.SolveTable(preserveRemainder, 0);
        var comparison = new TrainingComparison(people, works, cells, groups, targets, reference,
            rush ? 0 : (double)preset.TrainingLossLimit);
        var chosen = comparison.Select(model, preserveRemainder);
        var priorities = chosen.Priorities;
        var estimate = chosen.Forecast;
        var output = new List<PlannedCell>();
        for (int i = 0; i < people.Length; i++)
        for (int w = 0; w < works.Length; w++)
            if (cells[i, w] is { } cell)
                output.Add(new(people[i].Id, works[w].Id, cell.Current, priorities[i, w], estimate.Hours[i, w],
                    reference.Priorities[i, w], reference.Forecast.Hours[i, w]));
        var reports = strategies.Select((s, g) => new StrategyEstimate(s.Id, targets[g], estimate.Shares[g],
            model.Explain(g, potential, estimate.Shares[g]), comparison.Comparable[g] ? 100 : 0,
            comparison.Comparable[g] ? comparison.BestMetrics.Performance[g] : 0, comparison.ReferenceMetrics.Training[g],
            comparison.BestMetrics.Training[g], comparison.Comparable[g])).ToArray();
        return new(output, reports, Math.Max(0, total - estimate.Shares.Sum() - estimate.Unmapped), estimate.Unmapped,
            chosen != reference);
    }

    private static void Validate(PawnInput[] people, WorkColumn[] works, StrategyAllocation[] strategies)
    {
        static bool InvalidIds(IEnumerable<string> ids)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            return ids.Any(id => string.IsNullOrWhiteSpace(id) || !seen.Add(id));
        }
        if (InvalidIds(people.Select(x => x.Id)) || InvalidIds(works.Select(x => x.Id)) ||
            InvalidIds(strategies.Select(x => x.Id)))
            throw new ArgumentException("Pawn, work and strategy identifiers must be unique within each list.");
        if (people.Any(x => !double.IsFinite(x.Labor) || x.Labor < 0) || !double.IsFinite(people.Sum(x => x.Labor)) ||
            works.Any(x => x.Capacity is double cap && (!double.IsFinite(cap) || cap < 0) ||
                           x.OutputDemand is double demand && (!double.IsFinite(demand) || demand < 0)))
            throw new ArgumentException("Labor and capacity must be finite and nonnegative.");
    }

    private static bool CanWork(WorkCell? c) => c is { Eligible: true, Current: >= 0 } && (!c.Locked || c.Current > 0);

    private sealed record Forecast(double[,] Hours, double[] Shares, double Unmapped);
    private sealed record Table(int[,] Priorities, Forecast Forecast);

    private sealed class Model(PawnInput[] people, WorkColumn[] works, WorkCell?[,] cells, int[] groups,
        double[] limits, double[] targets, double[] minimums, int maxPriority)
    {
        private readonly SharedStations stations = new(works);
        private double[]? allocationLimits;

        private double[] InitialLimits()
        {
            if (!stations.Shared) return limits;
            var requested = new double[works.Length];
            for (int w = 0; w < works.Length; w++)
            {
                if (groups[w] < 0) continue;
                requested[w] = Math.Min(targets[groups[w]], limits[w]);
                if (works[w].OutputDemand is not double amount || amount <= 0) continue;
                double slowest = double.PositiveInfinity;
                for (int p = 0; p < people.Length; p++)
                    if (CanWork(cells[p, w]) && cells[p, w]!.Suitability > 0)
                        slowest = Math.Min(slowest, cells[p, w]!.Suitability);
                if (double.IsFinite(slowest)) requested[w] = Math.Min(requested[w], amount / slowest);
            }
            var granted = new double[works.Length];
            stations.Grant(requested, stations.Open(), granted);
            return granted;
        }

        private bool Writable(int p, int w) => people[p].Labor > Epsilon && groups[w] >= 0 &&
            cells[p, w] is { Eligible: true, Locked: false, Current: >= 0 } c && c.Current <= maxPriority;

        public bool CanAdjustForTraining(int p, int w) => Writable(p, w) && limits[w] > Epsilon &&
            cells[p, w]!.PerformanceComparable;

        public Table SolveTable(bool preserveRemainder, double trainingWeight)
        {
            var allocation = Allocate(trainingWeight);
            var priorities = Rank(allocation, preserveRemainder);
            var forecast = Estimate(priorities);
            ImproveRounding(allocation, priorities, ref forecast);
            return new(priorities, forecast);
        }

        private double[,] Allocate(double trainingWeight)
        {
            var caps = allocationLimits ??= InitialLimits();
            int workStart = people.Length, groupStart = workStart + works.Length;
            int source = groupStart + targets.Length, sink = source + 1;
            var flow = new LaborFlow(sink + 1);
            var assignments = new List<(int Pawn, int Work, LaborFlow.Edge Edge)>();
            var scores = new List<double>();
            double maxTraining = Epsilon;
            for (int p = 0; p < people.Length; p++)
            for (int w = 0; w < works.Length; w++)
                if (groups[w] >= 0 && CanWork(cells[p, w]))
                {
                    var cell = cells[p, w]!;
                    scores.Add(cell.Suitability);
                    maxTraining = Math.Max(maxTraining, cell.TrainingGain);
                }
            double scale = scores.Count == 0 ? 1 : Math.Max(1, Math.Max(Math.Abs(scores.Min()), Math.Abs(scores.Max())));
            for (int p = 0; p < people.Length; p++)
            {
                flow.Add(source, p, people[p].Labor, 0);
                for (int w = 0; w < works.Length; w++)
                {
                    if (groups[w] < 0 || targets[groups[w]] <= Epsilon || caps[w] <= Epsilon || !CanWork(cells[p, w])) continue;
                    var cell = cells[p, w]!;
                    double value = (1 - trainingWeight) * (cell.Suitability / scale) +
                                   trainingWeight * (cell.TrainingGain / maxTraining);
                    int cost = (int)Math.Round(1000 * (1 - value)) + (cell.Current == 0 ? 1 : 0);
                    assignments.Add((p, w, flow.Add(p, workStart + w, people[p].Labor, cost)));
                }
            }
            for (int g = 0; g < targets.Length; g++)
            {
                // Splitting hauling into delivery, loading and transport does not create extra labor demand.
                // Give each family coverage first, then allocate that coverage among its actual columns.
                var families = Enumerable.Range(0, works.Length).Where(w => groups[w] == g && caps[w] > Epsilon)
                    .GroupBy(w => works[w].WorkFamily ?? works[w].Id, StringComparer.Ordinal)
                    .Select(family => family.ToArray()).ToArray();
                var familyShares = CappedShares(families.Select(f => f.Sum(w => caps[w])).ToArray(), targets[g]);
                for (int f = 0; f < families.Length; f++)
                {
                    var family = families[f];
                    var shares = CappedShares(family.Select(w => caps[w]).ToArray(), familyShares[f]);
                    for (int j = 0; j < family.Length; j++)
                    {
                        int w = family[j];
                        flow.Add(workStart + w, groupStart + g, shares[j], 0);
                        flow.Add(workStart + w, groupStart + g, caps[w] - shares[j], 10_000);
                    }
                }
                flow.Add(groupStart + g, sink, minimums[g], 0);
                flow.Add(groupStart + g, sink, Math.Max(0, targets[g] - minimums[g]), 1_000_000);
            }
            flow.Solve(source, sink);
            var result = new double[people.Length, works.Length];
            foreach (var (p, w, edge) in assignments) result[p, w] = edge.Reverse.Remaining;
            return result;
        }

        private static double[] CappedShares(double[] capacities, double budget)
        {
            var shares = new double[capacities.Length];
            int remaining = capacities.Length;
            foreach (int i in Enumerable.Range(0, capacities.Length).OrderBy(i => capacities[i]))
            {
                shares[i] = Math.Min(capacities[i], budget / remaining);
                budget = Math.Max(0, budget - shares[i]);
                remaining--;
            }
            return shares;
        }

        public int[,] Rank(double[,] allocation, bool preserveRemainder)
        {
            var result = new int[people.Length, works.Length];
            var claimed = new double[works.Length];
            for (int p = 0; p < people.Length; p++)
            for (int w = 0; w < works.Length; w++)
                claimed[w] += allocation[p, w];

            for (int p = 0; p < people.Length; p++)
            {
                var ranked = new List<int>();
                bool assigned = Enumerable.Range(0, works.Length).Any(w => allocation[p, w] > Epsilon);
                for (int w = 0; w < works.Length; w++)
                {
                    var cell = cells[p, w];
                    if (cell == null) continue;
                    result[p, w] = cell.Current;
                    if (!Writable(p, w)) continue;
                    if (preserveRemainder && (!assigned || targets[groups[w]] <= Epsilon)) continue;
                    result[p, w] = 0;
                    if (!assigned || targets[groups[w]] <= Epsilon || limits[w] <= Epsilon) continue;
                    // Finite work already covered by its assigned specialists need not recruit other workers.
                    if (allocation[p, w] <= Epsilon && works[w].Capacity.HasValue && claimed[w] + Epsilon >= limits[w]) continue;
                    ranked.Add(w);
                }
                var byRole = ranked.OrderByDescending(w => Math.Round(allocation[p, w], 8))
                    .ThenByDescending(w => cells[p, w]!.Suitability)
                    .ThenBy(w => cells[p, w]!.Current > 0 ? cells[p, w]!.Current : int.MaxValue)
                    .ThenByDescending(w => works[w].NaturalPriority).ThenBy(w => w).ToArray();
                for (int i = 0; i < byRole.Length; i++)
                {
                    // Reserve the best tier for the lead job. With many modded columns an
                    // evenly rounded scale would put several fallback jobs in that tier.
                    int priority = 1;
                    if (i > 0 && maxPriority > 1)
                        priority = byRole.Length == 2 ? maxPriority :
                            2 + (int)Math.Round((i - 1) * (maxPriority - 2d) / (byRole.Length - 2), MidpointRounding.AwayFromZero);
                    result[p, byRole[i]] = priority;
                }
            }
            RetainStandbyForFutureWork(result, allocation);
            return result;
        }

        private void RetainStandbyForFutureWork(int[,] priorities, double[,] allocation)
        {
            // A research project or a new bill can appear between manual recalculations.
            // Staff one reserve at the lowest tier. Zero current capacity still excludes
            // the column from both today's labor budget and the estimated outcome.
            var spare = people.Select(p => p.Labor).ToArray();
            for (int p = 0; p < people.Length; p++)
            for (int w = 0; w < works.Length; w++)
                spare[p] -= allocation[p, w];
            for (int w = 0; w < works.Length; w++)
            {
                if ((works[w].Capacity != 0 && works[w].OutputDemand != 0) || groups[w] < 0 || targets[groups[w]] <= Epsilon) continue;
                var available = Enumerable.Range(0, people.Length)
                    .Where(p => people[p].Labor > Epsilon && CanWork(cells[p, w])).ToArray();
                if (available.Any(p => priorities[p, w] > 0)) continue;
                int standby = available.Where(p => Writable(p, w))
                    .OrderByDescending(p => spare[p] > Epsilon)
                    .ThenByDescending(p => cells[p, w]!.Suitability)
                    .ThenByDescending(p => cells[p, w]!.Current > 0)
                    .FirstOrDefault(-1);
                if (standby >= 0) priorities[standby, w] = maxPriority;
            }
        }

        public Forecast Estimate(int[,] priorities)
        {
            var hours = new double[people.Length, works.Length];
            var shares = new double[targets.Length];
            var stock = (double[])limits.Clone();
            var outputLeft = works.Select(w => w.OutputDemand ?? double.PositiveInfinity).ToArray();
            var remaining = people.Select(p => p.Labor).ToArray();
            var choices = new int[people.Length][];
            var next = new int[people.Length];
            for (int p = 0; p < people.Length; p++)
                choices[p] = Enumerable.Range(0, works.Length)
                    .Where(w => CanWork(cells[p, w]) && priorities[p, w] > 0 && stock[w] > Epsilon)
                    .OrderBy(w => priorities[p, w]).ThenByDescending(w => works[w].NaturalPriority).ThenBy(w => w).ToArray();
            var requested = new double[works.Length];
            var requestedOutput = new double[works.Length];
            var fraction = new double[works.Length];
            var stationRequests = new double[works.Length];
            var stationGrants = new double[works.Length];
            var stationTime = stations.Open();
            double unmapped = 0;
            // Each round exhausts at least one order or worker. Contenders for finite capacity
            // split it proportionally in the estimate; the game reserves real jobs individually.
            while (true)
            {
                Array.Clear(requested);
                Array.Clear(requestedOutput);
                bool active = false;
                for (int p = 0; p < people.Length; p++)
                {
                    if (remaining[p] <= Epsilon) continue;
                    while (next[p] < choices[p].Length &&
                        (stock[choices[p][next[p]]] <= Epsilon || outputLeft[choices[p][next[p]]] <= Epsilon ||
                         stations.Exhausted(choices[p][next[p]], stationTime))) next[p]++;
                    if (next[p] == choices[p].Length) continue;
                    int work = choices[p][next[p]];
                    requested[work] += remaining[p];
                    if (works[work].OutputDemand.HasValue)
                        requestedOutput[work] += remaining[p] * Math.Max(0, cells[p, work]!.Suitability);
                    active = true;
                }
                if (!active) break;
                for (int w = 0; w < works.Length; w++)
                {
                    if (requested[w] <= 0) { stationRequests[w] = 0; continue; }
                    fraction[w] = Math.Min(1, stock[w] / requested[w]);
                    if (requestedOutput[w] > 0)
                        fraction[w] = Math.Min(fraction[w], outputLeft[w] / requestedOutput[w]);
                    stationRequests[w] = requested[w] * fraction[w];
                }
                if (stations.Shared) stations.Grant(stationRequests, stationTime, stationGrants);
                for (int w = 0; w < works.Length; w++)
                {
                    if (requested[w] <= 0) continue;
                    if (stations.Shared) fraction[w] = stationGrants[w] / requested[w];
                    stock[w] = Math.Max(0, stock[w] - requested[w] * fraction[w]);
                    outputLeft[w] = Math.Max(0, outputLeft[w] - requestedOutput[w] * fraction[w]);
                }
                for (int p = 0; p < people.Length; p++)
                {
                    if (remaining[p] <= Epsilon || next[p] == choices[p].Length) continue;
                    int w = choices[p][next[p]];
                    double used = remaining[p] * fraction[w];
                    remaining[p] -= used;
                    hours[p, w] += used;
                    if (groups[w] >= 0) shares[groups[w]] += used;
                    else unmapped += used;
                }
            }
            return new(hours, shares, unmapped);
        }

        public void ImproveRounding(double[,] allocation, int[,] priorities, ref Forecast forecast)
        {
            // A worker can only have one leading job. When rounding several workers this
            // can overstaff one strategy. Try the transfers with the largest predicted
            // reduction in deficit first, then verify each against the whole work table.
            var best = Error(forecast);
            int trials = 0;
            int budget = Math.Clamp(100_000 / Math.Max(1, priorities.Length), 2, 48);
            while (best.TargetError > Epsilon && trials < budget)
            {
                var transfers = new List<(int Pawn, int From, int To, double MinimumGain, double TargetGain, double Planned, double SkillGain)>();
                var hours = forecast.Hours;
                for (int p = 0; p < people.Length; p++)
                {
                    int from = Enumerable.Range(0, works.Length)
                        .Where(w => hours[p, w] > Epsilon && Writable(p, w))
                        .OrderBy(w => priorities[p, w]).ThenByDescending(w => works[w].NaturalPriority).ThenBy(w => w)
                        .FirstOrDefault(-1);
                    if (from < 0) continue;
                    int g = groups[from];
                    for (int to = 0; to < works.Length; to++)
                    {
                        int h = groups[to];
                        if (h < 0 || h == g || !Writable(p, to) || priorities[p, to] <= priorities[p, from] ||
                            forecast.Shares[h] + Epsilon >= targets[h]) continue;
                        double moved = Math.Min(forecast.Hours[p, from], limits[to]);
                        double beforeG = forecast.Shares[g], beforeH = forecast.Shares[h];
                        double targetGain = Math.Abs(beforeG - targets[g]) + Math.Abs(beforeH - targets[h]) -
                            Math.Abs(beforeG - moved - targets[g]) - Math.Abs(beforeH + moved - targets[h]);
                        double minimumGain = Math.Max(0, minimums[g] - beforeG) + Math.Max(0, minimums[h] - beforeH) -
                            Math.Max(0, minimums[g] - beforeG + moved) - Math.Max(0, minimums[h] - beforeH - moved);
                        if (minimumGain < -Epsilon || (minimumGain <= Epsilon && targetGain <= Epsilon)) continue;
                        transfers.Add((p, from, to, minimumGain, targetGain, allocation[p, to],
                            cells[p, to]!.Suitability - cells[p, from]!.Suitability));
                    }
                }
                bool improved = false;
                foreach (var t in transfers.OrderByDescending(x => x.MinimumGain).ThenByDescending(x => x.TargetGain)
                    .ThenByDescending(x => x.Planned).ThenByDescending(x => x.SkillGain))
                {
                    int p = t.Pawn, first = t.From, w = t.To;
                    (priorities[p, first], priorities[p, w]) = (priorities[p, w], priorities[p, first]);
                    var alternative = Estimate(priorities);
                    var error = Error(alternative);
                    trials++;
                    if (error.CompareTo(best) < 0)
                    {
                        forecast = alternative;
                        best = error;
                        improved = true;
                        break;
                    }
                    (priorities[p, first], priorities[p, w]) = (priorities[p, w], priorities[p, first]);
                    if (trials >= budget) return;
                }
                if (!improved) return;
            }
        }

        private (double MinimumDeficit, double TargetError) Error(Forecast forecast)
        {
            double deficit = 0, error = 0;
            for (int g = 0; g < targets.Length; g++)
            {
                deficit += Math.Max(0, minimums[g] - forecast.Shares[g]);
                error += Math.Abs(targets[g] - forecast.Shares[g]);
            }
            return (Math.Round(deficit, 7), Math.Round(error, 7));
        }

        public ShortfallReason Explain(int group, double[] potential, double actual)
        {
            if (actual + Epsilon >= targets[group]) return ShortfallReason.None;
            var columns = Enumerable.Range(0, works.Length).Where(w => groups[w] == group).ToArray();
            if (columns.Length == 0) return ShortfallReason.NoMappedWork;
            if (columns.All(w => potential[w] <= Epsilon)) return ShortfallReason.NoEligiblePawn;
            double workerLimit = Enumerable.Range(0, people.Length)
                .Where(p => columns.Any(w => limits[w] > Epsilon && CanWork(cells[p, w])))
                .Sum(p => people[p].Labor);
            var caps = allocationLimits ?? limits;
            if (Math.Min(workerLimit, columns.Sum(w => caps[w])) + Epsilon < targets[group]) return ShortfallReason.Capacity;
            return ShortfallReason.PriorityOrder;
        }
    }
}
