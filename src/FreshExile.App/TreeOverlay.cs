using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using FreshExile.Core;

namespace FreshExile.App;

/// <summary>Small diagnostics log: %APPDATA%\FreshExile\overlay.log (rotated at 512 KB).</summary>
public static class Log
{
    static readonly object Gate = new();
    static readonly string FilePath = Path.Combine(StateStore.Dir, "overlay.log");

    public static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(StateStore.Dir);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 512 * 1024)
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
        }
        catch (IOException) { /* diagnostics only */ }
    }
}

/// <summary>GDI copy of the primary screen into a reused 32-bit top-down buffer.</summary>
public sealed class ScreenCapture : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct BitmapInfoHeader
    {
        public uint Size; public int Width; public int Height; public ushort Planes; public ushort BitCount;
        public uint Compression; public uint SizeImage; public int XPels; public int YPels; public uint ClrUsed; public uint ClrImportant;
    }

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

    IntPtr _dc, _bmp, _bits;
    int _w, _h;
    byte[] _buf = [];

    public (byte[] Bytes, int Width, int Height) Capture()
    {
        int w = GetSystemMetrics(0), h = GetSystemMetrics(1);
        if (w != _w || h != _h) Recreate(w, h);
        var screen = GetDC(IntPtr.Zero);
        BitBlt(_dc, 0, 0, w, h, screen, 0, 0, 0x00CC0020);
        ReleaseDC(IntPtr.Zero, screen);
        Marshal.Copy(_bits, _buf, 0, _buf.Length);
        return (_buf, w, h);
    }

    void Recreate(int w, int h)
    {
        Free();
        _dc = CreateCompatibleDC(IntPtr.Zero);
        var hdr = new BitmapInfoHeader { Size = 40, Width = w, Height = -h, Planes = 1, BitCount = 32 };
        _bmp = CreateDIBSection(_dc, ref hdr, 0, out _bits, IntPtr.Zero, 0);
        SelectObject(_dc, _bmp);
        _w = w;
        _h = h;
        _buf = new byte[w * h * 4];
    }

    void Free()
    {
        if (_bmp != IntPtr.Zero) DeleteObject(_bmp);
        if (_dc != IntPtr.Zero) DeleteDC(_dc);
        _bmp = _dc = IntPtr.Zero;
    }

    public void Dispose() => Free();
}

/// <summary>Full-screen click-through layer with only the stage path, lined up with the in-game tree.</summary>
public sealed class OverlayWindow : Window
{
    public TreeCanvas Canvas { get; } = new() { OverlayMode = true };

    public OverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        Content = Canvas;
        SourceInitialized += (_, _) =>
        {
            Native.SetClickThrough(this, true);
            Native.SetNoActivate(this, true);
            Native.SetToolWindow(this);
            ApplyCapture();
        };
    }

    /// <summary>Settings: the path seen by screenshots and screen recordings (to show or report a misplaced path).</summary>
    public bool InCapture { get; set; }

    /// <summary>
    /// Normally our white path is kept out of every capture, ours included: it covers the game's own edge, and a node just
    /// taken in game would never look gold. With <see cref="InCapture"/> it shows up in recordings, at that price.
    /// </summary>
    public void ApplyCapture()
    {
        if (new System.Windows.Interop.WindowInteropHelper(this).Handle == IntPtr.Zero) return;
        if (InCapture) Native.IncludeInCapture(this);
        else Native.ExcludeFromCapture(this);
    }

    public void SetView(AlignResult r)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        Canvas.SetView(r.Scale / dpi.DpiScaleX, new Vector(r.OffsetX / dpi.DpiScaleX, r.OffsetY / dpi.DpiScaleY));
    }
}

/// <summary>
/// Watches the screen while the game is in front; when the allocated path is found the in-game tree is open:
/// hide the panels, show the path layer + header, follow pans. Screen reading only — nothing is sent to the game.
/// </summary>
public sealed class TreeOverlayController : IDisposable
{

