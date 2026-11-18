namespace FreshExile.Core;

public sealed record PlanResult(
    IReadOnlyList<int> Order,
    IReadOnlySet<int> Refund,
    IReadOnlyList<int> Ascendancy,
    IReadOnlySet<int> Have,
    IReadOnlySet<int> Target);

public static class PathPlanner
{
    /// <summary>
    /// Order in which to take the target nodes starting from what is already allocated:
    /// repeatedly walk to the nearest reachable notable / keystone / jewel / mastery, small passives last.
    /// </summary>
    public static PlanResult Plan(PassiveTree tree, IEnumerable<int> haveIds, IEnumerable<int> targetIds, int classId)
    {
        var have = haveIds.Where(tree.Nodes.ContainsKey).ToHashSet();
        var target = targetIds.Where(tree.Nodes.ContainsKey).ToHashSet();

        bool IsMain(int id) => tree.Nodes[id] is { Ascendancy: null, Kind: not NodeKind.ClassStart };

        var ascendancy = target
            .Where(id => tree.Nodes[id] is { Ascendancy: not null, IsAscendancyStart: false } && !have.Contains(id))
            .ToList();
        var refund = have.Where(id => IsMain(id) && !target.Contains(id)).ToHashSet();

        var taken = target.Where(id => IsMain(id) && have.Contains(id)).ToHashSet();
        if (tree.ClassStart(classId) is { } start) taken.Add(start.Id);
        var remaining = target.Where(id => IsMain(id) && !taken.Contains(id)).ToHashSet();

        var order = new List<int>(remaining.Count);
        while (remaining.Count > 0)
        {
            var parent = new Dictionary<int, int>();
            var dist = new Dictionary<int, int>();
            var queue = new Queue<int>();
            foreach (var t in taken) { dist[t] = 0; queue.Enqueue(t); }

            int goal = -1, goalScore = int.MaxValue;
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                foreach (var nb in tree.Neighbours(tree.Nodes[cur]))
                {
                    if (!remaining.Contains(nb) || dist.ContainsKey(nb)) continue;
                    dist[nb] = dist[cur] + 1;
                    parent[nb] = cur;
                    queue.Enqueue(nb);
                    var score = dist[nb] + (tree.Nodes[nb].IsKey ? 0 : 1000);
                    if (score < goalScore) { goalScore = score; goal = nb; }
                }
            }

            if (goal < 0)
            {
                // unreachable from what is allocated (e.g. needs a refunded path): append as is
                order.AddRange(remaining.OrderBy(id => id));
                break;
            }

            var path = new List<int>();
            for (var n = goal; !taken.Contains(n); n = parent[n]) path.Add(n);
            path.Reverse();
            foreach (var n in path)
            {
                order.Add(n);
                taken.Add(n);
                remaining.Remove(n);
            }
        }

        return new PlanResult(order, refund, ascendancy, have, target);
    }
}
