using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using FreshExile.Core;

namespace FreshExile.App;

public partial class MainWindow : Window
{
    readonly MainViewModel _vm;
    TreeWindow? _tree;
    HotkeyHost? _hotkeys;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        WatchBuildPopup();
        DataContext = _vm = vm;
        // the panel keeps the size the user gave it; a change of the scale grows it (the mini line keeps its own width)
        UiScale.Attach(this, sizeNow: false, follow: false);
        UiScale.Changed += r =>
        {
            if (_vm.IsCompact) Width = MiniWidth * UiScale.Factor;
            else UiScale.Resize(this, r);
        };

        Placement.Restore(this, vm.State.Main, defaultRight: true);
        // saved right away: the overlay is often killed with the game rather than closed
        LocationChanged += (_, _) => SavePlacement();
        SizeChanged += (_, _) => SavePlacement();
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
        vm.PropertyChanged += OnVmChanged;
        vm.HotkeysChanged += RegisterHotkeys;
        vm.PriceCheckRequested += (text, pick) => _ = RunPriceCheckAsync(text, pick);
        vm.GemPriceRequested += text => _ = RunPriceCheckAsync(text);
        vm.NamedPriceRequested += name => _ = RunPriceByNameAsync(name);
        vm.SendChat = SendChatAsync;

        _treeOverlay = new TreeOverlayController(vm, HideForGameTree, RestoreAfterGameTree);
        _treeOverlay.InventoryChanged += OnInventory;
        Loaded += (_, _) => _treeOverlay.Start();

