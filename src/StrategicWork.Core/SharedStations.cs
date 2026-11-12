namespace StrategicWork.Core;

// Multiple work columns may offer jobs at the same physical workbench. The grants use labor
// units, so one bench provides one unit over the horizon, even if it has dozens of bills.
internal sealed class SharedStations
{
    private readonly int[][] users;
    private readonly int[][] resources;
    private readonly bool[] constrained;
    public bool Shared { get; }

    public SharedStations(WorkColumn[] columns)
    {
        var ids = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int w = 0; w < columns.Length; w++)
        {
            if (string.IsNullOrWhiteSpace(columns[w].SharedSiteIds)) continue;
            foreach (string id in columns[w].SharedSiteIds!.Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.Ordinal))
            {
                if (!ids.TryGetValue(id, out var work)) ids.Add(id, work = []);
                work.Add(w);
            }
        }
        Shared = ids.Values.Any(list => list.Count > 1);
        users = Shared ? ids.OrderBy(k => k.Value.Count).ThenBy(k => k.Key, StringComparer.Ordinal)
            .Select(k => k.Value.ToArray()).ToArray() : [];
        var grouped = Enumerable.Range(0, columns.Length).Select(_ => new List<int>()).ToArray();
        for (int site = 0; site < users.Length; site++)
            foreach (int w in users[site]) grouped[w].Add(site);
        resources = grouped.Select(s => s.ToArray()).ToArray();
        constrained = resources.Select(s => s.Length > 0).ToArray();
    }

    public double[] Open() => Enumerable.Repeat(1d, users.Length).ToArray();
    public bool Exhausted(int work, double[] remaining) =>
        constrained[work] && resources[work].All(s => remaining[s] <= WorkPlanner.Epsilon);

    // Dedicated benches first. Share a contested bench only between columns actually requesting
    // work this round; capacity left when an order finishes is available to later fallback jobs.
    public void Grant(double[] requested, double[] remaining, double[] granted)
    {
        for (int w = 0; w < requested.Length; w++) granted[w] = constrained[w] ? 0 : requested[w];
        for (int s = 0; s < users.Length; s++)
        {
            if (remaining[s] <= WorkPlanner.Epsilon) continue;
            var eligible = users[s];
            double demand = 0;
            foreach (int w in eligible) demand += Math.Max(0, requested[w] - granted[w]);
            if (demand <= 0) continue;
            double capacity = Math.Min(remaining[s], demand);
            if (demand <= remaining[s])
            {
                foreach (int w in eligible) granted[w] = Math.Max(requested[w], granted[w]);
            }
            else
            {
                // A short order only needs a fraction of a shift. Once it receives what it can
                // use, divide the rest of this station's time among the remaining requests.
                int count = eligible.Length;
                double left = capacity;
                foreach (int w in eligible.OrderBy(w => requested[w] - granted[w]))
                {
                    double share = Math.Min(Math.Max(0, requested[w] - granted[w]), left / count--);
                    granted[w] += share;
                    left -= share;
                }
            }
            remaining[s] -= capacity;
        }
    }
}
