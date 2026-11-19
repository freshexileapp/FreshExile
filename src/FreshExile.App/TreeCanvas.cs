using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FreshExile.Core;

namespace FreshExile.App;

/// <summary>
/// Passive tree renderer. Geometry is built once per plan in world units;
/// pan/zoom only change the transform, pen widths are divided by scale so lines keep their pixel width.
/// </summary>
public sealed class TreeCanvas : FrameworkElement
{
    const double MinScale = 0.012, MaxScale = 1.2;

    static readonly Brush BaseNode = Frozen(Color.FromRgb(0x2E, 0x2E, 0x33));
    static readonly Brush BaseMastery = Frozen(Color.FromRgb(0x1D, 0x1D, 0x21));
    static readonly Brush HaveNode = Frozen(Color.FromRgb(0x9C, 0x9C, 0xA3));
    static readonly Brush PlanNode = Frozen(Colors.White);
    static readonly Brush PlanHalo = Frozen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
    static readonly Brush RefundFill = Frozen(Color.FromRgb(0x1C, 0x10, 0x11));
    static readonly Brush BadgeFill = Frozen(Color.FromArgb(0xF0, 0x0B, 0x0B, 0x0C));
    static readonly Brush LabelFill = Frozen(Color.FromArgb(0xD8, 0x0B, 0x0B, 0x0C));
    static readonly Color BaseEdge = Color.FromRgb(0x26, 0x26, 0x2A);
    static readonly Color HaveEdge = Color.FromRgb(0x86, 0x86, 0x8E);
    static readonly Color PlanGlow = Color.FromArgb(0x2C, 0xFF, 0xFF, 0xFF);
    /// <summary>Respec between stages: nodes and links of the previous stage that this stage drops.</summary>
    static readonly Color RefundEdge = Color.FromRgb(0xF0, 0x4A, 0x4A);
    static readonly Color RefundBright = Color.FromRgb(0xFF, 0x2E, 0x2E);
    static readonly Color StartRing = Color.FromRgb(0x4A, 0x4A, 0x50);
    /// <summary>A cluster jewel the plan needs: its card on the game's tree.</summary>
    static readonly Color ClusterColor = Color.FromRgb(0xC2, 0x96, 0xFF);
    static readonly Typeface Face = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    PassiveTree? _tree;
    PlanResult? _plan;
    readonly List<PassiveNode> _hoverable = new();
    readonly Dictionary<int, int> _step = new();
    readonly Dictionary<(string, double), FormattedText> _textCache = new();

    Geometry? _baseEdges, _haveEdges, _planEdges, _refundEdges;
    Geometry? _baseNodes, _baseMastery, _haveNodes, _planNodes, _planHalo, _refundNodes, _refundX, _starts, _planRings;

    /// <summary>
    /// Overlay: everything but the insides of the plan's rings. The path is clipped to it, so a ring stays empty and the
    /// game's own node icon shows through instead of the line crossing it.
    /// </summary>
    Geometry? _outsideRings;

    /// <summary>
    /// Overlay: a cluster jewel the plan takes passives of that the game does not draw yet — nothing of it is allocated,
    /// so it is not in the game's socket. Its passives are drawn in the game's own art and a card says which jewel it is.
    /// </summary>
    sealed record GhostCluster(PobItem Item, Point Center, double Ring, Point Socket, List<(PassiveNode Node, bool Planned)> Nodes);
    readonly List<GhostCluster> _ghosts = new();

    /// <summary>
    /// Overlay: a jewel socket the plan takes and the jewel the stage puts in it. The game shows the socket empty, so a card
    /// says why it is taken; a cluster jewel's socket is told by its <see cref="GhostCluster"/> card instead.
    /// </summary>
    sealed record JewelCard(PobItem Item, Point Socket, int Root);
    readonly List<JewelCard> _cards = new();

    /// <summary>
    /// A main-tree socket and everything grown from it (its cluster, the clusters in that one's sockets): how far it reaches
    /// left and right, in tree units, and which way the tree's rim is. The cards of a bush stand in one column past it.
    /// </summary>
    readonly Dictionary<int, (double MinX, double MaxX, double Outward)> _bushes = new();

    IReadOnlyDictionary<int, PobItem> _jewels = new Dictionary<int, PobItem>();

    /// <summary>On top of the in-game tree: only the stage path, rings instead of filled nodes, view set from outside.</summary>
    public bool OverlayMode { get; init; }

    public static double RingRadius(PassiveNode n) => Radius(n) * 1.9 + 30;

    int? _highlight;

