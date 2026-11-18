using System.Globalization;
using System.Text.RegularExpressions;

namespace FreshExile.Core;

/// <summary>A notable of a cluster jewel and whether the build takes it.</summary>
public sealed record ClusterNotable(string Name, int NodeId, bool Taken);

/// <summary>What a socketed cluster jewel gives the stage: its notables taken or not, and how many small passives.</summary>
public sealed record ClusterAllocation(IReadOnlyList<ClusterNotable> Notables, int SmallTaken, int SmallTotal);

/// <summary>
/// The smart cluster search: <see cref="Wanted"/> all required, and <see cref="Count"/> more out of <see cref="Others"/>
/// (the notables that sit off the path to the wanted ones). <see cref="Skipped"/> notables of the enchant would block it.
/// </summary>
public sealed record ClusterPlan(IReadOnlyList<string> Wanted, IReadOnlyList<string> Others, int Count, int Skipped);

/// <summary>
/// Which nodes of a cluster jewel a PoB tree has: the ids PoB gives the nodes it generates, after its BuildSubgraph
/// (Classes/PassiveSpec.lua) and getClusterJewelNotableIndices (Modules/ItemTools.lua).
/// Bits 0-3 node index, 4-5 cluster size, 6-8 large socket index, 9-10 medium socket index, 16 set.
/// </summary>
public static partial class ClusterJewel
{
    sealed record Layout(int SizeIndex, int[] Small, int[] Notable, int[] Socket);