    readonly MainViewModel _vm;
    readonly Dispatcher _ui;
    readonly Action _hideOthers, _restoreOthers;
    readonly CancellationTokenSource _cts = new();
    OverlayWindow? _overlay;
    TreeHeaderWindow? _header;
    volatile TreeAligner? _aligner;
    volatile bool _open;
    int _misses;
    int _gamePid = -1;
    DateTime _nextCalibration;

    /// <summary>Inventory opened (screen x in pixels where it starts) or closed (null). Raised on the UI thread.</summary>
    public event Action<int?>? InventoryChanged;
    int? _inventoryLeft;

    void SetInventory(int? left)
    {
        if (left == _inventoryLeft) return;
        _inventoryLeft = left;
        Log.Write(left == null ? "inventory closed" : $"inventory open, panel from x={left}");
        _ = _ui.BeginInvoke(() => InventoryChanged?.Invoke(left));
    }

    public TreeOverlayController(MainViewModel vm, Action hideOthers, Action restoreOthers)
    {
        _vm = vm;
        _ui = Application.Current.Dispatcher;
        _hideOthers = hideOthers;
        _restoreOthers = restoreOthers;
        vm.AlignNodesChanged += RebuildAligner;
        vm.RecalibrateRequested += () => _nextCalibration = DateTime.MinValue;
        vm.PlanChanged += () => _overlay?.Canvas.SetData(_vm.Tree, _vm.Plan);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.HighlightNodeId) && _overlay != null) _overlay.Canvas.Highlight = _vm.HighlightNodeId;
        };
    }

    public void Start()
    {
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MainViewModel.TreeOverlayInCapture) || _overlay == null) return;
            _overlay.InCapture = _vm.TreeOverlayInCapture;
            _overlay.ApplyCapture();
        };
        var state = _vm.State;
        if (state.TreeScale is double legacy && state.TreeScales.Count == 0) state.TreeScales.Add(legacy);
        state.TreeScale = null;
        RebuildAligner();
        _ = Task.Run(() => Loop(_cts.Token));
    }

    /// <summary>Gold template points from which the taken path itself is matched (about a dozen links).</summary>
    const int GoldEnough = 60;

    void RebuildAligner()
    {
        var nodes = _vm.AlignNodes;
        var tree = _vm.Tree;
        var gold = tree != null && nodes is { Count: > 0 } ? TreeAligner.BuildTemplate(tree, nodes) : null;
        // a fresh character (or its first few points) has too little gold to go by: the tree's own drawing around the
        // class start is matched instead, until the taken path is long enough
        if (gold is not { Count: >= GoldEnough } && tree != null && _vm.ClassStartId is int start
            && TreeAligner.BuildStructureTemplate(tree, start) is { Count: >= 30 } structure)
            _aligner = new TreeAligner(structure, structure: true);
        else
            _aligner = gold != null ? new TreeAligner(gold) : null;
        if (_aligner is { PointCount: < 30 }) _vm.AlignStatus = Loc.T("Мало взятых узлов для наложения");
    }

    async Task Loop(CancellationToken ct)
    {
        using var cap = new ScreenCapture();
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(_open ? 80 : 300, ct); }
            catch (TaskCanceledException) { break; }
            try { Tick(cap); }
            catch (Exception ex)
            {
                Log.Write("tick failed: " + ex);
                _ = _ui.BeginInvoke(() => _vm.AlignStatus = Loc.T($"Ошибка наложения: {ex.Message}"));
            }
        }
    }

    string? _lastSkip;

    void Tick(ScreenCapture cap)
    {
        // tracking off: no screen capture at all (tree overlay and inventory dodge both live on it)
        if (!_vm.Tracking)
        {
            if (_lastSkip != "tracking off") Log.Write("idle: tracking off");
            _lastSkip = "tracking off";
            SetInventory(null);
            if (_open) _ = _ui.BeginInvoke(Close);
            return;
        }
        var aligner = _aligner;
        var inFront = GameInFront();
        var skip = !_vm.TreeOverlayEnabled ? "disabled" : aligner == null ? "no template (tree or allocated nodes not loaded)" : !inFront ? "game not in front" : null;
        if (skip != _lastSkip) Log.Write(skip == null ? $"watching, template {aligner!.PointCount} pts" : "idle: " + skip);
        _lastSkip = skip;
        if (!inFront)
        {
            SetInventory(null);
            if (_open) _ = _ui.BeginInvoke(Close);
            return;
        }

        var t0 = Stopwatch.StartNew();
        var (bytes, w, h) = cap.Capture();
        // the panel steps aside from the inventory; works without the tree overlay too
        SetInventory(!_open && InventoryPanel.IsOpen(bytes, w, h, w * 4) ? InventoryPanel.PanelLeft(w, h) : null);
        if (skip != null)
        {
            if (_open) _ = _ui.BeginInvoke(Close);
            return;
        }

        var state = _vm.State;
        // the remembered zooms (this screen height) and the game's own steps: a new zoom is found without a sweep
        var known = TreeDetector.ZoomsToTry(state.TreeScaleHeight == h ? state.TreeScales : [], h);

        var patch = state.TreeHeaderPatch;
        var v = TreeDetector.Evaluate(aligner!, bytes, w, h, known, patch, _open, DateTime.UtcNow >= _nextCalibration,
            () => _ = _ui.BeginInvoke(() =>
            {
                // the hint "path not found, zoom out" stays: the sweep repeats every few seconds and would hide it
                if (!_vm.AlignWarning) _vm.AlignStatus = Loc.T("подбираю масштаб…");
                // the sweep takes ~2 s: the path of the old zoom would sit off the nodes all that time
                HidePath();
            }), _open ? _shownScale : null);
        if (patch != null && v.TitleSeen != _lastTitle) Log.Write(v.TitleSeen ? "skills title on screen" : "skills title gone");
        _lastTitle = v.TitleSeen;
        if (v.SweepRan)
        {
            _nextCalibration = DateTime.UtcNow.AddSeconds(known.Count == 0 ? 3 : 2);
            Log.Write($"sweep: {(v.NewScale ? Fmt(v.Align) : "no better fit")} (known best {v.KnownScore:F2})");
        }
        var r = v.Align;
        var good = v.Good;
        var newScale = v.NewScale;
        Recorder.Note(v, _open, bytes, w, h);

        if (patch != null && !v.TitleSeen)
        {
            if (_open) _ = _ui.BeginInvoke(Close);
            return;
        }

        if (good != _open || newScale || (DateTime.UtcNow - _lastLog).TotalSeconds > 5)
        {
            _lastLog = DateTime.UtcNow;
            Log.Write($"frame {w}x{h}: known [{string.Join(", ", known.Select(k => k.ToString("F3")))}] -> {Fmt(r)}, good={good}, open={_open}, {t0.ElapsedMilliseconds} ms");
        }

        if (good)
        {
            var res = r!.Value;
            _misses = 0;
            _shownScale = res.Scale;
            var title = newScale || state.TreeHeaderPatch == null ? TreeAligner.HeaderPatch(bytes, w, h, w * 4) : null;
            _ = _ui.BeginInvoke(() =>
            {
                if (newScale) RememberScale(res.Scale, h);
                if (title != null) state.TreeHeaderPatch = title;
                if (newScale || title != null) _vm.SaveState();
                Show(res);
            });

            // points spent in game right now: their edges turn gold before any profile sync knows about them
            if (_vm.Tree is { } tree && _vm.Plan is { Order.Count: > 0 } plan)
            {
                if (!ReferenceEquals(tree, _edgesTree)) { _edges = TreeAligner.EdgesByNode(tree); _edgesTree = tree; }
                var taken = Steady(aligner!.DetectTaken(tree, plan, _edges!, res, w, h), tree);
                if (taken.Count > 0)
                {
                    Log.Write($"taken on screen: {string.Join(", ", taken.Select(id => tree.Nodes[id].Name))}");
                    _ = _ui.BeginInvoke(() => _vm.AddLiveTaken(taken));
                }
            }

            // taken on screen earlier, gold gone again (Cancel instead of Apply): two frames in a row, then back to "to take"
            if (_vm.Tree is { } tree3 && _vm.Plan is { } plan3 && _vm.LiveTakenSnapshot is { Length: > 0 } live)
            {
                if (!ReferenceEquals(tree3, _edgesTree)) { _edges = TreeAligner.EdgesByNode(tree3); _edgesTree = tree3; }
                var lost = aligner!.DetectGone(tree3, live, plan3.Have, _edges!, res, w, h);
                var sureLost = Steady(lost.Where(_lostSeen.Contains), tree3).ToList();
                _lostSeen = lost;
                if (sureLost.Count > 0)
                {
                    Log.Write($"no longer taken on screen: {string.Join(", ", sureLost.Select(id => tree3.Nodes[id].Name))}");
                    _ = _ui.BeginInvoke(() => _vm.RemoveLiveTaken(sureLost));
                }
            }

            // refunded on screen earlier, gold again (the respec was cancelled): two frames in a row, then allocated again
            if (_vm.Tree is { } tree4 && _vm.Plan is { } plan4 && _vm.LiveRefundedSnapshot is { Length: > 0 } refunded)
            {
                if (!ReferenceEquals(tree4, _edgesTree)) { _edges = TreeAligner.EdgesByNode(tree4); _edgesTree = tree4; }
                var back = aligner!.DetectBack(tree4, refunded, plan4.Have, _edges!, res, w, h);
                var sureBack = Steady(back.Where(_backSeen.Contains), tree4).ToList();
                _backSeen = back;
                if (sureBack.Count > 0)
                {
                    Log.Write($"allocated again on screen: {string.Join(", ", sureBack.Select(id => tree4.Nodes[id].Name))}");
                    _ = _ui.BeginInvoke(() => _vm.RemoveLiveRefunded(sureBack));
                }
            }

            // respec applied in game: refunded nodes lose their gold; two frames in a row so a passing effect does not count
            if (_vm.Tree is { } tree2 && _vm.Plan is { Refund.Count: > 0 } plan2)
            {
                if (!ReferenceEquals(tree2, _edgesTree)) { _edges = TreeAligner.EdgesByNode(tree2); _edgesTree = tree2; }
                var gone = aligner!.DetectRefunded(tree2, plan2, _edges!, res, w, h);
                var sure = Steady(gone.Where(_refundSeen.Contains), tree2).ToList();
                _refundSeen = gone;
                if (sure.Count > 0)
                {
                    Log.Write($"refunded on screen: {string.Join(", ", sure.Select(id => tree2.Nodes[id].Name))}");
                    _ = _ui.BeginInvoke(() => _vm.AddLiveRefunded(sure));
                }
            }
        }
        // a frame without a fit: with the title still there the path hides at once (a zoom step leaves it at the old
        // scale, off the nodes, for a few frames otherwise); the whole overlay closes only after two
        else if (_open && ++_misses >= (v.TitleSeen ? 1 : 2))
        {
            // the "Skills" title is still there: the tree is open, only our path is lost (zoomed in, panned away from the
            // allocated nodes). The path waits hidden instead of the whole overlay closing and the panel blinking back.
            if (v.TitleSeen) _ = _ui.BeginInvoke(HidePath);
            else _ = _ui.BeginInvoke(Close);
        }
    }

    void HidePath()
    {
        if (!_open || _overlay is not { IsVisible: true }) return;
        Log.Write("path lost, title still on screen -> path hidden, overlay kept");
        _overlay.Hide();
        // said where the eye is, in the accent colour: an empty tree looks like the overlay does not work
        _vm.AlignStatus = _aligner is { IsStructure: true }
            ? Loc.T("Путь не найден: отдали дерево колесом мыши или сдвинь к портрету класса")
            : Loc.T("Путь не найден: отдали дерево колесом мыши или сдвинь к взятым узлам");
        _vm.AlignWarning = true;
    }

    DateTime _lastLog;
    double? _shownScale;
    bool _lastTitle;
    HashSet<int> _refundSeen = new();
    HashSet<int> _lostSeen = new();
    HashSet<int> _backSeen = new();
    PassiveTree? _edgesTree;
    Dictionary<int, List<Edge>>? _edges;

    static string Fmt(AlignResult? r) =>
        r is { } a ? $"s={a.Scale:F4} off=({a.OffsetX:F0},{a.OffsetY:F0}) score={a.Score:F2} pts={a.Points}" : "none";

    void RememberScale(double s, int height)
    {
        var state = _vm.State;
        if (state.TreeScaleHeight != height)
        {
            state.TreeScales.Clear();
            state.TreeScaleHeight = height;
        }
        state.TreeScales.RemoveAll(x => Math.Abs(x / s - 1) < 0.015);
        state.TreeScales.Insert(0, s);
        // a handful of zoom levels is enough; each one costs a vote pass per frame
        if (state.TreeScales.Count > 6) state.TreeScales.RemoveRange(6, state.TreeScales.Count - 6);
    }

    static readonly int OwnPid = Environment.ProcessId;

    bool GameInFront()
    {
        var hwnd = GetForegroundWindow();
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == _gamePid) return true;
        // a click on our own tree header (show all steps) is still the game's moment: the overlay must not close for it
        if (pid == OwnPid && _gamePid != 0) return true;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            if (!p.ProcessName.StartsWith("PathOfExile", StringComparison.OrdinalIgnoreCase)) return false;
            _gamePid = (int)pid;
            return true;
        }
        catch (ArgumentException) { return false; }
    }

    void Show(AlignResult r)
    {
        if (!_open)
        {
            Log.Write("tree open -> overlay shown");
            _open = true;
            _hideOthers();
            _overlay ??= new OverlayWindow { InCapture = _vm.TreeOverlayInCapture };
            _header ??= new TreeHeaderWindow(_vm);
            _overlay.Canvas.SetData(_vm.Tree, _vm.Plan);
            _overlay.Canvas.Highlight = _vm.HighlightNodeId;
            _overlay.Show();
            _header.Show();
        }
        // each frame's fit lands a pixel or two either way: the path would shiver. It moves only for a real pan or zoom
        if (_shownView is not { } v || Math.Abs(r.OffsetX - v.OffsetX) > 2.5 || Math.Abs(r.OffsetY - v.OffsetY) > 2.5
            || Math.Abs(r.Scale / v.Scale - 1) > 0.004 || !_overlay!.IsVisible)
        {
            _overlay!.SetView(r);
            _shownView = r;
        }
        if (!_overlay.IsVisible) _overlay.Show();
        // in steps of 5 %: a number changing every frame reads as trouble
        _vm.AlignStatus = Loc.T($"совмещено {Math.Round(r.Score * 20) * 5:0} %");
        _vm.AlignWarning = false;
    }

    AlignResult? _shownView;

    /// <summary>
    /// A node whose link sits between "gold" and "not gold" can be read taken, then not, frame after frame: its ring or
    /// red mark blinks. Three changes within 10 s and the node is left as the plan has it until the tree closes.
    /// </summary>
    readonly Dictionary<int, List<DateTime>> _flips = new();
    readonly HashSet<int> _frozen = new();

    HashSet<int> Steady(IEnumerable<int> changed, PassiveTree tree)
    {
        var now = DateTime.UtcNow;
        var keep = new HashSet<int>();
        foreach (var id in changed)
        {
            if (_frozen.Contains(id)) continue;
            if (!_flips.TryGetValue(id, out var times)) _flips[id] = times = new();
            times.RemoveAll(t => now - t > TimeSpan.FromSeconds(10));
            times.Add(now);
            if (times.Count >= 3)
            {
                _frozen.Add(id);
                Log.Write($"unstable on screen, left as planned: {(tree.Nodes.TryGetValue(id, out var n) ? n.Name : id.ToString())}");
                continue;
            }
            keep.Add(id);
        }
        return keep;
    }

    void Close()
    {
        if (!_open) return;
        Log.Write("tree closed -> overlay hidden");
        _open = false;
        _shownView = null;
        _flips.Clear();
        _frozen.Clear();
        _misses = 0;
        _overlay?.Hide();
        _header?.Hide();
        _vm.HighlightNodeId = null;
        _restoreOthers();
        _vm.OnGameTreeClosed();
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    public void Dispose()
    {
        _cts.Cancel();
        _overlay?.Close();
        _header?.ForceClose();
    }
}