    /// <summary>Plan node picked in the step list; null = the next point to spend gets the focus ring.</summary>
    public int? Highlight
    {
        get => _highlight;
        set
        {
            if (_highlight == value) return;
            _highlight = value;
            InvalidateVisual();
        }
    }

    /// <summary>The node the focus ring / edge arrow points at: the picked one, else step 1.</summary>
    int? FocusId =>
        _highlight is int h && _step.ContainsKey(h) ? h
        : _plan is { Order.Count: > 0 } p ? p.Order[0]
        : null;

    public void SetView(double scale, Vector offset)
    {
        _scale = scale;
        _offset = offset;
        _needFit = false;
        InvalidateVisual();
    }

    double _scale = 0.04;
    Vector _offset;
    bool _needFit = true;
    Point? _dragStart;
    Vector _dragOrigin;
    PassiveNode? _hover;

    public event Action<PassiveNode?, Point>? HoverChanged;

    public TreeCanvas()
    {
        ClipToBounds = true;
        Focusable = false;
        TreeArt.Arrived += InvalidateVisual;
    }

    public static double Radius(PassiveNode n) => n.Kind switch
    {
        NodeKind.Notable => 46,
        NodeKind.Keystone => 66,
        NodeKind.Jewel => 42,
        NodeKind.Mastery => 38,
        _ => 27,
    };

    /// <param name="jewels">The stage's jewels by socket: the overlay cards a socket of the plan with the jewel for it.</param>
    public void SetData(PassiveTree? tree, PlanResult? plan, IReadOnlyDictionary<int, PobItem>? jewels = null)
    {
        var treeChanged = !ReferenceEquals(tree, _tree);
        _tree = tree;
        _plan = plan;
        _jewels = jewels ?? new Dictionary<int, PobItem>();
        Rebuild();
        if (!OverlayMode && (treeChanged || _needFit)) FitToPlan();
        else InvalidateVisual();
    }

    #region geometry

    enum State { Base, Have, Plan, Refund }

