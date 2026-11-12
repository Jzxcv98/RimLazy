namespace StrategicWork.Core;

// Each augmenting path can reassign an earlier worker through a reverse edge. This allows
// a generalist to cover a different job when a specialist has only one possible assignment.
internal sealed class LaborFlow(int nodeCount)
{
    internal sealed class Edge(int to, double capacity, int cost)
    {
        public int To { get; } = to;
        public double Remaining { get; set; } = capacity;
        public int Cost { get; } = cost;
        public Edge Reverse { get; set; } = null!;
    }

    private readonly List<Edge>[] edges = Enumerable.Range(0, nodeCount).Select(_ => new List<Edge>()).ToArray();

    public Edge Add(int from, int to, double capacity, int cost)
    {
        var forward = new Edge(to, capacity, cost);
        var reverse = new Edge(from, 0, -cost);
        forward.Reverse = reverse;
        reverse.Reverse = forward;
        edges[from].Add(forward);
        edges[to].Add(reverse);
        return forward;
    }

    public void Solve(int source, int sink)
    {
        var distance = new long[edges.Length];
        var queued = new bool[edges.Length];
        var via = new Edge?[edges.Length];
        var queue = new Queue<int>();
        while (true)
        {
            Array.Fill(distance, long.MaxValue);
            distance[source] = 0;
            queued[source] = true;
            queue.Enqueue(source);
            while (queue.TryDequeue(out var from))
            {
                queued[from] = false;
                foreach (var edge in edges[from])
                {
                    if (edge.Remaining <= WorkPlanner.Epsilon) continue;
                    long next = distance[from] + edge.Cost;
                    if (next >= distance[edge.To]) continue;
                    distance[edge.To] = next;
                    via[edge.To] = edge;
                    if (queued[edge.To]) continue;
                    queued[edge.To] = true;
                    queue.Enqueue(edge.To);
                }
            }
            if (distance[sink] == long.MaxValue) return;

            double amount = double.MaxValue;
            for (var v = sink; v != source; v = via[v]!.Reverse.To)
                amount = Math.Min(amount, via[v]!.Remaining);
            for (var v = sink; v != source; v = via[v]!.Reverse.To)
            {
                var edge = via[v]!;
                edge.Remaining -= amount;
                edge.Reverse.Remaining += amount;
            }
        }
    }
}
