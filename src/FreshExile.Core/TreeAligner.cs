namespace FreshExile.Core;

/// <summary>screen_px = world · Scale + Offset</summary>
public readonly record struct AlignResult(double Scale, double OffsetX, double OffsetY, double Score, int Points);

/// <summary>
/// Finds the in-game passive tree on a screenshot: allocated paths are drawn in pale gold,
/// so the known allocated edges are matched against gold pixels (Hough-style offset voting, then a fine search).
/// </summary>
public sealed class TreeAligner
{
    const int Cell = 4;
    public const double MinScale = 0.06, MaxScale = 0.7;

    readonly List<(double X, double Y)> _points;
    readonly List<(double X, double Y)> _sample;
    /// <summary>What a candidate fit is scored on: every point, or about a thousand of a big structure template (the sweep
    /// tries thousands of fits; the final refine scores them all).</summary>
    readonly List<(double X, double Y)> _scored;
    bool _refining;
    bool[] _gold = [];
    /// <summary>What the template is matched against: the gold mask, or for a structure template the tree's dim lines.</summary>
    bool[] _match = [];
    readonly bool _structure;
    readonly List<int> _cells = new();
    int[] _acc = [];

    /// <param name="structure">
    /// The template is the tree's own drawing around the class start (<see cref="BuildStructureTemplate"/>), matched against
    /// its dim unallocated lines: a fresh character has nothing gold to go by yet.
    /// </param>
    public TreeAligner(List<(double X, double Y)> template, bool structure = false)
    {
        _points = template;
        _structure = structure;
        // the dim lines of a structure template light up thousands of cells: fewer voters keep the sweep near a second
        var step = Math.Max(1, template.Count / (structure ? 90 : 250));
        _sample = template.Where((_, i) => i % step == 0).ToList();
        var every = Math.Max(1, template.Count / 1000);
        _scored = template.Where((_, i) => i % every == 0).ToList();
    }

    public int PointCount => _points.Count;
    public bool IsStructure => _structure;

    /// <summary>
    /// Sample points along every link drawn around a class start (both ends within <paramref name="radius"/> world units),
    /// the portrait excluded: what the game draws for a character with nothing allocated yet.
    /// </summary>
    public static List<(double X, double Y)> BuildStructureTemplate(PassiveTree tree, int classStartId, double radius = 5000)
    {
        var pts = new List<(double, double)>();
        if (!tree.Nodes.TryGetValue(classStartId, out var start)) return pts;
        bool Near(PassiveNode n) => (n.X - start.X) * (n.X - start.X) + (n.Y - start.Y) * (n.Y - start.Y) < radius * radius;
        foreach (var e in tree.Edges)
        {
            var a = tree.Nodes[e.A];
            var b = tree.Nodes[e.B];
            if (!Near(a) || !Near(b) || a.Kind == NodeKind.ClassStart || b.Kind == NodeKind.ClassStart) continue;
            AddEdgePoints(tree, e, pts);
        }
        DropPortrait(tree, pts);
        return pts;
    }

    /// <summary>An unallocated link: dim and warm on the cold near-black background (about 45, 40, 30 against 8, 12, 17).</summary>
    static bool IsLine(byte r, byte g, byte b) => r >= 28 && r <= 140 && r - b >= 6 && g + 4 >= b && r - g <= 30;

    /// <summary>Sample points along allocated edges (straight and orbit arcs), class start excluded.</summary>
    public static List<(double X, double Y)> BuildTemplate(PassiveTree tree, IEnumerable<int> allocated)
    {
        var set = allocated.ToHashSet();
        var pts = new List<(double, double)>();
        foreach (var e in tree.Edges)
        {
            if (!set.Contains(e.A) || !set.Contains(e.B)) continue;
            if (tree.Nodes[e.A].Kind == NodeKind.ClassStart || tree.Nodes[e.B].Kind == NodeKind.ClassStart) continue;
            AddEdgePoints(tree, e, pts);
        }
        return pts;
    }

