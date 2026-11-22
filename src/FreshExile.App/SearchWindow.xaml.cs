using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FreshExile.Core;

namespace FreshExile.App;

/// <summary>One line of the search list: the name in its rarity colour and the trade's group beside it.</summary>
public sealed class SearchRowVm(TradeEntry entry)
{
    public TradeEntry Entry { get; } = entry;
    public string Text => Entry.Text;
    public string Note => Entry.Kind == TradeEntryKind.Exchange ? Loc.T($"{Entry.Group} · обмен") : Entry.Group;

    public Brush Brush => Entry.Kind switch
    {
        TradeEntryKind.Unique => Unique,
        TradeEntryKind.Gem => Gem,
        _ => Plain,
    };

    static readonly Brush Unique = Frozen(0xAF, 0x60, 0x25), Gem = Frozen(0x1B, 0xA2, 0x9B), Plain = Frozen(0xC8, 0xC8, 0xC8);
    static Brush Frozen(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }
}

/// <summary>
/// Trade search by name, no item needed: type, pick with the arrows or the mouse, Enter — the price check window searches it.
/// An empty box shows the recent names. Esc or a click elsewhere closes it.
/// </summary>
public partial class SearchWindow : Window
{
    TradeLink? _trade;
    IntPtr _game;
    Func<IReadOnlyList<string>> _recent = () => [];

    /// <summary>A name was picked: the price check takes over (the window is already hidden).</summary>
    public event Action<TradeEntry>? Picked;

    public SearchWindow()
    {
        InitializeComponent();
        UiScale.Attach(this);
        WindowStartupLocation = WindowStartupLocation.Manual;
        SourceInitialized += (_, _) => Native.SetToolWindow(this);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; CloseAndReturn(); } };
        // a click on the game or another program: out of the way, the game keeps its focus
        Deactivated += (_, _) => { if (IsVisible) Hide(); };
    }

    /// <param name="game">The game's window, focused again when the window closes without a pick.</param>
    public void Open(IntPtr game, Func<IReadOnlyList<string>> recent)
    {
        _game = game;
        _recent = recent;
        Show();
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Top + area.Height * 0.18;
        Topmost = true;
        Activate();
        Query.Focus();
        Query.SelectAll();
        Refresh();
    }

    /// <summary>The trade's name list, once loaded; null = the site is out of reach.</summary>
    public void SetTrade(TradeLink? trade)
    {
        _trade = trade;
        Refresh();
    }

    void Refresh()
    {
        if (_trade == null)
        {
            Results.ItemsSource = null;
            StatusText.Text = Loc.T("Загружаю список предметов трейда…");
            return;
        }
        var q = Query.Text.Trim();
        List<SearchRowVm> rows;
        if (q.Length == 0)
        {
            var known = _trade.Catalog.ToDictionary(e => e.Text, StringComparer.OrdinalIgnoreCase);
            rows = _recent().Select(t => known.GetValueOrDefault(t)).OfType<TradeEntry>().Select(e => new SearchRowVm(e)).ToList();
            StatusText.Text = rows.Count > 0 ? Loc.T("Недавние · ↑↓ выбрать, Enter — искать") : Loc.T("Начни вводить название предмета, карты, валюты…");
        }
        else
        {
            rows = _trade.FindNames(q).Select(e => new SearchRowVm(e)).ToList();
            StatusText.Text = rows.Count > 0 ? Loc.T("↑↓ выбрать, Enter — искать") : Loc.T("Ничего не нашлось. Названия — как на трейде, по-английски.");
        }
        Results.ItemsSource = rows;
        Results.SelectedIndex = rows.Count > 0 ? 0 : -1;
    }

    void Query_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => Refresh();

    void Query_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var count = Results.Items.Count;
        switch (e.Key)
        {
            case Key.Down when count > 0:
                Results.SelectedIndex = (Results.SelectedIndex + 1) % count;
                Results.ScrollIntoView(Results.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up when count > 0:
                Results.SelectedIndex = (Results.SelectedIndex - 1 + count) % count;
                Results.ScrollIntoView(Results.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter:
                Pick();
                e.Handled = true;
                break;
        }
    }

    /// <summary>A click on a line (not on the scrollbar) searches it.</summary>
    void Results_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (System.Windows.Controls.ItemsControl.ContainerFromElement(Results, (DependencyObject)e.OriginalSource)
            is not System.Windows.Controls.ListBoxItem { DataContext: SearchRowVm row }) return;
        Results.SelectedItem = row;
        Pick();
    }

    void Pick()
    {
        if (Results.SelectedItem is not SearchRowVm row) return;
        Hide();
        Picked?.Invoke(row.Entry);
    }

    void CloseAndReturn()
    {
        Hide();
        if (_game != IntPtr.Zero) GameInput.SetForegroundWindow(_game);
    }

    void Close_Click(object sender, RoutedEventArgs e) => CloseAndReturn();

    void Drag_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
