namespace StrategicWork.Core;

public static partial class WorkPlanner
{
    private sealed record Metrics(double[] Performance, double[] Training)
    {
        public double TotalTraining { get; } = Training.Sum();
    }

    // Assess candidates after rounding to real priorities. Within a strategy, use the actual
    // reference task mix; surplus shirts cannot compensate for missing components or meals.
    private sealed class TrainingComparison
    {
        private readonly PawnInput[] people;
        private readonly WorkColumn[] works;
        private readonly WorkCell?[,] cells;
        private readonly int[] groups;
        private readonly double[] targets;
        private readonly Table reference;
        private readonly double tolerance;
        private readonly double[] referenceOutput;
        private readonly double[] columnWeight;
        private readonly double[] strategyWeight;
        private readonly bool[] freezeColumn;
        private readonly bool[,] adjustable;
        private Table best;

        public bool[] Comparable { get; }
        public Metrics ReferenceMetrics { get; }
        public Metrics BestMetrics { get; private set; }

        public TrainingComparison(PawnInput[] people, WorkColumn[] works, WorkCell?[,] cells, int[] groups,
            double[] targets, Table reference, double tolerance)
        {
            this.people = people;
            this.works = works;
            this.cells = cells;
            this.groups = groups;
            this.targets = targets;
            this.reference = best = reference;
            this.tolerance = tolerance;

            referenceOutput = new double[works.Length];
            columnWeight = new double[works.Length];
            freezeColumn = new bool[works.Length];
            strategyWeight = new double[targets.Length];
            Comparable = Enumerable.Repeat(true, targets.Length).ToArray();
            adjustable = new bool[people.Length, works.Length];
            for (int w = 0; w < works.Length; w++)
            {
                int group = groups[w];
                if (group < 0) continue;
                for (int p = 0; p < people.Length; p++)
                {
                    double hours = reference.Forecast.Hours[p, w];
                    if (hours <= Epsilon) continue;
                    var cell = cells[p, w]!;
                    referenceOutput[w] += hours * Math.Max(0, cell.Suitability);
                    columnWeight[w] += hours;
                    if (!cell.PerformanceComparable || cell.Suitability <= 0) freezeColumn[w] = true;
                }
                if (freezeColumn[w]) Comparable[group] = false;
                else strategyWeight[group] += columnWeight[w];
            }
            for (int g = 0; g < targets.Length; g++)
                Comparable[g] &= targets[g] > Epsilon && strategyWeight[g] > Epsilon;
            ReferenceMetrics = BestMetrics = Measure(reference.Forecast);
        }

        public Table Select(Model model, bool preserveRemainder)
        {
            if (tolerance <= 0) return reference;
            bool anyTraining = false;
            for (int p = 0; p < people.Length; p++)
            for (int w = 0; w < works.Length; w++)
            {
                int group = groups[w];
                adjustable[p, w] = group >= 0 && targets[group] > Epsilon && strategyWeight[group] > Epsilon &&
                    !freezeColumn[w] && model.CanAdjustForTraining(p, w);
                anyTraining |= adjustable[p, w] && cells[p, w]!.TrainingGain > 0;
            }
            if (!anyTraining) return reference;

            // Different flow objectives propose entire teams, including cycles involving several
            // specialists. A proposal must pass the same per-strategy limits after discretization.
            foreach (double weight in new[] { 0.25, 0.6, 1.0 })
            {
                var proposal = model.SolveTable(preserveRemainder, weight).Priorities;
                for (int p = 0; p < people.Length; p++)
                for (int w = 0; w < works.Length; w++)
                    if (!adjustable[p, w]) proposal[p, w] = reference.Priorities[p, w];
                Consider(new(proposal, model.Estimate(proposal)));
            }
            TryIndividualExchanges(model);
            return best;
        }