    /// <summary>World points along one edge (orbit arc or straight), ends excluded.</summary>
    public static void AddEdgePoints(PassiveTree tree, Edge e, List<(double X, double Y)> pts)
    {
        var a = tree.Nodes[e.A];
        var b = tree.Nodes[e.B];
        if (e.IsArc)
        {
            var a1 = Math.Atan2(a.X - a.GroupX, -(a.Y - a.GroupY));
            var a2 = Math.Atan2(b.X - b.GroupX, -(b.Y - b.GroupY));
            var d = a2 - a1;
            while (d <= -Math.PI) d += 2 * Math.PI;
            while (d > Math.PI) d -= 2 * Math.PI;
            var n = Math.Max(2, (int)(Math.Abs(d) * e.Radius / 25));
            for (var i = 1; i < n; i++)
            {
                var t = a1 + d * i / n;
                pts.Add((a.GroupX + e.Radius * Math.Sin(t), a.GroupY - e.Radius * Math.Cos(t)));
            }
        }
        else
        {
            var len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            var n = Math.Max(2, (int)(len / 25));
            for (var i = 1; i < n; i++) pts.Add((a.X + (b.X - a.X) * i / n, a.Y + (b.Y - a.Y) * i / n));
        }
    }

    /// <summary>
    /// Share of the points that are gold on the last aligned frame (allocated edges are gold in game).
    /// Only the middle of the edge counts: the ends sit under node icons. -1 = too little on screen.
    /// </summary>
    /// <summary>
    /// World units around a class start where the class portrait is painted: its hair, skin and gold frame read as "gold"
    /// on screen, so links there say nothing about what is allocated.
    /// </summary>
    public const double PortraitRadius = 1400;

    /// <summary>Drops the points of a link that lie on a class portrait (see <see cref="PortraitRadius"/>).</summary>
    public static void DropPortrait(PassiveTree tree, List<(double X, double Y)> pts)
    {
        foreach (var n in tree.Nodes.Values)
        {
            if (n.Kind != NodeKind.ClassStart) continue;
            pts.RemoveAll(p => (p.X - n.X) * (p.X - n.X) + (p.Y - n.Y) * (p.Y - n.Y) < PortraitRadius * PortraitRadius);
        }
    }

    public double GoldRatio(List<(double X, double Y)> world, AlignResult r, int w, int h)
    {
        if (_gold.Length != w * h) return -1;
        int hit = 0, inside = 0;
        var skip = world.Count / 5;
        for (var k = skip; k < world.Count - skip; k++)
        {
            var x = (int)(world[k].X * r.Scale + r.OffsetX);
            var y = (int)(world[k].Y * r.Scale + r.OffsetY);
            if (x < 2 || y < 2 || x >= w - 2 || y >= h - 2) continue;
            inside++;
            var i = y * w + x;
            if (_gold[i] || _gold[i - 1] || _gold[i + 1] || _gold[i - w] || _gold[i + w]
                || _gold[i - 2] || _gold[i + 2] || _gold[i - 2 * w] || _gold[i + 2 * w]) hit++;
        }
        return inside < 2 ? -1 : (double)hit / inside;
    }

