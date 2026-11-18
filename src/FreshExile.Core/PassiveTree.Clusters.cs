namespace FreshExile.Core;

public sealed partial class PassiveTree
{
    /// <summary>Nodes per orbit and orbit radii of the export: the cluster rings are placed with them.</summary>
    public int[] SkillsPerOrbit { get; private set; } = [];
    public double[] OrbitRadii { get; private set; } = [];

    /// <summary>Cluster notables as the export lists them (no group of their own): their stats by name.</summary>
    Dictionary<string, PassiveNode> _clusterNotables = new(StringComparer.OrdinalIgnoreCase);
    public PassiveNode? ClusterNotable(string name) => _clusterNotables.GetValueOrDefault(name);

    Dictionary<string, string[]>? _recipes;

    /// <summary>
    /// The oils of an anointment, "Allocates Toxic Strikes" → ClearOil, AmberOil, CrimsonOil (the export's own recipe);
    /// null for a notable no oil can anoint.
    /// </summary>
    public string[]? AnointRecipe(string notable)
    {
        _recipes ??= Nodes.Values.Where(n => n.Recipe.Length > 0 && n.Name.Length > 0)
            .GroupBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Recipe, StringComparer.OrdinalIgnoreCase);
        return _recipes.GetValueOrDefault(notable);
    }

    /// <summary>
    /// The tree with the stage's cluster jewels grown into it, the way PoB and the game draw them: their passives on
    /// rings in the sockets' proxy groups, linked to the socket, smaller sockets moved onto the ring. Nodes and links of
    /// the base tree are shared, never changed; the tree itself when no cluster jewel is socketed.
    /// </summary>
    public PassiveTree WithClusters(IReadOnlyList<(int Socket, PobItem Item)> jewels)
    {
        var clusters = jewels.Where(j => ClusterJewel.IsCluster(j.Item)).GroupBy(j => j.Socket).ToDictionary(g => g.Key, g => g.First().Item);
        if (clusters.Count == 0) return this;

        var t = new PassiveTree { SkillsPerOrbit = SkillsPerOrbit, OrbitRadii = OrbitRadii, _clusterNotables = _clusterNotables };
        foreach (var (id, n) in Nodes) t.Nodes[id] = n;
        foreach (var (g, list) in MasteriesByGroup) t.MasteriesByGroup[g] = list;

        // the main tree's sockets first; a smaller socket is built once its bigger cluster has put it on the ring
        var queue = new Queue<int>(clusters.Keys.Where(s => Nodes.TryGetValue(s, out var n) && n.ExpansionParent == null));
        var done = new HashSet<int>();
        while (queue.Count > 0)
        {
            var socketId = queue.Dequeue();
            if (!done.Add(socketId) || !t.Nodes.TryGetValue(socketId, out var socket)) continue;
            if (ClusterGraph.Build(clusters[socketId], socket, t) is not { } sub) continue;

            var radius = sub.Orbit < OrbitRadii.Length ? OrbitRadii[sub.Orbit] : 0;
            var count = sub.Orbit < SkillsPerOrbit.Length ? SkillsPerOrbit[sub.Orbit] : 12;
            var ring = sub.Nodes.OrderBy(n => n.Index).ToList();
            var placed = new Dictionary<int, PassiveNode>();
            foreach (var c in ring)
            {
                var a = OrbitAngle(count, c.OrbitIndex) * Math.PI / 180;
                // a socket keeps what the export knows of it (its proxy, its size), only its place and links are new
                var old = c.Kind == NodeKind.Jewel ? t.Nodes.GetValueOrDefault(c.Id) : null;
                var node = new PassiveNode
                {
                    Id = c.Id, Name = c.Name, Kind = c.Kind, Stats = c.Stats, Group = sub.Group, Orbit = sub.Orbit, OrbitIndex = c.OrbitIndex,
                    ExpansionSize = old?.ExpansionSize, ExpansionIndex = old?.ExpansionIndex ?? 0, ExpansionParent = old?.ExpansionParent,
                    ExpansionProxy = old?.ExpansionProxy, IsClusterNode = true,
                    GroupX = sub.CenterX, GroupY = sub.CenterY,
                    X = sub.CenterX + radius * Math.Sin(a), Y = sub.CenterY - radius * Math.Cos(a),
                    Drawable = true,
                };
                placed[c.Index] = node;
            }

            // the ring, in place order, closed on a medium or large cluster; the entrance to the socket
            var links = placed.Values.ToDictionary(n => n.Id, _ => new List<int>());
            var outs = placed.Values.ToDictionary(n => n.Id, _ => new List<int>());
            for (var i = 1; i < ring.Count; i++) Link(placed[ring[i - 1].Index].Id, placed[ring[i].Index].Id);
            if (sub.Closed && ring.Count > 2) Link(placed[ring[^1].Index].Id, placed[ring[0].Index].Id);
            void Link(int a, int b) { links[a].Add(b); links[b].Add(a); outs[a].Add(b); }

            foreach (var n in placed.Values)
            {
                var o = outs[n.Id];
                if (n.Id == sub.EntranceId) o.Add(socketId);
                t.Nodes[n.Id] = Clone(n, o.ToArray(), [.. links[n.Id], .. n.Id == sub.EntranceId ? new[] { socketId } : []]);
            }
            // the socket opens into the cluster
            t.Nodes[socketId] = Clone(socket, socket.Out, [.. socket.Links, sub.EntranceId]);

            foreach (var n in placed.Values.Where(n => n.Kind == NodeKind.Jewel && clusters.ContainsKey(n.Id))) queue.Enqueue(n.Id);
        }

        t.BuildEdges(OrbitRadii);
        return t;
    }

    static PassiveNode Clone(PassiveNode n, int[] outs, int[] links) => new()
    {
        Id = n.Id, Name = n.Name, Kind = n.Kind, Stats = n.Stats, Ascendancy = n.Ascendancy, IsAscendancyStart = n.IsAscendancyStart,
        ClassStartIndex = n.ClassStartIndex, Group = n.Group, Orbit = n.Orbit, OrbitIndex = n.OrbitIndex,
        X = n.X, Y = n.Y, GroupX = n.GroupX, GroupY = n.GroupY, Drawable = n.Drawable, Out = outs, Links = links.Distinct().ToArray(),
        MasteryEffects = n.MasteryEffects, ExpansionSize = n.ExpansionSize, ExpansionIndex = n.ExpansionIndex,
        ExpansionParent = n.ExpansionParent, ExpansionProxy = n.ExpansionProxy, IsClusterNode = n.IsClusterNode,
    };
}
