using System.Windows;

namespace FreshExile.App;

/// <summary>The session's recent price checks, a narrow column left of the price check window (right when the screen ends).</summary>
public partial class PriceHistoryWindow : Window
{
    /// <summary>An entry was clicked: that check goes back into the price window.</summary>
    public event Action<PriceCheckViewModel>? Picked;

    public PriceHistoryWindow(IEnumerable<PriceCheckViewModel> checks)
    {
        InitializeComponent();
        UiScale.Attach(this);
        WindowStartupLocation = WindowStartupLocation.Manual;
        SourceInitialized += (_, _) => Native.SetToolWindow(this);
        List.ItemsSource = checks;
    }

    public void ShowBeside(Window anchor)
    {
        if (!IsVisible) Show();
        UpdateLayout();
        Place(anchor);
    }

    public void Place(Window anchor)
    {
        if (!IsVisible) return;
        var area = SystemParameters.WorkArea;
        Scroll.MaxHeight = Math.Max(120, Math.Min(anchor.ActualHeight, area.Height - 16) - 40);
        var left = anchor.Left - 8 - ActualWidth;
        Left = left >= area.Left ? left : Math.Min(area.Right - ActualWidth, anchor.Left + anchor.ActualWidth + 8);
        Top = Math.Max(area.Top + 8, Math.Min(anchor.Top, area.Bottom - 8 - ActualHeight));
    }

    void Entry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PriceCheckViewModel vm }) Picked?.Invoke(vm);
    }
}