    /// <summary>
    /// Plan nodes already taken on screen: a plan node counts as taken when an edge from it to
    /// something taken is gold. Walks in plan order and repeats so chains are picked up.
    /// </summary>
    public HashSet<int> DetectTaken(PassiveTree tree, PlanResult plan, IReadOnlyDictionary<int, List<Edge>> edgesByNode,
        AlignResult r, int w, int h)
    {
        var taken = new HashSet<int>(plan.Have);
        foreach (var n in tree.Nodes.Values)
            if (n.Kind == NodeKind.ClassStart) taken.Add(n.Id);
        var found = new HashSet<int>();
        // the plan's nodes next to a class start: their own link runs under the portrait and is never read
        var byStart = new HashSet<int>(plan.Order.Where(id => tree.Nodes[id].Links.Any(l => tree.Nodes.TryGetValue(l, out var s) && s.Kind == NodeKind.ClassStart)));
        var pts = new List<(double X, double Y)>();
        bool changed;
        do
        {
            changed = false;
            foreach (var id in plan.Order)
            {
                if (found.Contains(id) || !edgesByNode.TryGetValue(id, out var edges)) continue;
                foreach (var e in edges)
                {
                    var other = e.A == id ? e.B : e.A;
                    // a link is gold only when both its ends are taken: so the first node by the class start, whose own
                    // link is lost under the portrait, is seen taken through its link to the next one
                    var otherPlan = byStart.Contains(other) && !found.Contains(other);
                    if (!taken.Contains(other) && !found.Contains(other) && !otherPlan) continue;
                    pts.Clear();
                    AddEdgePoints(tree, e, pts);
                    DropPortrait(tree, pts);
                    if (GoldRatio(pts, r, w, h) >= 0.6)
                    {
                        found.Add(id);
                        if (otherPlan) found.Add(other);
                        changed = true;
                        break;
                    }
                }
            }
        } while (changed);
        return found;
    }

    /// <summary>
    /// Nodes to refund that are already gone on screen: every visible edge to an allocated neighbour
    /// has lost its gold. A node with no such edge in view tells nothing and is left alone.
    /// </summary>
    public HashSet<int> DetectRefunded(PassiveTree tree, PlanResult plan, IReadOnlyDictionary<int, List<Edge>> edgesByNode,
        AlignResult r, int w, int h) =>
        DetectGone(tree, plan.Refund, plan.Have, edgesByNode, r, w, h);

    /// <summary>
    /// Of <paramref name="ids"/>, those whose every visible link to another allocated node has no gold any more:
    /// refunded in game, or seen taken and then cancelled (points spent but not applied).
    /// </summary>
    public HashSet<int> DetectGone(PassiveTree tree, IEnumerable<int> ids, IEnumerable<int> allocatedIds,
        IReadOnlyDictionary<int, List<Edge>> edgesByNode, AlignResult r, int w, int h)
    {
        var allocated = new HashSet<int>(allocatedIds);
        foreach (var n in tree.Nodes.Values)
            if (n.Kind == NodeKind.ClassStart) allocated.Add(n.Id);
        var gone = new HashSet<int>();
        var pts = new List<(double X, double Y)>();
        foreach (var id in ids)
        {
            if (!edgesByNode.TryGetValue(id, out var edges)) continue;
            int seen = 0, gold = 0;
            foreach (var e in edges)
            {
                var other = e.A == id ? e.B : e.A;
                if (!allocated.Contains(other)) continue; // only links between two allocated nodes were gold
                pts.Clear();
                AddEdgePoints(tree, e, pts);
                DropPortrait(tree, pts);
                var ratio = GoldRatio(pts, r, w, h);
                if (ratio < 0) continue;
                seen++;
                if (ratio >= 0.35) gold++;
            }
            if (seen > 0 && gold == 0) gone.Add(id);
        }
        return gone;
    }

    /// <summary>
    /// Of <paramref name="ids"/> (seen refunded earlier), those gold again: a link to another allocated node (or to one of
    /// them) is clearly gold — the respec was cancelled instead of applied.
    /// </summary>
    public HashSet<int> DetectBack(PassiveTree tree, IReadOnlyCollection<int> ids, IEnumerable<int> allocatedIds,
        IReadOnlyDictionary<int, List<Edge>> edgesByNode, AlignResult r, int w, int h)
    {
        var allocated = new HashSet<int>(allocatedIds);
        allocated.UnionWith(ids);
        foreach (var n in tree.Nodes.Values)
            if (n.Kind == NodeKind.ClassStart) allocated.Add(n.Id);
        var back = new HashSet<int>();
        var pts = new List<(double X, double Y)>();
        foreach (var id in ids)
        {
            if (!edgesByNode.TryGetValue(id, out var edges)) continue;
            foreach (var e in edges)
            {
                var other = e.A == id ? e.B : e.A;
                if (!allocated.Contains(other)) continue;
                pts.Clear();
                AddEdgePoints(tree, e, pts);
                DropPortrait(tree, pts);
                if (GoldRatio(pts, r, w, h) >= 0.6) { back.Add(id); break; }
            }
        }
        return back;
    }