    void Rebuild()
    {
        _hoverable.Clear();
        _step.Clear();
        _ghosts.Clear();
        _cards.Clear();
        _bushes.Clear();
        _hover = null;
        if (_tree == null)
        {
            _baseEdges = _haveEdges = _planEdges = _refundEdges = null;
            _baseNodes = _baseMastery = _haveNodes = _planNodes = _planHalo = _refundNodes = _refundX = _starts = _planRings = _outsideRings = null;
            return;
        }

        var have = _plan?.Have ?? new HashSet<int>();
        var target = _plan?.Target ?? new HashSet<int>();
        var refund = _plan?.Refund ?? new HashSet<int>();
        if (_plan != null)
            for (var i = 0; i < _plan.Order.Count; i++) _step[_plan.Order[i]] = i + 1;
        var planClass = ClassOfPlan();

        State StateOf(PassiveNode n)
        {
            if (_plan == null) return State.Base;
            if (_step.ContainsKey(n.Id)) return State.Plan;
            if (refund.Contains(n.Id)) return State.Refund;
            if (n.Kind == NodeKind.ClassStart) return planClass != null && n.ClassStartIndex == planClass ? State.Have : State.Base;
            return have.Contains(n.Id) && target.Contains(n.Id) ? State.Have : State.Base;
        }

        var gBase = new StreamGeometry(); var gHave = new StreamGeometry(); var gPlan = new StreamGeometry(); var gRefund = new StreamGeometry();
        using (var cBase = gBase.Open())
        using (var cHave = gHave.Open())
        using (var cPlan = gPlan.Open())
        using (var cRefund = gRefund.Open())
        {
            foreach (var e in _tree.Edges)
            {
                var a = _tree.Nodes[e.A];
                var b = _tree.Nodes[e.B];
                // on the game's tree the stub from the class start runs over the portrait and says nothing
                if (OverlayMode && (a.Kind == NodeKind.ClassStart || b.Kind == NodeKind.ClassStart)) continue;
                var sa = StateOf(a);
                var sb = StateOf(b);
                StreamGeometryContext ctx;
                if ((sa == State.Plan && sb is State.Plan or State.Have) || (sb == State.Plan && sa == State.Have)) ctx = cPlan;
                else if (sa == State.Have && sb == State.Have) ctx = cHave;
                else if ((sa == State.Refund && sb is State.Refund or State.Have) || (sb == State.Refund && sa == State.Have)) ctx = cRefund;
                else ctx = cBase;

                ctx.BeginFigure(new Point(a.X, a.Y), false, false);
                if (e.IsArc)
                    ctx.ArcTo(new Point(b.X, b.Y), new Size(e.Radius, e.Radius), 0, false,
                        e.Clockwise ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true, false);
                else
                    ctx.LineTo(new Point(b.X, b.Y), true, false);
            }
        }

        var nBase = new StreamGeometry(); var nMastery = new StreamGeometry(); var nHave = new StreamGeometry();
        var nPlan = new StreamGeometry(); var nHalo = new StreamGeometry(); var nRefund = new StreamGeometry();
        var nX = new StreamGeometry(); var nStart = new StreamGeometry(); var nRings = new StreamGeometry();
        using (var cRings = nRings.Open())
        using (var cBase = nBase.Open())
        using (var cMastery = nMastery.Open())
        using (var cHave = nHave.Open())
        using (var cPlan = nPlan.Open())
        using (var cHalo = nHalo.Open())
        using (var cRefund = nRefund.Open())
        using (var cX = nX.Open())
        using (var cStart = nStart.Open())
        {
            foreach (var n in _tree.Nodes.Values)
            {
                if (!n.Drawable) continue;
                if (n.Kind == NodeKind.ClassStart)
                {
                    Circle(cStart, n.X, n.Y, 130, false);
                    if (StateOf(n) == State.Have) Circle(cHave, n.X, n.Y, 40, true);
                    continue;
                }

                _hoverable.Add(n);
                var r = Radius(n);
                switch (StateOf(n))
                {
                    case State.Plan:
                        Circle(cHalo, n.X, n.Y, r * 2.1, true);
                        Circle(cPlan, n.X, n.Y, r, true);
                        Circle(cRings, n.X, n.Y, RingRadius(n), false);
                        break;
                    case State.Have:
                        Circle(cHave, n.X, n.Y, r, true);
                        break;
                    case State.Refund:
                        Circle(cRefund, n.X, n.Y, r, true);
                        var d = r * 0.55;
                        cX.BeginFigure(new Point(n.X - d, n.Y - d), false, false);
                        cX.LineTo(new Point(n.X + d, n.Y + d), true, false);
                        cX.BeginFigure(new Point(n.X - d, n.Y + d), false, false);
                        cX.LineTo(new Point(n.X + d, n.Y - d), true, false);
                        break;
                    default:
                        Circle(n.Kind == NodeKind.Mastery ? cMastery : cBase, n.X, n.Y, r, true);
                        break;
                }
            }
        }

        foreach (var g in new Geometry[] { gBase, gHave, gPlan, gRefund, nBase, nMastery, nHave, nPlan, nHalo, nRefund, nX, nStart, nRings }) g.Freeze();
        _planRings = nRings;
        var holes = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var n in _tree.Nodes.Values)
            if (n.Drawable && n.Kind != NodeKind.ClassStart && StateOf(n) == State.Plan)
                holes.Children.Add(new EllipseGeometry(new Point(n.X, n.Y), RingRadius(n), RingRadius(n)));
        var world = new RectangleGeometry(new Rect(_tree.MinX - 5000, _tree.MinY - 5000, _tree.MaxX - _tree.MinX + 10000, _tree.MaxY - _tree.MinY + 10000));
        _outsideRings = new CombinedGeometry(GeometryCombineMode.Exclude, world, holes);
        _outsideRings.Freeze();
        (_baseEdges, _haveEdges, _planEdges, _refundEdges) = (gBase, gHave, gPlan, gRefund);

        foreach (var (socketId, item) in _tree.Clusters)
        {
            if (!_tree.Nodes.TryGetValue(socketId, out var socket) || socket.ExpansionProxy is not int px || !_tree.Nodes.TryGetValue(px, out var proxy)) continue;
            var members = _tree.Nodes.Values.Where(n => n.IsClusterNode && n.Drawable && n.Group == proxy.Group).ToList();
            // a passive of it already taken: the jewel sits in the game's socket and the game draws it itself
            if (members.Count == 0 || members.Any(n => StateOf(n) == State.Have) || !members.Any(n => StateOf(n) == State.Plan)) continue;
            var center = new Point(members[0].GroupX, members[0].GroupY);
            var ring = members.Max(n => Math.Sqrt((n.X - center.X) * (n.X - center.X) + (n.Y - center.Y) * (n.Y - center.Y)));
            _ghosts.Add(new GhostCluster(item, center, ring, new Point(socket.X, socket.Y), members.Select(n => (n, StateOf(n) == State.Plan)).ToList()));
            _cards.Add(new JewelCard(item, new Point(socket.X, socket.Y), RootSocket(socketId)));
        }
        // the plan's other sockets with a jewel waiting for them; a cluster whose ring the game already draws needs no card
        foreach (var (socketId, item) in _jewels)
            if (!ClusterJewel.IsCluster(item) && _tree.Nodes.TryGetValue(socketId, out var socket) && socket.Drawable && _step.ContainsKey(socketId))
                _cards.Add(new JewelCard(item, new Point(socket.X, socket.Y), RootSocket(socketId)));