        _focusTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _focusTimer.Tick += (_, _) => ApplyGameFocus();
        _focusTimer.Start();
        vm.SwapAnnounced += () => (_swapNotice ??= new SwapNoticeWindow(vm, OpenPanelOn)).Announce();
        DataObject.AddPastingHandler(PopupInput, PopupInput_Pasted);
        // a build loaded from the switcher: it closes
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.BuildLine) && !_vm.IsBusy && !_vm.HasBuildError) BuildPopup.IsOpen = false; };

        // a list scrolled down past 40 px folds the stage block into the header's one line: the room goes to the list
        AddHandler(System.Windows.Controls.ScrollViewer.ScrollChangedEvent, new System.Windows.Controls.ScrollChangedEventHandler((_, e) =>
        {
            // folds past 40 px, unfolds only back at the top: the header changing height must not flip it back and forth
            if (e.OriginalSource is not System.Windows.Controls.ScrollViewer { IsVisible: true } v || _vm.IsCompact) return;
            if (v.VerticalOffset > 40) Condensed = true;
            else if (v.VerticalOffset < 4) Condensed = false;
        }));
        vm.PropertyChanged += (_, e) =>
        {
            // another tab or stage starts at the top: the full header comes back
            if (e.PropertyName is nameof(MainViewModel.IsGemsTab) or nameof(MainViewModel.StageIndex) or nameof(MainViewModel.IsCompact)) Condensed = false;
        };
    }

    /// <summary>From the stage plate's chips: the full panel, on the gems (true) or the links (false) tab.</summary>
    void OpenPanelOn(bool gems)
    {
        SetCompact(false);
        if (gems) _vm.IsGemsTab = true; else _vm.IsLinksTab = true;
        Show();
    }

    /// <summary>The build switcher is the panel's width less a margin.</summary>
    public double BuildPopupWidth => Math.Max(260, ActualWidth - 16);

    /// <summary>
    /// The switcher reaches down to the footer: the tab under it neither shows through below the list nor takes clicks
    /// meant for the panel. Header bottom + the popup's offset (6) to the footer's top, less the same gap.
    /// </summary>
    double BuildPopupMinHeight()
    {
        if (!Footer.IsVisible || !Header.IsVisible) return 0;
        var top = Header.TranslatePoint(new Point(0, Header.ActualHeight), this).Y + 6;
        var bottom = Footer.TranslatePoint(new Point(0, 0), this).Y - 6;
        return Math.Max(0, bottom - top);
    }

    DateTime _popupClosedAt;

    /// <summary>The press on the class line is no drag of the window; the switcher opens on the release.</summary>
    void BuildSwitch_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    /// <summary>
    /// A click on the class line: the switcher opens, a second click closes it. On the release, not the press: a popup with
    /// StaysOpen=False opened on the press takes the release outside it for a click away and closes at once.
    /// </summary>
    void BuildSwitch_MouseUp(object sender, MouseButtonEventArgs? e)
    {
        if (e != null) e.Handled = true;
        // the press of this click closed the popup (StaysOpen=False): the release must not reopen it
        if ((DateTime.UtcNow - _popupClosedAt).TotalMilliseconds < 400) return;
        if (BuildPopup.IsOpen) { BuildPopup.IsOpen = false; return; }
        // the panel may have been resized since: the switcher takes its width now
        if (BuildPopup.Child is System.Windows.Controls.Border b)
        {
            System.Windows.Data.BindingOperations.GetBindingExpression(b, WidthProperty)?.UpdateTarget();
            b.MinHeight = BuildPopupMinHeight();
        }
        BuildPopup.IsOpen = true;
        Dispatcher.BeginInvoke(() => PopupInput.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    void BuildSwitch_Click(object sender, RoutedEventArgs e) => BuildSwitch_MouseUp(sender, null);

    void BuildPopup_Closed(object? sender, EventArgs e) => _popupClosedAt = DateTime.UtcNow;

    /// <summary>
    /// The moment IsOpen drops, not Closed (that waits for the fade): a press on the class line closes the popup as a click
    /// away, and the release that follows must not open it again.
    /// </summary>
    void WatchBuildPopup() =>
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(System.Windows.Controls.Primitives.Popup.IsOpenProperty,
                typeof(System.Windows.Controls.Primitives.Popup))
            .AddValueChanged(BuildPopup, (_, _) => { if (!BuildPopup.IsOpen) _popupClosedAt = DateTime.UtcNow; });

    void BuildPopup_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { BuildPopup.IsOpen = false; e.Handled = true; }
    }

    /// <summary>A line in the switcher: that build opens, the switcher closes.</summary>
    void PopupBuild_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RecentBuildVm r }) return;
        if (_vm.OpenRecentCommand.CanExecute(r)) _vm.OpenRecentCommand.Execute(r);
        BuildPopup.IsOpen = false;
    }

    void PopupSettings_Click(object sender, MouseButtonEventArgs e)
    {
        BuildPopup.IsOpen = false;
        OpenSettings();
        _vm.OpenSettingsSection("build");
    }

    /// <summary>Ctrl+V into the empty box: the pasted link or code loads at once.</summary>
    void PopupInput_Pasted(object sender, DataObjectPastingEventArgs e)
    {
        if (PopupInput.Text.Length > 0) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_vm.LoadBuildCommand.CanExecute(null) && _vm.BuildInput.Trim().Length > 0) _vm.LoadBuildCommand.Execute(null);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    public static readonly DependencyProperty CondensedProperty =
        DependencyProperty.Register(nameof(Condensed), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

    /// <summary>The list is scrolled down: the stage block is folded into the header's one line.</summary>
    public bool Condensed { get => (bool)GetValue(CondensedProperty); set => SetValue(CondensedProperty, value); }

    SwapNoticeWindow? _swapNotice;

    /// <summary>The game or this overlay is in front: the price check hotkey is registered only then.</summary>
    bool _gameSide;
    PriceCheckWindow? _price;
    IntPtr _gameWindow;
    bool _priceRunning;
    bool _priceAway;

    /// <summary>
    /// The price check hotkey: in game one Ctrl+Alt+C copies the item under the mouse; with the price window in front
    /// (or nothing copied) the clipboard as it is. Then the window, and a search unless the cooldown still runs.
    /// </summary>
    /// <summary>QA: "--demo-price" opens the window on a sample item, as if copied in game.</summary>
    public Task DemoPriceCheckAsync(string text) => RunPriceCheckAsync(text);

    async Task RunPriceCheckAsync(string? given = null, IReadOnlyCollection<string>? pick = null)
    {
        if (_priceRunning) { Log.Write("price check: still busy with the previous one"); return; }
        _priceRunning = true;
        Log.Write("price check: hotkey");
        try
        {
            string? text;
            if (given != null) text = given;
            else if (GameFocus.Front() == FrontWindow.Game || OverGameFromOurWindow())
            {
                if (GameFocus.Front() == FrontWindow.Game) _gameWindow = GameInput.GetForegroundWindow();
                else
                {
                    // the price window from the previous check took the focus: the mouse is on the game again, so is the copy
                    GameInput.SetForegroundWindow(_gameWindow);
                    await Task.Delay(80);
                }
                // our own hotkeys off while the copy goes out: Ctrl+Alt+C is also "compact panel", and a registered
                // hotkey swallows the keystroke before the game sees it (no item copied, the panel unfolded instead)
                _hotkeys?.Clear();
                try { text = await GameInput.CopyItemAsync(); }
                finally { RegisterHotkeys(); }
            }
            else text = GameInput.ReadClipboard();
            Log.Write(text == null ? "price check: nothing copied" : $"price check: copied {text.Length} chars, item={GameItem.Parse(text) != null}");

            _price ??= new PriceCheckWindow();
            var (vm, error) = await _vm.PreparePriceCheckAsync(text, pick);
            if (vm == null)
            {
                _price.ShowMessage(error ?? "", _gameWindow);
                return;
            }
            _price.ShowCheck(vm, _gameWindow);
            Log.Write("price check: window shown");
            // from the gear tab the window is for picking filters: the search waits for "Искать"
            if (_vm.PriceAutoSearch && vm.CanRequest && pick == null) await vm.SearchAsync();
        }
        catch (Exception ex)
        {
            Log.Write("price check failed: " + ex.Message);
        }
        finally { _priceRunning = false; }
    }

    SearchWindow? _search;

    /// <summary>The search hotkey: a name box over the game; the picked name goes to the price check window and is searched.</summary>
    async Task OpenSearchAsync()
    {
        Log.Write("search: hotkey");
        if (GameFocus.Front() == FrontWindow.Game) _gameWindow = GameInput.GetForegroundWindow();
        if (_search == null)
        {
            _search = new SearchWindow();
            _search.Picked += entry => _ = RunNamedSearchAsync(entry);
        }
        _search.Open(_gameWindow, () => _vm.State.RecentSearches);
        var trade = await _vm.TradeForSearchAsync();
        _search.SetTrade(trade);
        if (trade == null) Log.Write("search: trade site out of reach");
    }

    /// <summary>A runegraft / tattoo from the tree tab: found in the trade's catalogue by name, then priced like a search.</summary>
    async Task RunPriceByNameAsync(string name)
    {
        var trade = await _vm.TradeForSearchAsync();
        var entry = trade?.Catalog.FirstOrDefault(e => string.Equals(e.Text, name, StringComparison.OrdinalIgnoreCase))
                    ?? trade?.FindNames(name, 1).FirstOrDefault();
        if (entry == null) { _vm.Toast(Loc.T($"На трейде нет «{name}»")); return; }
        await RunNamedSearchAsync(entry);
    }

    async Task RunNamedSearchAsync(TradeEntry entry)
    {
        if (_priceRunning) { Log.Write("search: the price check is still busy"); return; }
        _priceRunning = true;
        Log.Write($"search: {entry.Kind} \"{entry.Text}\"");
        try
        {
            _vm.RememberSearch(entry.Text);
            _price ??= new PriceCheckWindow();
            var (vm, error) = await _vm.PreparePriceCheckAsync(entry.ToItem());
            if (vm == null)
            {
                _price.ShowMessage(error ?? "", _gameWindow);
                return;
            }
            _price.ShowCheck(vm, _gameWindow);
            // a name was asked for: searched right away, whatever the auto-search setting
            if (vm.CanRequest) await vm.SearchAsync();
        }
        catch (Exception ex)
        {
            Log.Write("search failed: " + ex.Message);
        }
        finally { _priceRunning = false; }
    }

    /// <summary>
    /// "Написать" on an offer: the message into the game's chat. Our hotkeys are off meanwhile — Ctrl+V or Enter may be
    /// one of them, and a registered hotkey swallows the keystroke before the game sees it.
    /// </summary>
    async Task<bool> SendChatAsync(string text)
    {
        if (!GameFocus.GameRunning() && _gameWindow == IntPtr.Zero) { await GameInput.SendChatAsync(IntPtr.Zero, text); return false; }
        var game = _gameWindow != IntPtr.Zero && IsWindow(_gameWindow) ? _gameWindow : GameInput.FindGameWindow();
        if (game != IntPtr.Zero) _gameWindow = game;
        _hotkeys?.Clear();
        try
        {
            var sent = await GameInput.SendChatAsync(game, text);
            Log.Write(sent ? "whisper: sent to the game's chat" : "whisper: game not found, copied only");
            return sent;
        }
        finally { RegisterHotkeys(); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);

    /// <summary>One of our windows is in front (last check's price window), but the mouse is over the game: copy from it.</summary>
    bool OverGameFromOurWindow() =>
        _gameWindow != IntPtr.Zero && GameFocus.Front() == FrontWindow.Overlay
        && !Application.Current.Windows.OfType<Window>().Any(w => w.IsVisible && w.IsMouseOver);

    void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hotkeys = new HotkeyHost(this);
        RegisterHotkeys();
        ApplyActivation();
    }

    void RegisterHotkeys()
    {
        if (_hotkeys == null) return;
        _hotkeys.Clear();
        // while a new combo is being typed, the old ones must not swallow it
        if (_vm.CapturingRow != null)
        {
            // the combo is typed into the settings window, where the capture was started
            _settings?.Activate();
            return;
        }

        foreach (var row in _vm.HotkeyRows)
        {
            if (row.Binding is not { } b) continue;
            // Ctrl+D and the like belong to other programs (a browser's bookmark): taken only while the game is in front
            if (row.Id is HotkeyActions.Price or HotkeyActions.Merc && !_gameSide) continue;
            Action action = row.Id switch
            {
                HotkeyActions.Main => ToggleVisible,
                HotkeyActions.Tree => ToggleTree,
                HotkeyActions.Through => ToggleTreeClickThrough,
                HotkeyActions.Prev => () => _vm.StepStage(-1),
                HotkeyActions.Compact => ToggleCompact,
                HotkeyActions.Price => () => _ = RunPriceCheckAsync(),
                HotkeyActions.Search => () => _ = OpenSearchAsync(),
                HotkeyActions.Merc => () => _ = RunMercCheckAsync(pick: false),
                _ => () => _vm.StepStage(1),
            };
            var ok = _hotkeys.Register(b.NativeMods, b.Key, action);
            if (row.Id == HotkeyActions.Price) Log.Write($"price hotkey {b.Display}: {(ok ? "registered" : "taken by another program")}");
            if (!ok) _vm.ReportHotkeyFailure(row.Id);
        }
    }

    SettingsWindow? _settings;

    /// <summary>
    /// The gear: settings in their own window; a second click brings the open one to the front.
    /// The panel steps aside meanwhile, so the two do not lie on top of each other, and is back when settings close.
    /// </summary>
    void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    void Merc_Click(object sender, RoutedEventArgs e) => _ = RunMercCheckAsync(pick: true);

    /// <summary>
    /// The mercenary window priced: the screen captured, the window's rectangle picked by hand (the button, or the first
    /// time) or the one picked before (the hotkey), read, and shown as a warrant in the price window.
    /// </summary>
    async Task RunMercCheckAsync(bool pick)
    {
        if (_priceRunning) { Log.Write("merc check: the price check is still busy"); return; }
        _priceRunning = true;
        try
        {
            if (GameFocus.Front() == FrontWindow.Game) _gameWindow = GameInput.GetForegroundWindow();
            byte[] screen;
            int w, h;
            using (var cap = new ScreenCapture())
            {
                var (bytes, cw, ch) = cap.Capture();
                screen = (byte[])bytes.Clone();
                (w, h) = (cw, ch);
            }
            Int32Rect region;
            if (!pick && _vm.State.MercRegion is [var x, var y, var rw, var rh]) region = new Int32Rect(x, y, rw, rh);
            else
            {
                var picked = await RegionPickWindow.PickAsync(screen, w, h, Loc.T("Обведи окно наёмника мышкой: имя сверху, умения с саппортами снизу · Esc — отмена"));
                if (picked is not { } r) { Log.Write("merc check: no region picked"); return; }
                region = r;
                _vm.State.MercRegion = [r.X, r.Y, r.Width, r.Height];
                _vm.SaveState();
            }
            Log.Write($"merc check: region {region.X},{region.Y} {region.Width}x{region.Height}");
            _price ??= new PriceCheckWindow();
            var (vm, error) = await _vm.PrepareMercenaryCheckAsync(MercScan.Crop(screen, w, h, region));
            if (vm == null) { _price.ShowMessage(error ?? "", _gameWindow); return; }
            _price.ShowCheck(vm, _gameWindow);
            if (_vm.PriceAutoSearch && vm.CanRequest) await vm.SearchAsync();
        }
        catch (Exception ex)
        {
            Log.Write("merc check failed: " + ex.Message);
        }
        finally { _priceRunning = false; }
    }

    MapRegexWindow? _mapRegex;

    void MapRegex_Click(object sender, RoutedEventArgs e)
    {
        _mapRegex ??= new MapRegexWindow(new MapRegexViewModel(_vm.State.MapRegex, _vm.SaveState));
        _mapRegex.ShowBeside(this, _gameWindow);
    }

    public void OpenSettings()
    {
        if (_settings == null)
        {
            _settings = new SettingsWindow(_vm);
            var panelWasVisible = IsVisible;
            _settings.Closed += (_, _) =>
            {
                _settings = null;
                if (panelWasVisible && !IsVisible) { Show(); Topmost = true; }
            };
            _settings.Show();
            Hide();
        }
        if (_settings.WindowState == WindowState.Minimized) _settings.WindowState = WindowState.Normal;
        _settings.Activate();
    }

    void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.NeedsFocus)) ApplyActivation();
        // compact shows only the stage block: the empty state needs the full panel
        if (e.PropertyName == nameof(MainViewModel.HasBuild) && _vm.IsCompact && !_vm.HasBuild)
            SetCompact(false);
    }

    // overlay must not steal focus from the game, except when text has to be typed
    void ApplyActivation() => Native.SetNoActivate(this, !_vm.NeedsFocus);

    readonly System.Windows.Threading.DispatcherTimer _focusTimer;
    bool _fadedOut;

    /// <summary>
    /// Alt-Tab to a browser: panel and tree window fade out (fully transparent windows let clicks through)
    /// and come back with the game. Stays on while the game is not running at all, e.g. when setting things up.
    /// Opacity, not Hide(): the show/hide hotkey and the in-game tree keep their own visibility.
    /// </summary>
    void ApplyGameFocus()
    {
        var front = GameFocus.Front();
        var gameSide = front != FrontWindow.Other;
        if (gameSide != _gameSide)
        {
            _gameSide = gameSide;
            RegisterHotkeys();
        }
        // the price check steps out of the way of the browser ("Открыть на трейде") and is back with the game;
        // opacity, not Hide(): showing it again would take the focus away from the game
        var priceAway = front == FrontWindow.Other && GameFocus.GameRunning();
        if (_price != null && priceAway != _priceAway)
        {
            _priceAway = priceAway;
            _price.SetAway(priceAway);
        }

        var fade = _vm.HideWhenGameInactive && front == FrontWindow.Other && GameFocus.GameRunning();
        if (fade == _fadedOut) return;
        _fadedOut = fade;
        foreach (var w in new Window?[] { this, _tree }.OfType<Window>())
        {
            w.Opacity = fade ? 0 : 1;
            w.IsHitTestVisible = !fade;
        }
    }

    double? _leftBeforeInventory, _leftForInventory;

    /// <summary>
    /// The inventory opens on the right: a panel lying over it moves to the left of it, and comes back when the
    /// inventory closes. Not saved as the panel's place; dragged meanwhile — it stays where it was dragged.
    /// </summary>
    void OnInventory(int? panelLeftPx)
    {
        if (panelLeftPx is int px)
        {
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
            var edge = px / dpi;
            if (!IsVisible || Left + ActualWidth <= edge) return;
            _leftBeforeInventory = Left;
            _leftForInventory = Math.Max(0, edge - ActualWidth - 8);
            MoveWithoutSaving(_leftForInventory.Value);
        }
        else if (_leftBeforeInventory is double back)
        {
            if (_leftForInventory is double moved && Math.Abs(Left - moved) < 1) MoveWithoutSaving(back);
            _leftBeforeInventory = _leftForInventory = null;
        }
    }

    void MoveWithoutSaving(double left)
    {
        _switching = true;
        Left = left;
        _switching = false;
    }

    void ToggleCompact()
    {
        if (!_vm.HasBuild) return;
        if (!IsVisible) { Show(); Topmost = true; }
        SetCompact(!_vm.IsCompact);
    }

    void ToggleVisible()
    {
        if (IsVisible) Hide();
        else { Show(); Topmost = true; }
    }

    readonly TreeOverlayController _treeOverlay;
    bool _mainWasVisible, _treeWasVisible;

    // the in-game tree is open: only the path layer and its header stay on screen
    void HideForGameTree()
    {
        _mainWasVisible = IsVisible;
        _treeWasVisible = _tree?.IsVisible == true;
        Hide();
        _tree?.Hide();
    }

    void RestoreAfterGameTree()
    {
        if (_mainWasVisible) Show();
        if (_treeWasVisible) _tree?.Show();
    }

    /// <summary>QA (--ui-shots): the build switcher.</summary>
    internal void OpenBuildPopupForShots() => BuildPopup.IsOpen = true;
    internal void CloseBuildPopupForShots() => BuildPopup.IsOpen = false;
    internal object BuildPopupChild => BuildPopup.Child;

    /// <summary>QA (--ui-shots): the tree window too.</summary>
    internal void ShowTreeForShots() => ShowTree();

    void ShowTree()
    {
        if (_tree == null)
        {
            _tree = new TreeWindow(_vm);
            if (_fadedOut) { _tree.Opacity = 0; _tree.IsHitTestVisible = false; }
        }
        _tree.Show();
        _tree.Topmost = true;
    }

    void ToggleTree()
    {
        if (_tree is { IsVisible: true }) _tree.Hide();
        else ShowTree();
    }

    void ToggleTreeClickThrough()
    {
        ShowTree();
        _vm.TreeClickThrough = !_vm.TreeClickThrough;
    }

    void Drag_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    void OpenTree_Click(object sender, RoutedEventArgs e) => ShowTree();

    void Compact_Click(object sender, RoutedEventArgs e) => SetCompact(!_vm.IsCompact);

    /// <summary>The header X: into the mini tracker; with no build there is no mini, so it closes.</summary>
    void Collapse_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.HasBuild) SetCompact(true);
        else Close();
    }

    /// <summary>After the saved build is applied: the panel comes back the way it was left.</summary>
    public void RestoreCompact()
    {
        if (_vm.State.Compact && _vm.HasBuild) SetCompact(true);
    }

    const double MiniWidth = 300;

    void SetCompact(bool compact)
    {
        if (compact == _vm.IsCompact) return;
        var state = _vm.State;
        _switching = true;
        if (compact)
        {
            if (IsLoaded) state.Main = Placement.Capture(this);
            var full = state.Main ?? Placement.Capture(this);
            _vm.IsCompact = true;
            MinHeight = 0;
            MinWidth = 0;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.Height;
            Width = MiniWidth * UiScale.Factor;
            // each mode has its own spot; the first time the mini keeps the full panel's right edge
            var mini = state.MainMini;
            Left = mini != null && Placement.OnScreen(mini.Left, mini.Top) ? mini.Left : full.Left + full.Width - MiniWidth * UiScale.Factor;
            Top = mini != null && Placement.OnScreen(mini.Left, mini.Top) ? mini.Top : full.Top;
        }
        else
        {
            if (IsLoaded) state.MainMini = Placement.Capture(this);
            SizeToContent = SizeToContent.Manual;
            _vm.IsCompact = false;
            MinHeight = 180 * UiScale.Factor;
            MinWidth = 320 * UiScale.Factor;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            var full = state.Main;
            if (full != null && Placement.OnScreen(full.Left, full.Top))
            {
                Left = full.Left;
                Top = full.Top;
                Width = Math.Max(full.Width, 320 * UiScale.Factor);
                Height = Math.Max(full.Height, 400 * UiScale.Factor);
            }
            else
            {
                Width = 380 * UiScale.Factor;
                Height = 740 * UiScale.Factor;
            }
        }
        _switching = false;
        state.Compact = compact;
        _vm.SaveState();
    }

    bool _switching;

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Mini and full panel remember their own spot.</summary>
    void SavePlacement()
    {
        if (!IsLoaded || _switching || WindowState != WindowState.Normal) return;
        var p = Placement.Capture(this);
        if (_vm.IsCompact) _vm.State.MainMini = p;
        else _vm.State.Main = p;
        _vm.SaveState();
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        SavePlacement();
        if (_tree != null)
        {
            _vm.State.Tree = Placement.Capture(_tree);
            _tree.ForceClose();
        }
        _hotkeys?.Dispose();
        _settings?.Close();
        _price?.Close();
        _treeOverlay.Dispose();
        _vm.Shutdown();
    }
}