    public static Dictionary<int, List<Edge>> EdgesByNode(PassiveTree tree)
    {
        var map = new Dictionary<int, List<Edge>>();
        foreach (var e in tree.Edges)
        {
            if (!map.TryGetValue(e.A, out var la)) map[e.A] = la = new();
            if (!map.TryGetValue(e.B, out var lb)) map[e.B] = lb = new();
            la.Add(e);
            lb.Add(e);
        }
        return map;
    }

    static bool IsGold(byte r, byte g, byte b) => r > 150 && g > 120 && b > 60 && r - b > 40 && r >= g;

    /// <summary>
    /// bgra: 32-bit top-down screenshot. knownScales: zoom levels seen before — each is tried (fast, tracking);
    /// null or empty: sweep all scales (calibration, under a second or two).
    /// </summary>
    public AlignResult? Align(byte[] bgra, int width, int height, int stride, IReadOnlyList<double>? knownScales)
    {
        if (_points.Count < 30) return null;
        BuildMask(bgra, width, height, stride);
        if (_cells.Count == 0) return null;

        if (knownScales is { Count: > 0 })
        {
            AlignResult? best = null;
            var bestRank = double.MinValue;
            foreach (var ks in knownScales)
            {
                var v = Vote([ks], width, height);
                if (v.Votes == 0) continue;
                var r = Refine(v.S, v.Ox, v.Oy, 0.995, 1.005, width, height);
                if (r.Score <= bestRank) continue;   // the rank is never above the score
                var rank = Rank(r.Score, r.Scale, r.OffsetX, r.OffsetY, width, height);
                if (rank > bestRank) { best = r; bestRank = rank; }
            }
            return best;
        }

        // raw vote counts favour small scales (more of the path fits on screen),
        // so every scale's best offset is judged by its hit ratio instead
        (double S, double Ox, double Oy, double Rank) pick = (0, 0, 0, double.MinValue);
        foreach (var s in Sweep(_structure ? 1.04 : 1.025))
            foreach (var (ox, oy) in Peaks(s, width, height, 3))
                for (var dy = -4; dy <= 4; dy += 2)
                    for (var dx = -4; dx <= 4; dx += 2)
                    {
                        var sc = Score(s, ox + dx, oy + dy, width, height).Score;
                        if (sc <= pick.Rank) continue;
                        var rank = Rank(sc, s, ox + dx, oy + dy, width, height);
                        if (rank > pick.Rank) pick = (s, ox + dx, oy + dy, rank);
                    }
        return pick.Rank == double.MinValue ? null : Refine(pick.S, pick.Ox, pick.Oy, 0.97, 1.03, width, height);
    }

    /// <summary>Top offsets (px) by votes for one scale, kept a few cells apart.</summary>
    List<(double Ox, double Oy)> Peaks(double s, int width, int height, int count)
    {
        var cw = width / Cell;
        var ch = height / Cell;
        int ox0 = -3 * cw, oy0 = -3 * ch, aw = 7 * cw;
        Vote([s], width, height);
        var peaks = new List<(int I, int V)>();
        for (var i = 0; i < _acc.Length; i++)
        {
            var v = _acc[i];
            if (v == 0 || (peaks.Count == count && v <= peaks[^1].V)) continue;
            var near = peaks.FindIndex(p => Math.Abs(p.I % aw - i % aw) <= 3 && Math.Abs(p.I / aw - i / aw) <= 3);
            if (near >= 0)
            {
                if (v <= peaks[near].V) continue;
                peaks.RemoveAt(near);
            }
            peaks.Add((i, v));
            peaks.Sort((a, b) => b.V.CompareTo(a.V));
            if (peaks.Count > count) peaks.RemoveAt(count);
        }
        return peaks.Select(p => ((double)(p.I % aw + ox0) * Cell, (double)(p.I / aw + oy0) * Cell)).ToList();
    }

