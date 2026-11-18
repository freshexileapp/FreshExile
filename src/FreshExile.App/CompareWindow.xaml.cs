using System.Windows;
using System.Windows.Input;

namespace FreshExile.App;

/// <summary>Our item against one offer, beside the price check window (right of it, or left when the screen ends).</summary>
public partial class CompareWindow : Window
{
    public event Action? CloseRequested;

    public CompareWindow()
    {
        InitializeComponent();
        WindowStartupLocation = WindowStartupLocation.Manual;
        SourceInitialized += (_, _) => Native.SetToolWindow(this);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; CloseRequested?.Invoke(); } };
    }

    public void ShowBeside(ListingVm row, Window anchor)
    {
        DataContext = row;
        var area = SystemParameters.WorkArea;
        RowsScroll.MaxHeight = Math.Max(200, area.Height - 220);
        if (!IsVisible) Show();
        UpdateLayout();
        Place(anchor);
    }

    public void Place(Window anchor)
    {
        if (!IsVisible) return;
        var area = SystemParameters.WorkArea;
        var right = anchor.Left + anchor.ActualWidth + 8;
        Left = right + ActualWidth <= area.Right ? right : Math.Max(area.Left, anchor.Left - 8 - ActualWidth);
        Top = Math.Max(area.Top + 8, Math.Min(anchor.Top, area.Bottom - 8 - ActualHeight));
    }

    void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    void Drag_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
