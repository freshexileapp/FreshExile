using System.Windows;
using System.Windows.Input;
using FreshExile.Core;

namespace FreshExile.App;

/// <summary>Price check over the game, in the middle of the screen; Esc or the cross closes it and gives the game its focus back.</summary>
public partial class PriceCheckWindow : Window
{
    IntPtr _game;

    public PriceCheckWindow()
    {
        InitializeComponent();
        UiScale.Attach(this);
        WindowStartupLocation = WindowStartupLocation.Manual;
        SourceInitialized += (_, _) => Native.SetToolWindow(this);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; CloseAndReturn(); } };
        // offers arrive after the window is placed: it grows, and must not run off the bottom of the screen
        SizeChanged += (_, _) => { KeepOnScreen(); _compare?.Place(this); _history?.Place(this); };
        LocationChanged += (_, _) => { _compare?.Place(this); _history?.Place(this); };
    }

    CompareWindow? _compare;
    PriceHistoryWindow? _history;
    const int MaxHistory = 12;
    /// <summary>The session's checks, newest first; each keeps its offers, so going back costs no trade request.</summary>
    readonly System.Collections.ObjectModel.ObservableCollection<PriceCheckViewModel> _checks = new();
    PriceCheckViewModel? _vm;
    bool _away;

    /// <summary>
    /// Out of the way while another program is in front (Alt+Tab, the browser), back with the game; the comparison beside it
    /// goes and comes along. Opacity, not Hide(): showing again would take the focus away from the game.
    /// </summary>
    public void SetAway(bool away)
    {
        _away = away;
        foreach (var w in new Window?[] { this, _compare, _history })
        {
            if (w == null) continue;
            w.Opacity = away ? 0 : 1;
            w.IsHitTestVisible = !away;
        }
    }

    void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PriceCheckViewModel.Compared) || _vm == null) return;
        if (_vm.Compared is { } row)
        {
            if (_compare == null)
            {
                _compare = new CompareWindow();
                _compare.CloseRequested += () => _vm?.CloseCompare();
            }
            _compare.Opacity = _away ? 0 : 1;
            _compare.IsHitTestVisible = !_away;
            _compare.ShowBeside(row, this);
        }
        else _compare?.Hide();
    }

    void Bind(PriceCheckViewModel? vm)
    {
        if (_vm != null) { _vm.PropertyChanged -= OnVmChanged; _vm.CloseCompare(); }
        _compare?.Hide();
        _vm = vm;
        if (_vm != null) _vm.PropertyChanged += OnVmChanged;
    }

    /// <param name="game">The game's window, focused again on close.</param>
    public void ShowCheck(PriceCheckViewModel vm, IntPtr game)
    {
        _game = game;
        Remember(vm);
        Bind(vm);
        DataContext = vm;
        Links.Visibility = Visibility.Visible;
        MessageText.Visibility = Visibility.Collapsed;
        Body.Visibility = Visibility.Visible;
        Present();
        ShowHistory();
    }

    /// <summary>A new check on top of the history; the same item checked again replaces its older entry.</summary>
    void Remember(PriceCheckViewModel vm)
    {
        var old = _checks.FirstOrDefault(c => c != vm && c.Title == vm.Title && c.BaseLine == vm.BaseLine);
        if (old != null) _checks.Remove(old);
        if (!_checks.Contains(vm)) _checks.Insert(0, vm);
        while (_checks.Count > MaxHistory) _checks.RemoveAt(_checks.Count - 1);
        foreach (var c in _checks) c.IsCurrent = c == vm;
    }

    bool _historyHidden;

    /// <summary>The history column put away by the player; remembered by the owner through <see cref="HistoryHiddenChanged"/>.</summary>
    public bool HistoryHidden
    {
        get => _historyHidden;
        set
        {
            _historyHidden = value;
            HistoryButton.Opacity = value ? 0.55 : 1;
            if (IsVisible && Body.Visibility == Visibility.Visible) ShowHistory();
        }
    }

    public event Action<bool>? HistoryHiddenChanged;

    void SetHistoryHidden(bool hidden)
    {
        HistoryHidden = hidden;
        HistoryHiddenChanged?.Invoke(hidden);
    }

    void History_Click(object sender, RoutedEventArgs e) => SetHistoryHidden(!_historyHidden);

    /// <summary>The history column beside the window, once there is something to go back to, unless put away.</summary>
    void ShowHistory()
    {
        if (_checks.Count < 2 || _historyHidden) { _history?.Hide(); return; }
        if (_history == null)
        {
            _history = new PriceHistoryWindow(_checks);
            _history.Picked += Reopen;
            _history.HideRequested += () => SetHistoryHidden(true);
        }
        _history.Opacity = _away ? 0 : 1;
        _history.IsHitTestVisible = !_away;
        _history.ShowBeside(this);
    }

    /// <summary>An older check back in the window as it was; the request pause is the newest one's, not its own old one.</summary>
    void Reopen(PriceCheckViewModel vm)
    {
        if (vm == _vm) return;
        vm.InheritCooldown(_checks.Max(c => c.ReadyAt));
        ShowCheck(vm, _game);
    }

    /// <summary>No item under the mouse, the trade site is down…: one line instead of the filters.</summary>
    public void ShowMessage(string text, IntPtr game)
    {
        _game = game;
        Bind(null);
        _history?.Hide();
        DataContext = new MessageHeader();
        Links.Visibility = Visibility.Collapsed;
        MessageText.Text = text;
        MessageText.Visibility = Visibility.Visible;
        Body.Visibility = Visibility.Collapsed;
        Present();
    }

    void Present()
    {
        Show();
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        MaxHeight = area.Height - 16;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        // room for the offers below: placed as if already ~800 px tall, so it does not jump when they arrive
        Top = area.Top + Math.Max(8, (area.Height - Math.Max(ActualHeight, 800)) / 2);
        KeepOnScreen();
        Topmost = true;
        Activate();
    }

    void KeepOnScreen()
    {
        if (!IsVisible) return;
        var area = SystemParameters.WorkArea;
        if (Top + ActualHeight > area.Bottom - 8) Top = Math.Max(area.Top + 8, area.Bottom - 8 - ActualHeight);
    }

    void CloseAndReturn()
    {
        _vm?.CloseCompare();
        _compare?.Hide();
        _history?.Hide();
        Hide();
        if (_game != IntPtr.Zero) GameInput.SetForegroundWindow(_game);
    }

    void Close_Click(object sender, RoutedEventArgs e) => CloseAndReturn();

    void Drag_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}

/// <summary>Header of the "no item" state: every price box and link off, so none of them shows up empty.</summary>
public sealed class MessageHeader
{
    public string Title => Loc.T("Нет предмета");
    public System.Windows.Media.Brush RarityBrush => System.Windows.Media.Brushes.White;
    public bool HasBaseLine => false;
    public bool HasLowest => false;
    public bool HasNinja => false;
    public bool HasRate => false;
}