    (double S, double Ox, double Oy, int Votes) Vote(IEnumerable<double> scales, int width, int height)
    {
        var cw = width / Cell;
        var ch = height / Cell;
        int ox0 = -3 * cw, oy0 = -3 * ch, aw = 7 * cw, ah = 7 * ch;
        if (_acc.Length != aw * ah) _acc = new int[aw * ah];

        (double S, double Ox, double Oy, int Votes) best = (0, 0, 0, 0);
        foreach (var s in scales)
        {
            Array.Clear(_acc);
            foreach (var p in _sample)
            {
                var px = p.X * s / Cell;
                var py = p.Y * s / Cell;
                foreach (var c in _cells)
                {
                    var ox = (int)Math.Round(c % cw - px) - ox0;
                    var oy = (int)Math.Round(c / cw - py) - oy0;
                    if ((uint)ox < (uint)aw && (uint)oy < (uint)ah) _acc[oy * aw + ox]++;
                }
            }
            for (var i = 0; i < _acc.Length; i++)
                if (_acc[i] > best.Votes) best = (s, (i % aw + ox0) * Cell, (i / aw + oy0) * Cell, _acc[i]);
        }
        return best;
    }

    AlignResult Refine(double s0, double ox0, double oy0, double lo, double hi, int width, int height)
    {
        _refining = true;
        try { return RefineAll(s0, ox0, oy0, lo, hi, width, height); }
        finally { _refining = false; }
    }

    AlignResult RefineAll(double s0, double ox0, double oy0, double lo, double hi, int width, int height)
    {
        var fine = Score(s0, ox0, oy0, width, height);
        var res = new AlignResult(s0, ox0, oy0, fine.Score, fine.Inside);
        for (var s = s0 * lo; s <= s0 * hi; s *= 1.002)
            for (var dy = -6; dy <= 6; dy++)
                for (var dx = -6; dx <= 6; dx++)
                {
                    var sc = Score(s, ox0 + dx, oy0 + dy, width, height);
                    if (sc.Score > res.Score) res = new AlignResult(s, ox0 + dx, oy0 + dy, sc.Score, sc.Inside);
                }
        return res;
    }

    static IEnumerable<double> Sweep(double step)
    {
        for (var s = MinScale; s <= MaxScale; s *= step) yield return s;
    }

    /// <summary>
    /// 64×8 grey thumbnail of the top-centre strip where the game draws the "Skills" title:
    /// it does not depend on tree zoom or pan, so it tells "tree is open" even when the path is not found.
    /// </summary>
    public static byte[] HeaderPatch(byte[] bgra, int width, int height, int stride)
    {
        const int pw = 64, ph = 8;
        int x0 = width / 3, rw = width / 3, rh = Math.Max(ph, height * 9 / 100);
        var patch = new byte[pw * ph];
        for (var py = 0; py < ph; py++)
            for (var px = 0; px < pw; px++)
            {
                int sum = 0, n = 0;
                for (var y = py * rh / ph; y < (py + 1) * rh / ph; y += 2)
                    for (var x = x0 + px * rw / pw; x < x0 + (px + 1) * rw / pw; x += 2)
                    {
                        var i = y * stride + x * 4;
                        sum += bgra[i] + bgra[i + 1] + bgra[i + 2];
                        n++;
                    }
                patch[py * pw + px] = (byte)(n == 0 ? 0 : sum / (3 * n));
            }
        return patch;
    }

