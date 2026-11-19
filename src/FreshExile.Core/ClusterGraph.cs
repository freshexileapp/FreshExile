using System.Globalization;
using System.Text.RegularExpressions;

namespace FreshExile.Core;

/// <summary>
/// The passives a socketed cluster jewel adds to the tree, laid out the way PoB's BuildSubgraph does
/// (Classes/PassiveSpec.lua): the cluster's ring sits in the socket's proxy group, on the orbit of its size, turned by
/// the proxy's offset (Data/ClusterJewels.lua orbitOffsets); sockets of smaller clusters move onto the ring.
/// The game draws the cluster in the same place, so the overlay on the game's tree lines up with it too.
/// </summary>
public static partial class ClusterGraph
{
    sealed record Template(int SizeIndex, int Total, int[] Small, int[] Notable, int[] Socket);

    static readonly Dictionary<string, Template> Templates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Small Cluster Jewel"] = new(0, 6, [0, 4, 2], [4], [4]),
        ["Medium Cluster Jewel"] = new(1, 12, [0, 6, 8, 4, 10, 2], [6, 10, 2, 0], [6]),
        ["Large Cluster Jewel"] = new(2, 12, [0, 4, 6, 8, 10, 2, 7, 5, 9, 3, 11, 1], [6, 4, 8, 10, 2], [4, 8, 6]),
    };

    /// <summary>A small passive's stat with its numbers as # → its icon (Data/ClusterIcons.tsv, from PoB).</summary>
    static readonly Lazy<Dictionary<string, string>> Icons = new(() =>
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var s = typeof(ClusterGraph).Assembly.GetManifestResourceStream("ClusterIcons.tsv");
        if (s == null) return map;
        using var r = new StreamReader(s);
        while (r.ReadLine() is { } line)
            if (line.Split('\t') is [var stat, var icon]) map.TryAdd(stat, icon);
        return map;
    });

    /// <summary>
    /// The icon the game gives a cluster's small passives, found by what they grant ("10% increased Area Damage");
    /// empty for passives that grant nothing or a stat no enchant has.
    /// </summary>
    public static string SmallIcon(IEnumerable<string> stats)
    {
        foreach (var stat in stats)
            if (Icons.Value.TryGetValue(NumberRx().Replace(stat, "#"), out var icon)) return icon;
        return "";
    }

    /// <summary>Proxy node → the ring's turn per cluster size (Data/ClusterOrbitOffsets.tsv, from PoB).</summary>
    static readonly Lazy<Dictionary<int, int[]>> Offsets = new(() =>
    {
        var map = new Dictionary<int, int[]>();
        using var s = typeof(ClusterGraph).Assembly.GetManifestResourceStream("ClusterOrbitOffsets.tsv");
        if (s == null) return map;
        using var r = new StreamReader(s);
        while (r.ReadLine() is { } line)
        {
            var p = line.Split('\t');
            if (p.Length == 4 && int.TryParse(p[0], out var id))
                map[id] = p.Skip(1).Select(x => int.TryParse(x, out var v) ? v : 0).ToArray();
        }
        return map;
    });

    /// <summary>One generated passive: its id, what it is, and its place on the cluster's ring.</summary>
    public sealed record ClusterNode(int Id, NodeKind Kind, string Name, string[] Stats, int Index, int OrbitIndex);

    /// <summary>A cluster's passives in a socket: the nodes, the orbit they sit on, the proxy group's centre.</summary>
    public sealed record Subgraph(IReadOnlyList<ClusterNode> Nodes, int Orbit, double CenterX, double CenterY, int Group, int EntranceId, bool Closed);

    /// <summary>Null when the item is no cluster jewel, or the tree data lacks the socket's proxy.</summary>
    public static Subgraph? Build(PobItem item, PassiveNode socket, PassiveTree tree)
    {
        if (!Templates.TryGetValue(item.BaseType, out var t) || socket.ExpansionProxy is not int proxyId
            || !tree.Nodes.TryGetValue(proxyId, out var proxy)) return null;

        // the subgraph's id: this socket's index and those of the bigger clusters around it (as ClusterJewel.Allocation)
        var id = 0x10000;
        for (PassiveNode? s = socket; s?.ExpansionSize is int size; s = s.ExpansionParent is int p && tree.Nodes.TryGetValue(p, out var pn) ? pn : null)
            id += size switch { 2 => s.ExpansionIndex << 6, 1 => s.ExpansionIndex << 9, _ => 0 };
        var baseId = id + (t.SizeIndex << 4);

        int? declared = null;
        var socketCount = 0;
        var nothing = 0;
        var names = new List<string>();
        string? grant = null;
        var also = new List<string>();
        foreach (var m in item.Mods)
        {
            var text = m.Text;
            Match x;
            if ((x = AddsRx().Match(text)).Success) declared = int.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture);
            else if ((x = SocketsRx().Match(text)).Success) socketCount = int.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture);
            else if ((x = NothingRx().Match(text)).Success) nothing = int.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture);
            else if ((x = NotableRx().Match(text)).Success) names.Add(x.Groups[1].Value.Trim());
            else if ((x = GrantRx().Match(text)).Success) grant = x.Groups[1].Value.Trim();
            else if ((x = AlsoRx().Match(text)).Success) also.Add(x.Groups[1].Value.Trim());
        }
        names = ClusterJewel.SortedNotables(names);
        var nodeCount = declared ?? socketCount + names.Count + nothing;
        var smallCount = nodeCount - socketCount - names.Count;
        var large = t.SizeIndex == 2;
        var medium = t.SizeIndex == 1;

        var at = new SortedDictionary<int, ClusterNode>();
        // sockets keep the tree's own nodes of the proxy group, by their expansion index
        var proxyGroup = tree.Nodes.Values.Where(n => n.Group == proxy.Group && n.ExpansionSize != null).ToList();
        void Socket(int index, int jewelIndex)
        {
            if (proxyGroup.FirstOrDefault(n => n.ExpansionIndex == jewelIndex) is { } s)
                at[index] = new ClusterNode(s.Id, NodeKind.Jewel, s.Name, [], index, 0);
        }
        if (large && socketCount == 1) Socket(6, 1);
        else
        {
            int[] jewels = [0, 2, 1];
            for (var i = 0; i < Math.Min(socketCount, t.Socket.Length); i++) Socket(t.Socket[i], jewels[i]);
        }

        var notableAt = ClusterJewel.NotableIndexesFor(item.BaseType, socketCount, names.Count, nodeCount);
        for (var i = 0; i < names.Count && i < notableAt.Count; i++)
        {
            var base_ = tree.ClusterNotable(names[i]);
            at[notableAt[i]] = new ClusterNode(baseId + notableAt[i], NodeKind.Notable, names[i], base_?.Stats ?? [], notableAt[i], 0);
        }

        // the small passives: PoB's fill order, with its special places on a medium cluster
        var smallName = grant ?? Loc.T("Малая пассивка кластера");
        string[] smallStats = [.. grant != null ? [grant] : Array.Empty<string>(), .. also];
        var placed = 0;
        foreach (var raw in t.Small)
        {
            if (placed == smallCount) break;
            var i = raw;
            if (medium && nodeCount == 5 && i == 4) i = 3;
            else if (medium && nodeCount == 4) i = i switch { 8 => 9, 4 => 3, _ => i };
            if (at.ContainsKey(i)) continue;
            at[i] = new ClusterNode(baseId + i, NodeKind.Normal, smallName, smallStats, i, 0);
            placed++;
        }
        if (!at.ContainsKey(0)) return null;   // no entrance: not a cluster PoB could build either

        // the ring's places in the tree's orbit: turned by the proxy's offset, then 12 places spread over the orbit's 16
        var orbit = t.SizeIndex + 1;
        var perOrbit = orbit < tree.SkillsPerOrbit.Length ? tree.SkillsPerOrbit[orbit] : t.Total;
        var start = Offsets.Value.TryGetValue(proxy.Id, out var off) && t.SizeIndex < off.Length ? off[t.SizeIndex] : 0;
        var nodes = at.Values.Select(n => n with { OrbitIndex = Translate((n.Index + start) % t.Total, t.Total, perOrbit) }).ToList();
        return new Subgraph(nodes, orbit, proxy.GroupX, proxy.GroupY, proxy.Group, at[0].Id, Closed: t.SizeIndex != 0);
    }

    /// <summary>PoB's TranslateClusterOrbitIndex: a place on a ring of one size to the same place on another.</summary>
    static int Translate(int index, int from, int to) => (from, to) switch
    {
        _ when from == to => index,
        (12, 16) => new[] { 0, 1, 3, 4, 5, 7, 8, 9, 11, 12, 13, 15 }[index],
        (6, 16) => new[] { 0, 3, 5, 8, 11, 13 }[index],
        _ => index * to / from,
    };

    [GeneratedRegex(@"[\d.]+")] private static partial Regex NumberRx();
    [GeneratedRegex(@"^Adds (\d+) Passive Skills?$", RegexOptions.IgnoreCase)] private static partial Regex AddsRx();
    [GeneratedRegex(@"^(\d+) Added Passive Skills? (?:are|is an?) Jewel Sockets?$", RegexOptions.IgnoreCase)] private static partial Regex SocketsRx();
    [GeneratedRegex(@"^Adds (\d+) Small Passive Skills? which grant nothing$", RegexOptions.IgnoreCase)] private static partial Regex NothingRx();
    [GeneratedRegex(@"^1 Added Passive Skill is (.+)$", RegexOptions.IgnoreCase)] private static partial Regex NotableRx();
    [GeneratedRegex(@"^Added Small Passive Skills grant: (.+)$", RegexOptions.IgnoreCase)] private static partial Regex GrantRx();
    [GeneratedRegex(@"^Added Small Passive Skills also grant: (.+)$", RegexOptions.IgnoreCase)] private static partial Regex AlsoRx();
}
