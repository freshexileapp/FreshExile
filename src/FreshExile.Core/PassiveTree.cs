using System.Text.Json;

namespace FreshExile.Core;

public enum NodeKind { Normal, Notable, Keystone, Mastery, Jewel, ClassStart }

public sealed class PassiveNode
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public NodeKind Kind { get; init; }
    public string[] Stats { get; init; } = [];
    public string? Ascendancy { get; init; }
    public bool IsAscendancyStart { get; init; }
    public int? ClassStartIndex { get; init; }
    public int Group { get; init; } = -1;
    public int Orbit { get; init; }
    public int OrbitIndex { get; init; }
    public double X { get; set; }
    public double Y { get; set; }
    public double GroupX { get; set; }
    public double GroupY { get; set; }
    public bool Drawable { get; set; }
    public int[] Out { get; init; } = [];
    public int[] Links { get; set; } = [];
    public IReadOnlyDictionary<int, string[]> MasteryEffects { get; init; } = new Dictionary<int, string[]>();

    /// <summary>A cluster jewel socket: 2 large, 1 medium, 0 small; null for any other node.</summary>
    public int? ExpansionSize { get; init; }
    /// <summary>The socket's number among its size, which PoB puts into the ids of the cluster's nodes.</summary>
    public int ExpansionIndex { get; init; }
    /// <summary>The socket of the bigger cluster this one sits in; null on the main tree.</summary>
    public int? ExpansionParent { get; init; }
    /// <summary>The socket's proxy node: its group is where a socketed cluster's ring is drawn.</summary>
    public int? ExpansionProxy { get; init; }
    /// <summary>A passive of a socketed cluster jewel (or a socket moved onto its ring), not of the export itself.</summary>
    public bool IsClusterNode { get; init; }
    /// <summary>The three Blight oils that anoint this notable ("ClearOil", "AmberOil", "CrimsonOil"); empty for the rest.</summary>
    public string[] Recipe { get; init; } = [];

    public bool IsKey => Kind is NodeKind.Notable or NodeKind.Keystone or NodeKind.Jewel or NodeKind.Mastery;
}

public sealed record Edge(int A, int B, bool IsArc, double Radius, bool Clockwise);

public sealed partial class PassiveTree
{
    static readonly double[] Angles16 = [0, 30, 45, 60, 90, 120, 135, 150, 180, 210, 225, 240, 270, 300, 315, 330];
    static readonly double[] Angles40 =
    [
        0, 10, 20, 30, 40, 45, 50, 60, 70, 80, 90, 100, 110, 120, 130, 135, 140, 150, 160, 170,
        180, 190, 200, 210, 220, 225, 230, 240, 250, 260, 270, 280, 290, 300, 310, 315, 320, 330, 340, 350,
    ];

    public Dictionary<int, PassiveNode> Nodes { get; } = new();
    public List<Edge> Edges { get; } = new();
    public Dictionary<int, List<int>> MasteriesByGroup { get; } = new();
    public double MinX { get; private set; }
    public double MinY { get; private set; }
    public double MaxX { get; private set; }
    public double MaxY { get; private set; }

    public PassiveNode? ClassStart(int classId) =>
        Nodes.Values.FirstOrDefault(n => n.ClassStartIndex == classId);

    public static PassiveTree Load(Stream json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var tree = new PassiveTree();

        var consts = r.GetProperty("constants");
        var perOrbit = consts.GetProperty("skillsPerOrbit").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        var radii = consts.GetProperty("orbitRadii").EnumerateArray().Select(e => e.GetDouble()).ToArray();

        var groups = new Dictionary<int, (double X, double Y, bool Proxy)>();
        foreach (var g in r.GetProperty("groups").EnumerateObject())
        {
            if (!int.TryParse(g.Name, out var gid)) continue;
            var v = g.Value;
            groups[gid] = (v.GetProperty("x").GetDouble(), v.GetProperty("y").GetDouble(), Bool(v, "isProxy"));
        }

        foreach (var p in r.GetProperty("nodes").EnumerateObject())
        {
            if (!int.TryParse(p.Name, out var id)) continue;
            var n = p.Value;

            int? classStart = n.TryGetProperty("classStartIndex", out var cs) && cs.ValueKind == JsonValueKind.Number ? cs.GetInt32() : null;
            var kind = classStart != null ? NodeKind.ClassStart
                : Bool(n, "isKeystone") ? NodeKind.Keystone
                : Bool(n, "isMastery") ? NodeKind.Mastery
                : Bool(n, "isJewelSocket") ? NodeKind.Jewel
                : Bool(n, "isNotable") ? NodeKind.Notable
                : NodeKind.Normal;

            var mastery = new Dictionary<int, string[]>();
            if (n.TryGetProperty("masteryEffects", out var me) && me.ValueKind == JsonValueKind.Array)
                foreach (var eff in me.EnumerateArray())
                    if (eff.TryGetProperty("effect", out var ef) && ef.ValueKind == JsonValueKind.Number)
                        mastery[ef.GetInt32()] = Strings(eff, "stats");

            var node = new PassiveNode
            {
                Id = id,
                Name = Str(n, "name") ?? "",
                Kind = kind,
                Stats = Strings(n, "stats"),
                Recipe = Strings(n, "recipe"),
                Ascendancy = Str(n, "ascendancyName"),
                IsAscendancyStart = Bool(n, "isAscendancyStart"),
                ClassStartIndex = classStart,
                Group = n.TryGetProperty("group", out var gr) && gr.ValueKind == JsonValueKind.Number ? gr.GetInt32() : -1,
                Orbit = Int(n, "orbit"),
                OrbitIndex = Int(n, "orbitIndex"),
                Out = Ints(n, "out"),
                MasteryEffects = mastery,
                ExpansionSize = n.TryGetProperty("expansionJewel", out var ej) && ej.ValueKind == JsonValueKind.Object ? Int(ej, "size") : null,
                ExpansionIndex = ej.ValueKind == JsonValueKind.Object ? Int(ej, "index") : 0,
                ExpansionParent = ej.ValueKind == JsonValueKind.Object && IntText(ej, "parent") is int par ? par : null,
                ExpansionProxy = ej.ValueKind == JsonValueKind.Object && IntText(ej, "proxy") is int prx ? prx : null,
            };
            var inIds = Ints(n, "in");
            node.Links = node.Out.Concat(inIds).Distinct().ToArray();

            if (groups.TryGetValue(node.Group, out var grp))
            {
                var radius = node.Orbit < radii.Length ? radii[node.Orbit] : 0;
                var count = node.Orbit < perOrbit.Length ? perOrbit[node.Orbit] : 1;
                var a = OrbitAngle(count, node.OrbitIndex) * Math.PI / 180;
                node.GroupX = grp.X;
                node.GroupY = grp.Y;
                node.X = grp.X + radius * Math.Sin(a);
                node.Y = grp.Y - radius * Math.Cos(a);
                node.Drawable = !grp.Proxy && !Bool(n, "isProxy") && node.Ascendancy == null;
            }

            tree.Nodes[id] = node;
            // a cluster notable has no group in the export: kept by name, a socketed cluster takes its stats from it
            if (node.Kind == NodeKind.Notable && node.Group < 0 && node.Name.Length > 0) tree._clusterNotables.TryAdd(node.Name, node);
            if (node.Kind == NodeKind.Mastery && node.Group >= 0)
            {
                if (!tree.MasteriesByGroup.TryGetValue(node.Group, out var list)) tree.MasteriesByGroup[node.Group] = list = new();
                list.Add(id);
            }
        }

        tree.SkillsPerOrbit = perOrbit;
        tree.OrbitRadii = radii;
        tree.BuildEdges(radii);
        return tree;
    }