    /// <summary>Mean absolute difference of two header patches (0 = identical).</summary>
    public static double PatchDiff(byte[] a, byte[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 255;
        long d = 0;
        for (var i = 0; i < a.Length; i++) d += Math.Abs(a[i] - b[i]);
        return (double)d / a.Length;
    }

    void BuildMask(byte[] bgra, int w, int h, int stride)
    {
        if (_gold.Length != w * h) _gold = new bool[w * h];
        if (_structure && _match.Length != w * h) _match = new bool[w * h];
        if (!_structure) _match = _gold;
        for (var y = 0; y < h; y++)
        {
            var row = y * stride;
            for (var x = 0; x < w; x++)
            {
                var i = row + x * 4;
                _gold[y * w + x] = IsGold(bgra[i + 2], bgra[i + 1], bgra[i]);
                if (_structure) _match[y * w + x] = IsLine(bgra[i + 2], bgra[i + 1], bgra[i]);
            }
        }

        _cells.Clear();
        var cw = w / Cell;
        var ch = h / Cell;
        for (var cy = 0; cy < ch; cy++)
            for (var cx = 0; cx < cw; cx++)
            {
                var any = false;
                for (var dy = 0; dy < Cell && !any; dy++)
                {
                    var r = (cy * Cell + dy) * w + cx * Cell;
                    for (var dx = 0; dx < Cell && !any; dx++) any = _match[r + dx];
                }
                if (any) _cells.Add(cy * cw + cx);
            }
    }

    /// <summary>
    /// Share of the template that is gold a few pixels off its own lines too (of the last aligned frame). A real path
    /// is thin lines on a dark ground (low); a false fit over a dense patch of gold node frames is gold all around (high).
    /// </summary>
    public double Beside(AlignResult r, int w, int h) => Beside(r.Scale, r.OffsetX, r.OffsetY, w, h);

    /// <summary>
    /// How a fit is ranked against other zooms and offsets: its score minus the gold beside its lines. At a far zoom the
    /// node frames stand close and a short path "fits" a wrong zoom at ~1.0, but there it is gold all around too.
    /// A structure template already counts this in its score.
    /// </summary>
    double Rank(double score, double s, double ox, double oy, int w, int h) =>
        _structure ? score : score - Beside(s, ox, oy, w, h);

    double Beside(double s, double ox, double oy, int w, int h)
    {
        var d = Math.Max(5, (int)Math.Ceiling(s * 45));
        int inside = 0, beside = 0;
        foreach (var p in _points)
        {
            var x = (int)(p.X * s + ox);
            var y = (int)(p.Y * s + oy);
            if (x < d + 1 || y < d + 1 || x >= w - d - 1 || y >= h - d - 1) continue;
            inside++;
            var i = y * w + x;
            var n = (_match[i + d] ? 1 : 0) + (_match[i - d] ? 1 : 0) + (_match[i + d * w] ? 1 : 0) + (_match[i - d * w] ? 1 : 0);
            if (n >= 3) beside++;
        }
        return inside == 0 ? 1 : (double)beside / inside;
    }

    (double Score, int Inside) Score(double s, double ox, double oy, int w, int h)
    {
        int hit = 0, inside = 0, beside = 0;
        foreach (var p in _refining ? _points : _scored)
        {
            var x = (int)(p.X * s + ox);
            var y = (int)(p.Y * s + oy);
            if (x < 7 || y < 7 || x >= w - 7 || y >= h - 7) continue;
            inside++;
            var i = y * w + x;
            if (_match[i] || _match[i - 1] || _match[i + 1] || _match[i - w] || _match[i + w]) hit++;
            // a structure template must also find the dark between the lines: the tree's links are thin, the ground's
            // warm dim textures are everywhere. A point a few pixels off a link lands on the cold background
            if (_structure && (_match[i + 6 + 4 * w] || _match[i - 6 - 4 * w])) beside++;
        }
        if (inside < (_refining || _scored.Count == _points.Count ? 30 : 30 * _scored.Count / _points.Count)) return (0, inside);
        return _structure ? (Math.Max(0, (double)(hit - beside) / inside), inside) : ((double)hit / inside, inside);
    }
}