    static readonly Dictionary<string, Layout> Layouts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Small Cluster Jewel"] = new(0, [0, 4, 2], [4], [4]),
        ["Medium Cluster Jewel"] = new(1, [0, 6, 8, 4, 10, 2], [6, 10, 2, 0], [6]),
        ["Large Cluster Jewel"] = new(2, [0, 4, 6, 8, 10, 2, 7, 5, 9, 3, 11, 1], [6, 4, 8, 10, 2], [4, 8, 6]),
    };

    /// <summary>PoB's notableSortOrder (Data/ClusterJewels.lua): the order notables take their places on the jewel.</summary>
    static readonly Lazy<Dictionary<string, int>> SortOrder = new(() =>
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var s = typeof(ClusterJewel).Assembly.GetManifestResourceStream("ClusterNotables.tsv");
        if (s == null) return map;
        using var r = new StreamReader(s);
        while (r.ReadLine() is { } line)
        {
            var tab = line.IndexOf('\t');
            if (tab > 0 && int.TryParse(line[(tab + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) map[line[..tab]] = v;
        }
        return map;
    });

    public static bool IsCluster(PobItem item) => Layouts.ContainsKey(item.BaseType);

    /// <summary>The notables PoB knows, in the order they take their places on the jewel.</summary>
    internal static List<string> SortedNotables(IEnumerable<string> names)
    {
        var order = SortOrder.Value;
        return names.Where(order.ContainsKey).OrderBy(n => order[n]).ToList();
    }

    /// <summary>The ring places of the notables, ascending (the first sorted notable takes the first).</summary>
    internal static List<int> NotableIndexesFor(string baseType, int sockets, int notables, int nodeCount) =>
        Layouts.TryGetValue(baseType, out var layout) ? NotableIndexes(baseType, layout, sockets, notables, nodeCount) : [];

    /// <summary>Null when the item is not a cluster jewel or the socket is unknown to the tree data.</summary>
    public static ClusterAllocation? Allocation(PobItem item, int socket, PassiveTree tree, IReadOnlyList<int> nodes)
    {
        if (!Layouts.TryGetValue(item.BaseType, out var layout)) return null;
        if (!tree.Nodes.TryGetValue(socket, out var sock) || sock.ExpansionSize == null) return null;

        // the id of the subgraph: this socket's index and the indexes of the bigger clusters around it
        var id = 0x10000;
        for (PassiveNode? s = sock; s?.ExpansionSize is int size; s = s.ExpansionParent is int p && tree.Nodes.TryGetValue(p, out var pn) ? pn : null)
            id += size switch { 2 => s.ExpansionIndex << 6, 1 => s.ExpansionIndex << 9, _ => 0 };
        var baseId = id + (layout.SizeIndex << 4);

        int? nodeCount = null;
        var sockets = 0;
        var nothing = 0;
        var names = new List<string>();
        foreach (var m in item.Mods)
        {
            var t = m.Text;
            Match x;
            if ((x = AddsRx().Match(t)).Success) nodeCount = int.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture);
            else if ((x = SocketsRx().Match(t)).Success) sockets = int.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture);
            else if ((x = NothingRx().Match(t)).Success) nothing = int.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture);
            else if ((x = NotableRx().Match(t)).Success) names.Add(x.Groups[1].Value.Trim());
        }
        var order = SortOrder.Value;
        names = names.Where(order.ContainsKey).OrderBy(n => order[n]).ToList();
        var count = nodeCount ?? sockets + names.Count + nothing;

        var taken = nodes.ToHashSet();
        var indexes = NotableIndexes(item.BaseType, layout, sockets, names.Count, count);
        var notables = names.Zip(indexes, (n, i) => new ClusterNotable(n, baseId + i, taken.Contains(baseId + i))).ToList();

        // the small passives: every other node of the cluster (the mastery sits at +12, the sockets keep real ids)
        var notableIds = notables.Select(n => n.NodeId).ToHashSet();
        var smallTaken = taken.Count(n => n >= baseId && n < baseId + 12 && !notableIds.Contains(n));
        var smallTotal = Math.Max(0, count - sockets - notables.Count);
        return new ClusterAllocation(notables, Math.Min(smallTaken, smallTotal), smallTotal);
    }

    /// <summary>The notables each cluster enchant can roll (Data/ClusterSkills.tsv, from RePoE).</summary>
    static readonly Lazy<List<HashSet<string>>> Skills = new(() =>
    {
        var list = new List<HashSet<string>>();
        using var s = typeof(ClusterJewel).Assembly.GetManifestResourceStream("ClusterSkills.tsv");
        if (s == null) return list;
        using var r = new StreamReader(s);
        while (r.ReadLine() is { } line)
        {
            var tab = line.IndexOf('\t');
            if (tab > 0) list.Add(line[(tab + 1)..].Split('|').ToHashSet(StringComparer.OrdinalIgnoreCase));
        }
        return list;
    });

    /// <summary>
    /// The smart search for a cluster jewel: the notables the build takes, and which others may fill the remaining
    /// places without standing in their way. The game seats notables by a fixed order (PoB's notableSortOrder), so the
    /// thirds that land on the far side of the ring, off the path to the wanted ones, are the ones in certain gaps of
    /// that order. Null when there is nothing to choose (every notable wanted, or the jewel is not a cluster).
    /// </summary>
    /// <param name="notables">The notables on this jewel.</param>
    /// <param name="wanted">Those of them the build takes.</param>
    public static ClusterPlan? Plan(string baseType, int nodeCount, int sockets, IReadOnlyList<string> notables, IReadOnlyCollection<string> wanted)
    {
        if (!Layouts.TryGetValue(baseType, out var layout)) return null;
        var order = SortOrder.Value;
        var want = notables.Where(n => wanted.Contains(n, StringComparer.OrdinalIgnoreCase) && order.ContainsKey(n))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => order[n]).ToList();
        var others = notables.Count - want.Count;
        if (want.Count == 0 || others <= 0) return null;

        // every notable this enchant can roll: the skills whose list holds all of the jewel's notables
        var pool = Skills.Value.Where(s => notables.All(s.Contains)).SelectMany(s => s).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(n => order.ContainsKey(n) && !want.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
        if (pool.Count == 0) return null;

        // the others all in one gap of the wanted ones' order: gap 0 before the first, gap k after the last
        var cost = new int[want.Count + 1];
        for (var gap = 0; gap <= want.Count; gap++)
        {
            var seats = new List<bool>();   // in order: true = wanted
            for (var i = 0; i <= want.Count; i++)
            {
                if (i == gap) seats.AddRange(Enumerable.Repeat(false, others));
                if (i < want.Count) seats.Add(true);
            }
            cost[gap] = PathCost(baseType, layout, sockets, nodeCount, seats);
        }
        var best = cost.Min();
        if (best == int.MaxValue) return null;
        var gaps = Enumerable.Range(0, cost.Length).Where(g => cost[g] == best).ToList();
        // a gap is the stretch of the order between two wanted notables
        bool InGap(int o, int g) => (g == 0 || o > order[want[g - 1]]) && (g == want.Count || o < order[want[g]]);
        var fits = pool.Where(n => gaps.Any(g => InGap(order[n], g))).OrderBy(n => order[n]).ToList();
        var skipped = pool.Count - fits.Count;
        return fits.Count == 0 ? null : new ClusterPlan(want, fits, others, skipped);
    }

    /// <summary>
    /// The passives to allocate to reach every wanted notable from the entry (index 0) without allocating an unwanted
    /// one: the cheapest arc of the ring around the entry; int.MaxValue when no arc avoids them.
    /// </summary>
    static int PathCost(string baseType, Layout layout, int sockets, int nodeCount, IReadOnlyList<bool> seats)
    {
        var notableAt = NotableIndexes(baseType, layout, sockets, seats.Count, nodeCount);
        if (notableAt.Count < seats.Count) return int.MaxValue;
        var ring = baseType.StartsWith("Small", StringComparison.OrdinalIgnoreCase) ? 6 : 12;
        // the occupied places: sockets, notables, and the small passives filling the rest in PoB's order
        var socketAt = new HashSet<int>(baseType.StartsWith("Large", StringComparison.OrdinalIgnoreCase) && sockets == 1 ? [6] : layout.Socket.Take(sockets));
        var used = new HashSet<int>(socketAt.Concat(notableAt));
        foreach (var i in layout.Small)
        {
            if (used.Count >= nodeCount) break;
            used.Add(i);
        }
        var wanted = notableAt.Where((_, k) => seats[k]).ToHashSet();
        var unwanted = notableAt.Where((_, k) => !seats[k]).ToHashSet();

        var best = int.MaxValue;
        // the arc reaches cw (clockwise) indexes 1..a and ccw indexes ring-1 down to ring-b
        for (var a = 0; a < ring; a++)
            for (var b = 0; a + b < ring; b++)
            {
                var arc = new HashSet<int> { 0 };
                for (var i = 1; i <= a; i++) arc.Add(i);
                for (var i = 1; i <= b; i++) arc.Add(ring - i);
                if (!wanted.IsSubsetOf(arc) || unwanted.Overlaps(arc)) continue;
                best = Math.Min(best, arc.Count(used.Contains));
            }
        return best;
    }

    static List<int> NotableIndexes(string baseType, Layout layout, int socketCount, int notableCount, int nodeCount)
    {
        var large = baseType.StartsWith("Large", StringComparison.OrdinalIgnoreCase);
        var medium = baseType.StartsWith("Medium", StringComparison.OrdinalIgnoreCase);
        var socketAt = new HashSet<int>(large && socketCount == 1 ? [6] : layout.Socket.Take(socketCount));
        var list = new List<int>();
        foreach (var raw in layout.Notable)
        {
            if (list.Count == notableCount) break;
            var i = raw;
            if (medium)
            {
                if (socketCount == 0 && notableCount == 2) i = i switch { 6 => 4, 10 => 8, _ => i };
                else if (nodeCount == 4) i = i switch { 10 => 9, 2 => 3, _ => i };
            }
            if (!socketAt.Contains(i)) list.Add(i);
        }
        list.Sort();
        return list;
    }

    [GeneratedRegex(@"^Adds (\d+) Passive Skills?$", RegexOptions.IgnoreCase)] private static partial Regex AddsRx();
    [GeneratedRegex(@"^(\d+) Added Passive Skills? (?:are|is an?) Jewel Sockets?$", RegexOptions.IgnoreCase)] private static partial Regex SocketsRx();
    [GeneratedRegex(@"^Adds (\d+) Small Passive Skills? which grant nothing$", RegexOptions.IgnoreCase)] private static partial Regex NothingRx();
    [GeneratedRegex(@"^1 Added Passive Skill is (.+)$", RegexOptions.IgnoreCase)] private static partial Regex NotableRx();
}