        private Metrics Measure(Forecast forecast)
        {
            var performance = new double[targets.Length];
            var growth = new double[targets.Length];
            for (int w = 0; w < works.Length; w++)
            {
                int group = groups[w];
                if (group < 0 || freezeColumn[w] || targets[group] <= Epsilon) continue;
                double output = 0;
                for (int p = 0; p < people.Length; p++)
                {
                    double hours = forecast.Hours[p, w];
                    if (hours <= Epsilon) continue;
                    var cell = cells[p, w]!;
                    output += hours * Math.Max(0, cell.Suitability);
                    growth[group] += hours * cell.TrainingGain;
                }
                if (strategyWeight[group] > Epsilon && referenceOutput[w] > 0)
                    performance[group] += columnWeight[w] / strategyWeight[group] *
                        Math.Min(1, output / referenceOutput[w]) * 100;
            }
            return new(performance, growth);
        }

        private bool Consider(Table candidate)
        {
            for (int g = 0; g < targets.Length; g++)
            {
                double achievable = Math.Min(targets[g], reference.Forecast.Shares[g]);
                if (candidate.Forecast.Shares[g] + Epsilon < achievable) return false;
            }
            // Changes to the order of a different column could still steal time from an unknown
            // worker/recipe. Do not spend an unverifiable loss as part of the training budget.
            for (int w = 0; w < works.Length; w++)
            {
                int group = groups[w];
                if (group < 0 || targets[group] <= Epsilon) continue;
                for (int p = 0; p < people.Length; p++)
                    if ((freezeColumn[w] || cells[p, w] is { PerformanceComparable: false }) &&
                        Math.Abs(candidate.Forecast.Hours[p, w] - reference.Forecast.Hours[p, w]) > Epsilon)
                        return false;
            }
            var metric = Measure(candidate.Forecast);
            for (int g = 0; g < targets.Length; g++)
                if (strategyWeight[g] > Epsilon && metric.Performance[g] + Epsilon < ReferenceMetrics.Performance[g] * (1 - tolerance))
                    return false;
            // Equal training value only breaks a tie between two actual improvements; prefer the
            // reference unchanged whenever switching does not create useful growth.
            if (metric.TotalTraining <= ReferenceMetrics.TotalTraining + Epsilon) return false;
            if (metric.TotalTraining < BestMetrics.TotalTraining - Epsilon) return false;
            if (metric.TotalTraining <= BestMetrics.TotalTraining + Epsilon &&
                metric.Performance.Sum() <= BestMetrics.Performance.Sum() + Epsilon) return false;
            best = candidate;
            BestMetrics = metric;
            return true;
        }

        private void TryIndividualExchanges(Model model)
        {
            int remaining = Math.Clamp(150_000 / Math.Max(1, cells.Length), 4, 96);
            while (remaining > 0)
            {
                // A whole team of trainees can be too expensive even when one mentor/apprentice
                // exchange is affordable. Rank proposals before running the full colony forecast.
                var trades = new List<(int First, int Second, double Gain)>();
                for (int a = 0; a < people.Length; a++)
                for (int b = a + 1; b < people.Length; b++)
                {
                    double gain = 0;
                    for (int w = 0; w < works.Length; w++)
                        if (adjustable[a, w] && adjustable[b, w] && best.Priorities[a, w] != best.Priorities[b, w])
                            gain += (best.Forecast.Hours[a, w] - best.Forecast.Hours[b, w]) *
                                    (cells[b, w]!.TrainingGain - cells[a, w]!.TrainingGain);
                    if (gain > Epsilon) trades.Add((a, b, gain));
                }
                bool improved = false;
                foreach (var (a, b, _) in trades.OrderByDescending(t => t.Gain))
                {
                    if (remaining-- <= 0) return;
                    var priorities = (int[,])best.Priorities.Clone();
                    for (int w = 0; w < works.Length; w++)
                        if (adjustable[a, w] && adjustable[b, w])
                            (priorities[a, w], priorities[b, w]) = (priorities[b, w], priorities[a, w]);
                    if (!Consider(new(priorities, model.Estimate(priorities)))) continue;
                    improved = true;
                    break;
                }
                if (!improved) return;
            }
        }
    }
}