public static class Placement
{
    /// <summary>The window's top-left corner lies on some monitor (a saved spot may belong to an unplugged one).</summary>
    public static bool OnScreen(double left, double top) =>
        new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth - 40, SystemParameters.VirtualScreenHeight - 30).Contains(left, top);

    public static WindowPlacement Capture(Window w) => new()
    {
        Left = w.Left,
        Top = w.Top,
        Width = w.ActualWidth,
        Height = w.ActualHeight,
    };

    public static void Restore(Window w, WindowPlacement? p, bool defaultRight)
    {
        var area = SystemParameters.WorkArea;
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);

        w.WindowStartupLocation = WindowStartupLocation.Manual;
        if (p is { Width: > 100, Height: > 100 } && screen.IntersectsWith(new Rect(p.Left, p.Top, p.Width, p.Height)))
        {
            w.Left = p.Left;
            w.Top = p.Top;
            w.Width = p.Width;
            w.Height = p.Height;
            return;
        }
        if (defaultRight)
        {
            w.Left = area.Right - w.Width - 24;
            w.Top = area.Top + 80;
        }
        else
        {
            // left of the guide panel, which sits at the right edge
            w.Width = Math.Min(w.Width, area.Width - 380 - 72);
            w.Height = Math.Min(w.Height, area.Height * 0.85);
            w.Left = area.Left + 24;
            w.Top = area.Top + (area.Height - w.Height) / 2;
        }
    }
}