        // each bush's reach: its root socket and every passive grown from it, the game's own rings included
        var bushOfGroup = new Dictionary<int, int>();
        foreach (var socketId in _tree.Clusters.Keys)
            if (_tree.Nodes.TryGetValue(socketId, out var sk) && sk.ExpansionProxy is int px && _tree.Nodes.TryGetValue(px, out var proxy))
                bushOfGroup[proxy.Group] = RootSocket(socketId);
        foreach (var root in _cards.Select(c => c.Root).Distinct())
        {
            var r = _tree.Nodes[root];
            var xs = _tree.Nodes.Values.Where(n => n.IsClusterNode && bushOfGroup.GetValueOrDefault(n.Group, -1) == root).Select(n => n.X).Append(r.X).ToList();
            _bushes[root] = (xs.Min(), xs.Max(), r.X >= 0 ? 1 : -1);
        }
        (_baseNodes, _baseMastery, _haveNodes, _planNodes, _planHalo, _refundNodes, _refundX, _starts) = (nBase, nMastery, nHave, nPlan, nHalo, nRefund, nX, nStart);
    }

    /// <summary>The main-tree socket a cluster socket grows from (itself for a main-tree socket).</summary>
    int RootSocket(int socketId)
    {
        while (_tree!.Nodes.TryGetValue(socketId, out var n) && n.ExpansionParent is int p && _tree.Nodes.ContainsKey(p)) socketId = p;
        return socketId;
    }

    int? ClassOfPlan()
    {
        if (_plan == null || _tree == null) return null;
        foreach (var id in _plan.Target)
            if (_tree.Nodes.TryGetValue(id, out var n) && n.Kind == NodeKind.ClassStart) return n.ClassStartIndex;
        // PoB spec node lists do not always contain the start; find the start adjacent to the plan
        foreach (var id in _plan.Order.Take(1).Concat(_plan.Target))
            if (_tree.Nodes.TryGetValue(id, out var n))
                foreach (var l in n.Links)
                    if (_tree.Nodes.TryGetValue(l, out var s) && s.Kind == NodeKind.ClassStart) return s.ClassStartIndex;
        return null;
    }

    static void Circle(StreamGeometryContext c, double x, double y, double r, bool filled)
    {
        c.BeginFigure(new Point(x + r, y), filled, true);
        c.ArcTo(new Point(x - r, y), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
        c.ArcTo(new Point(x + r, y), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
    }

    #endregion

    #region render

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (_tree == null) return;

        var s = _scale;
        var px = 1 / s;
        dc.PushTransform(new MatrixTransform(s, 0, 0, s, _offset.X, _offset.Y));

        if (OverlayMode)
        {
            // dark outline keeps the white path readable over the gold game art
            var outline = Color.FromArgb(0xB0, 0, 0, 0);
            // the path stops at each ring: the inside stays empty for the game's icon
            if (_outsideRings != null) dc.PushClip(_outsideRings);
            dc.DrawGeometry(null, Pen(outline, 5.5 * px), _planEdges);
            dc.DrawGeometry(null, Pen(Colors.White, 2.4 * px), _planEdges);
            if (_outsideRings != null) dc.Pop();
            DrawGhostArt(dc);
            dc.DrawGeometry(null, Pen(outline, 4.5 * px), _planRings);
            dc.DrawGeometry(null, Pen(Colors.White, 2 * px), _planRings);
            // respec: bright red dashes, outlined like the plan path, so they beat the gold links the game still shows as allocated
            dc.DrawGeometry(null, Pen(outline, 6 * px), _refundEdges);
            dc.DrawGeometry(null, Pen(RefundBright, 3 * px, dashed: true), _refundEdges);
            dc.DrawGeometry(null, Pen(outline, 4.5 * px), _refundX);
            dc.DrawGeometry(null, Pen(RefundBright, 2.4 * px), _refundX);
            dc.Pop();
            DrawGhostCards(dc);
            DrawLabels(dc);
            DrawFocus(dc);
            return;
        }

        dc.DrawGeometry(null, Pen(BaseEdge, 1.1 * px), _baseEdges);
        dc.DrawGeometry(null, Pen(HaveEdge, 2.0 * px), _haveEdges);
        dc.DrawGeometry(null, Pen(RefundEdge, 1.8 * px, dashed: true), _refundEdges);
        dc.DrawGeometry(null, Pen(PlanGlow, 10 * px), _planEdges);
        dc.DrawGeometry(null, Pen(Colors.White, 2.6 * px), _planEdges);

        dc.DrawGeometry(null, Pen(StartRing, 1.4 * px), _starts);
        dc.DrawGeometry(BaseMastery, null, _baseMastery);
        dc.DrawGeometry(BaseNode, null, _baseNodes);
        dc.DrawGeometry(HaveNode, null, _haveNodes);
        dc.DrawGeometry(RefundFill, Pen(RefundEdge, 1.5 * px), _refundNodes);
        dc.DrawGeometry(null, Pen(RefundEdge, 1.5 * px), _refundX);
        dc.DrawGeometry(PlanHalo, null, _planHalo);
        dc.DrawGeometry(PlanNode, null, _planNodes);

        if (_hover != null)
            dc.DrawEllipse(null, Pen(Colors.White, 1.5 * px), new Point(_hover.X, _hover.Y), Radius(_hover) + 7 * px, Radius(_hover) + 7 * px);

        dc.Pop();
        DrawLabels(dc);
        DrawFocus(dc);
    }

    /// <summary>
    /// The passives of a cluster the game does not show yet, in the game's own art (frame and icon, at the size the game
    /// draws them): the ones to take now full, the rest of the jewel faint. World coordinates.
    /// </summary>
    void DrawGhostArt(DrawingContext dc)
    {
        if (_tree == null) return;
        foreach (var g in _ghosts)
            foreach (var (n, planned) in g.Nodes)
            {
                if (!planned) dc.PushOpacity(0.45);
                var (sheet, frame) = n.Kind switch
                {
                    NodeKind.Notable => ("notableActive", "NotableFrameUnallocated"),
                    NodeKind.Jewel => ("", "JewelFrameUnallocated"),
                    _ => ("normalActive", "PSSkillFrame"),
                };
                if (sheet.Length > 0 && n.Icon.Length > 0) DrawSprite(dc, sheet, n.Icon, n.X, n.Y);
                DrawSprite(dc, "frame", frame, n.X, n.Y);
                if (!planned) dc.Pop();
            }
    }

    void DrawSprite(DrawingContext dc, string sheetName, string name, double x, double y)
    {
        if (_tree == null || !_tree.Sprites.TryGetValue(sheetName, out var sheet) || !sheet.Coords.TryGetValue(name, out var r)) return;
        if (TreeArt.Sprite(sheet, name) is not { } img) return;
        var w = r.W / sheet.Zoom;
        var h = r.H / sheet.Zoom;
        dc.DrawImage(img, new Rect(x - w / 2, y - h / 2, w, h));
    }

    bool _clusterCardFull;

    /// <summary>Settings: the cluster card lists every mod that makes the passives; off = the jewel and its notables only.</summary>
    public bool ClusterCardFull
    {
        get => _clusterCardFull;
        set
        {
            if (_clusterCardFull == value) return;
            _clusterCardFull = value;
            InvalidateVisual();
        }
    }

    /// <summary>
    /// A card per jewel the plan's sockets wait for: the jewel and, for a cluster, its notables — or, with
    /// <see cref="ClusterCardFull"/>, every mod of it. The cards of one bush (a socket and the clusters grown from it) stand
    /// in a column past its outer edge, where the game draws nothing, each level with its socket, a dashed line running to
    /// the socket over whatever lies between. Screen coordinates.
    /// </summary>
    void DrawGhostCards(DrawingContext dc)
    {
        if (_cards.Count == 0 || _scale < 0.028) return;
        var s = _scale;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var linkEdge = Pen(Color.FromArgb(0xB0, 0, 0, 0), 3.5);
        var margin = RingRadius(new PassiveNode { Kind = NodeKind.Notable }) * s + 20;
        var socketR = RingRadius(new PassiveNode { Kind = NodeKind.Jewel }) * s;
        var placed = new List<Rect>();

        foreach (var bush in _cards.GroupBy(c => c.Root))
        {
            var (minX, maxX, outward) = _bushes[bush.Key];
            var made = bush.Select(card => (card, lines: CardLines(card.Item, dpi), sock: new Point(card.Socket.X * s + _offset.X, card.Socket.Y * s + _offset.Y)))
                .OrderBy(m => m.sock.Y).ToList();
            var colW = made.Max(m => m.lines.Max(t => t.Width) + 16);
            // the rim side if the column fits on screen there, else the other side
            var right = maxX * s + _offset.X + margin;
            var left = minX * s + _offset.X - margin - colW;
            var x = outward > 0 ? (right + colW <= RenderSize.Width - 4 || left < 4 ? right : left)
                                : (left >= 4 || right + colW > RenderSize.Width - 4 ? left : right);
            x = Math.Clamp(x, 4, Math.Max(4, RenderSize.Width - colW - 4));
            var onRight = x > made[0].sock.X;

            foreach (var (card, lines, sock) in made)
            {
                var color = JewelColor(card.Item);
                var w = lines.Max(t => t.Width) + 16;
                var h = lines.Sum(t => t.Height) + 12 + (lines.Count > 1 ? 2 : 0);
                // level with the socket; under the card above when they would overlap
                var box = new Rect(onRight ? x : x + colW - w, sock.Y - h / 2, w, h);
                for (var i = 0; i < 8 && placed.Any(box.IntersectsWith); i++) box.Y = placed.Where(box.IntersectsWith).Max(r => r.Bottom) + 6;
                box.Y = Math.Clamp(box.Y, 4, Math.Max(4, RenderSize.Height - h - 4));
                placed.Add(box);

                var to = new Point(onRight ? box.Left : box.Right, Math.Clamp(sock.Y, box.Top + 8, box.Bottom - 8));
                var d = to - sock;
                if (d.Length > socketR + 6)
                {
                    d.Normalize();
                    var from = sock + d * socketR;
                    dc.DrawLine(linkEdge, from, to);
                    dc.DrawLine(Pen(color, 1.5, dashed: true), from, to);
                }

                dc.DrawRoundedRectangle(LabelFill, Pen(color, 1.5), box, 6, 6);
                var y = box.Y + 6;
                foreach (var t in lines)
                {
                    dc.DrawText(t, new Point(box.X + 8, y));
                    y += t.Height + (ReferenceEquals(t, lines[_clusterCardFull ? 1 : 0]) ? 2 : 0);
                }
            }
        }
    }

    List<FormattedText> CardLines(PobItem item, double dpi)
    {
        var lines = new List<FormattedText>();
        if (_clusterCardFull) lines.Add(Text(Loc.T("Вставь в гнездо:"), 10.5, dpi, Color.FromRgb(0xB8, 0xB8, 0xC0)));
        lines.Add(Text(CardTitle(item), 12.5, dpi, JewelColor(item)));
        var body = _clusterCardFull ? (ClusterJewel.IsCluster(item) ? ClusterLines(item) : item.Mods.Select(m => m.Text)).ToList() : MiniLines(item);
        lines.AddRange(body.Take(8).Select(m => Text(m, 11, dpi)));
        return lines;
    }

    /// <summary>A unique by its name; a cluster by its base; another rare jewel by base and name ("Viridian Jewel · Armageddon Heart").</summary>
    static string CardTitle(PobItem item) =>
        item.Rarity == ItemRarity.Unique ? item.Name
        : ClusterJewel.IsCluster(item) || item.Name.Length == 0 || item.Name == item.BaseType ? item.BaseType
        : $"{item.BaseType} · {item.Name}";

    /// <summary>The jewel's colour as the game shows it: a unique's brown-gold, the gem colour of a plain jewel, violet for clusters.</summary>
    static Color JewelColor(PobItem item) =>
        item.Rarity == ItemRarity.Unique ? Color.FromRgb(0xD8, 0x8A, 0x45)
        : item.BaseType.Contains("Viridian", StringComparison.OrdinalIgnoreCase) ? Color.FromRgb(0x72, 0xD6, 0x72)
        : item.BaseType.Contains("Crimson", StringComparison.OrdinalIgnoreCase) ? Color.FromRgb(0xEE, 0x6A, 0x6A)
        : item.BaseType.Contains("Cobalt", StringComparison.OrdinalIgnoreCase) ? Color.FromRgb(0x78, 0x9A, 0xFF)
        : ClusterColor;

    /// <summary>The lines of a cluster jewel that make its passives: "Adds 8 Passive Skills", "Added Small Passive Skills grant: …", notables, sockets.</summary>
    static IEnumerable<string> ClusterLines(PobItem item) =>
        item.Mods.Select(m => m.Text).Where(t =>
            t.StartsWith("Adds ", StringComparison.OrdinalIgnoreCase) || t.Contains("Added ", StringComparison.OrdinalIgnoreCase));

    /// <summary>The short card: the notables by name; a jewel without notables says what its small passives grant.</summary>
    static List<string> MiniLines(PobItem item)
    {
        const string notable = "1 Added Passive Skill is ", grant = "Added Small Passive Skills grant: ";
        var texts = item.Mods.Select(m => m.Text).ToList();
        // "1 Added Passive Skill is a Jewel Socket" reads like a notable but is not one
        var notables = texts.Where(t => t.StartsWith(notable, StringComparison.OrdinalIgnoreCase)).Select(t => t[notable.Length..].Trim())
            .Where(n => !n.Contains("Jewel Socket", StringComparison.OrdinalIgnoreCase)).ToList();
        if (!ClusterJewel.IsCluster(item)) return [];
        return notables.Count > 0 ? notables
            : texts.Where(t => t.StartsWith(grant, StringComparison.OrdinalIgnoreCase)).Select(t => t[grant.Length..].Trim()).ToList();
    }

    void DrawLabels(DrawingContext dc)
    {
        if (_plan == null || _tree == null || _step.Count == 0) return;
        var s = _scale;
        if (s < 0.028) return;

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var allSteps = OverlayMode || s >= 0.07;
        // the game shows node names itself on hover
        var names = !OverlayMode && s >= 0.11;
        var bounds = new Rect(RenderSize);
        bounds.Inflate(40, 40);
        var focus = FocusId;

        foreach (var (id, step) in _step)
        {
            var n = _tree.Nodes[id];
            if (!n.Drawable || (!allSteps && !n.IsKey && id != focus)) continue;
            var p = new Point(n.X * s + _offset.X, n.Y * s + _offset.Y);
            if (!bounds.Contains(p)) continue;
            var rpx = (OverlayMode ? RingRadius(n) : Radius(n)) * s;

            var label = step.ToString(CultureInfo.InvariantCulture);
            var w = StepBadge.WidthFor(label);
            StepBadge.Draw(dc, new Rect(p.X + rpx * 0.75 + 2, p.Y - rpx * 0.75 - 15, w, StepBadge.Size), label, id == focus);

            if (names && n.IsKey && n.Name.Length > 0)
            {
                var nt = Text(n.Name, 11, dpi);
                var lx = p.X - nt.Width / 2;
                var ly = p.Y + rpx + 5;
                dc.DrawRoundedRectangle(LabelFill, null, new Rect(lx - 5, ly - 2, nt.Width + 10, nt.Height + 4), 5, 5);
                dc.DrawText(nt, new Point(lx, ly));
            }
        }
    }

    /// <summary>
    /// Ring around the focus node; when it is off screen (or under the game's bottom bar) an arrow
    /// on the edge points the way, with its step number — "where is 1?" answered without hunting.
    /// </summary>
    void DrawFocus(DrawingContext dc)
    {
        if (_tree == null || FocusId is not int id || !_tree.Nodes.TryGetValue(id, out var n) || !n.Drawable) return;
        var s = _scale;
        var p = new Point(n.X * s + _offset.X, n.Y * s + _offset.Y);
        var label = _step[id].ToString(CultureInfo.InvariantCulture);
        var picked = _highlight == id;

        var view = new Rect(RenderSize);
        // the flask / skill bar hides the bottom of the in-game tree
        if (OverlayMode) view.Height = Math.Max(0, view.Height * 0.87);
        var edge = Pen(Color.FromArgb(0xC0, 0, 0, 0), 6);
        var ring = Pen(StepBadge.FocusColor, 3);

        if (view.Contains(p))
        {
            // on the game's tree the cyan ring lies right on the node's white plan ring: one ring, not two
            var r = OverlayMode ? Math.Max(RingRadius(n) * s, 7) : Math.Max(Radius(n) * s, 7) + 7;
            dc.DrawEllipse(null, edge, p, r, r);
            dc.DrawEllipse(null, ring, p, r, r);
            if (!picked) return;
            // crosshair ticks: the picked node is found at a glance even in a busy cluster
            foreach (var (dx, dy) in new[] { (1.0, 0.0), (-1.0, 0.0), (0.0, 1.0), (0.0, -1.0) })
            {
                var a = new Point(p.X + dx * (r + 4), p.Y + dy * (r + 4));
                var b = new Point(p.X + dx * (r + 18), p.Y + dy * (r + 18));
                dc.DrawLine(edge, a, b);
                dc.DrawLine(ring, a, b);
            }
            return;
        }

        var inner = view;
        inner.Inflate(-34, -34);
        if (inner.Width <= 0 || inner.Height <= 0) return;
        var c = new Point(inner.X + inner.Width / 2, inner.Y + inner.Height / 2);
        var d = p - c;
        var t = Math.Min(d.X == 0 ? double.MaxValue : inner.Width / 2 / Math.Abs(d.X),
                         d.Y == 0 ? double.MaxValue : inner.Height / 2 / Math.Abs(d.Y));
        var u = d;
        u.Normalize();
        var at = c + d * Math.Min(t, 1);
        var side = new Vector(-u.Y, u.X);

        var tip = at + u * 26;
        var arrow = new StreamGeometry();
        using (var g = arrow.Open())
        {
            g.BeginFigure(tip, true, true);
            g.LineTo(at + u * 8 + side * 11, true, false);
            g.LineTo(at + u * 8 - side * 11, true, false);
        }
        arrow.Freeze();
        dc.DrawGeometry(Frozen(StepBadge.FocusColor), Pen(Color.FromArgb(0xC0, 0, 0, 0), 2), arrow);

        var w = StepBadge.WidthFor(label);
        var box = new Rect(at.X - w / 2 - 4, at.Y - StepBadge.Size / 2 - 4, w + 8, StepBadge.Size + 8);
        dc.DrawRoundedRectangle(Frozen(Color.FromArgb(0xE6, 0x0B, 0x0B, 0x0C)), ring, box, box.Height / 2, box.Height / 2);
        StepBadge.Draw(dc, new Rect(at.X - w / 2, at.Y - StepBadge.Size / 2, w, StepBadge.Size), label, focus: true);
    }

    FormattedText Text(string text, double size, double dpi, Color color) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, Frozen(color), dpi);

    FormattedText Text(string text, double size, double dpi)
    {
        if (_textCache.TryGetValue((text, size), out var ft)) return ft;
        ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, Brushes.White, dpi);
        if (_textCache.Count > 4000) _textCache.Clear();
        _textCache[(text, size)] = ft;
        return ft;
    }

    static Pen Pen(Color c, double width, bool dashed = false)
    {
        var p = new Pen(new SolidColorBrush(c), width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (dashed) p.DashStyle = new DashStyle([3, 3], 0);
        p.Freeze();
        return p;
    }

    static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    #endregion

    #region view

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        if (_needFit) FitToPlan();
    }

    /// <summary>Zooms to what has to be taken on this stage (or the whole tree).</summary>
    public void FitToPlan()
    {
        if (_tree == null) return;
        if (ActualWidth < 10 || ActualHeight < 10) { _needFit = true; return; }
        _needFit = false;

        IEnumerable<PassiveNode> focus = _plan == null
            ? _hoverable
            : _plan.Order.Concat(_plan.Target).Select(id => _tree.Nodes[id]).Where(n => n.Drawable);
        var list = focus.ToList();
        if (list.Count == 0) list = _hoverable;
        if (list.Count == 0) return;

        var rect = new Rect(new Point(list.Min(n => n.X), list.Min(n => n.Y)), new Point(list.Max(n => n.X), list.Max(n => n.Y)));
        rect.Inflate(600, 600);
        _scale = Math.Clamp(Math.Min(ActualWidth / rect.Width, ActualHeight / rect.Height), MinScale, 0.4);
        _offset = new Vector(ActualWidth / 2 - (rect.X + rect.Width / 2) * _scale, ActualHeight / 2 - (rect.Y + rect.Height / 2) * _scale);
        InvalidateVisual();
    }

    public void ZoomBy(double factor) => ZoomAt(new Point(ActualWidth / 2, ActualHeight / 2), factor);

    void ZoomAt(Point p, double factor)
    {
        var ns = Math.Clamp(_scale * factor, MinScale, MaxScale);
        var world = new Point((p.X - _offset.X) / _scale, (p.Y - _offset.Y) / _scale);
        _scale = ns;
        _offset = new Vector(p.X - world.X * ns, p.Y - world.Y * ns);
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        ZoomAt(e.GetPosition(this), e.Delta > 0 ? 1.18 : 1 / 1.18);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _dragOrigin = _offset;
        CaptureMouse();
        Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragStart = null;
        ReleaseMouseCapture();
        Cursor = null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_dragStart is { } d && IsMouseCaptured)
        {
            _offset = _dragOrigin + (p - d);
            InvalidateVisual();
            return;
        }
        UpdateHover(p);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_hover == null) return;
        _hover = null;
        InvalidateVisual();
        HoverChanged?.Invoke(null, default);
    }

    void UpdateHover(Point p)
    {
        if (_tree == null) return;
        var wx = (p.X - _offset.X) / _scale;
        var wy = (p.Y - _offset.Y) / _scale;
        var tol = 8 / _scale;
        PassiveNode? best = null;
        var bestD = double.MaxValue;
        foreach (var n in _hoverable)
        {
            var dx = n.X - wx;
            var dy = n.Y - wy;
            var d2 = dx * dx + dy * dy;
            var r = Radius(n) + tol;
            if (d2 < r * r && d2 < bestD) { bestD = d2; best = n; }
        }
        if (!ReferenceEquals(best, _hover))
        {
            _hover = best;
            InvalidateVisual();
        }
        HoverChanged?.Invoke(best, p);
    }

    #endregion
}
