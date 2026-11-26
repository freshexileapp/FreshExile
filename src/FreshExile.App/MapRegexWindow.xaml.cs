using System.Windows;
using System.Windows.Input;

namespace FreshExile.App;

/// <summary>The map regex builder over the game; Esc or the cross closes it and gives the game its focus back.</summary>
public partial class MapRegexWindow : Window
{
    IntPtr _game;

    public MapRegexWindow(MapRegexViewModel vm)
    {
        InitializeComponent();
        UiScale.Attach(this);
        DataContext = vm;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SourceInitialized += (_, _) => Native.SetToolWindow(this);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; CloseAndReturn(); } };
    }

    /// <summary>Next to <paramref name="anchor"/> (the main panel), on the side with room; the game gets its focus back on close.</summary>
    public void ShowBeside(Window anchor, IntPtr game)
    {
        _game = game;
        if (!IsVisible)
        {
            var area = SystemParameters.WorkArea;
            Height = Math.Min(Height, area.Height - 16);
            var left = anchor.Left - 8 - Width;
            Left = left >= area.Left ? left : Math.Min(area.Right - Width, anchor.Left + anchor.ActualWidth + 8);
            Top = Math.Max(area.Top + 8, Math.Min(anchor.Top, area.Bottom - 8 - Height));
            Show();
        }
        Activate();
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