    void BuildEdges(double[] radii)
    {
        var seen = new HashSet<long>();
        foreach (var a in Nodes.Values)
        {
            if (!a.Drawable || a.Kind == NodeKind.Mastery) continue;
            foreach (var bid in a.Out)
            {
                if (!Nodes.TryGetValue(bid, out var b) || !b.Drawable || b.Kind == NodeKind.Mastery) continue;
                var key = ((long)Math.Min(a.Id, b.Id) << 32) | (uint)Math.Max(a.Id, b.Id);
                if (!seen.Add(key)) continue;

                if (a.Group == b.Group && a.Orbit == b.Orbit && a.Orbit > 0)
                {
                    var angA = Math.Atan2(a.X - GroupX(a), -(a.Y - GroupY(a)));
                    var angB = Math.Atan2(b.X - GroupX(b), -(b.Y - GroupY(b)));
                    var d = angB - angA;
                    while (d <= -Math.PI) d += 2 * Math.PI;
                    while (d > Math.PI) d -= 2 * Math.PI;
                    Edges.Add(new Edge(a.Id, b.Id, true, radii[a.Orbit], d > 0));
                }
                else Edges.Add(new Edge(a.Id, b.Id, false, 0, false));
            }
        }

        var drawn = Nodes.Values.Where(n => n.Drawable).ToList();
        if (drawn.Count == 0) return;
        MinX = drawn.Min(n => n.X); MaxX = drawn.Max(n => n.X);
        MinY = drawn.Min(n => n.Y); MaxY = drawn.Max(n => n.Y);
    }

    static double GroupX(PassiveNode n) => n.GroupX;
    static double GroupY(PassiveNode n) => n.GroupY;

    public static double OrbitAngle(int count, int index) => count switch
    {
        16 when index < 16 => Angles16[index],
        40 when index < 40 => Angles40[index],
        _ => count <= 0 ? 0 : 360.0 * index / count,
    };

    /// <summary>Neighbours used for pathing; a notable also opens its cluster's mastery.</summary>
    public IEnumerable<int> Neighbours(PassiveNode n)
    {
        foreach (var l in n.Links) yield return l;
        if (n.Kind == NodeKind.Notable && MasteriesByGroup.TryGetValue(n.Group, out var ms))
            foreach (var m in ms) yield return m;
    }

    static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    /// <summary>A number the export writes either as a number or as text ("parent": "21984").</summary>
    static int? IntText(JsonElement e, string name) =>
        !e.TryGetProperty(name, out var v) ? null
        : v.ValueKind == JsonValueKind.Number ? v.GetInt32()
        : v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var x) ? x
        : null;

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static string[] Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => PlainText(x.GetString()!)).ToArray()
            : [];

    /// <summary>
    /// The game's link markup as the player sees it: "[Poison|Poisons]" → "Poisons", "[Blind]" → "Blind",
    /// "[Envy::25|Envy]" → "Envy".
    /// </summary>
    public static string PlainText(string s) => s.Contains('[') ? LinkRx.Replace(s, m => m.Groups[2].Success ? m.Groups[2].Value : m.Groups[1].Value) : s;

    static readonly System.Text.RegularExpressions.Regex LinkRx = new(@"\[([^\]|]*)(?:\|([^\]]*))?\]", System.Text.RegularExpressions.RegexOptions.Compiled);

    static int[] Ints(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetInt32()
                : int.TryParse(x.GetString(), out var i) ? i : -1).Where(i => i >= 0).ToArray()
            : [];
}
