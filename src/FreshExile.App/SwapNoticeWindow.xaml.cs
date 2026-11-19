using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FreshExile.App;

/// <summary>Short over-the-game plate when the game moved the guide to a new stage.</summary>
public partial class SwapNoticeWindow : Window
{
    static readonly TimeSpan ShowFor = TimeSpan.FromSeconds(5);

    public SwapNoticeWindow(MainViewModel vm)
    {
        InitializeComponent();
        UiScale.Attach(this);
        DataContext = vm;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SourceInitialized += (_, _) =>
        {
            Native.SetNoActivate(this, true);
            Native.SetToolWindow(this);
        };
    }

    public void Announce()
    {
        Show();
        UpdateLayout();
        // top centre, under the game's own top bar
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Top + area.Height * 0.12;
        Topmost = true;

        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        var timer = new DoubleAnimation(1, 0, ShowFor);
        timer.Completed += (_, _) => Dismiss();
        TimerScale.BeginAnimation(ScaleTransform.ScaleXProperty, timer);
    }

    void Dismiss()
    {
        if (!IsVisible) return;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
        fade.Completed += (_, _) => Hide();
        BeginAnimation(OpacityProperty, fade);
    }

    void Dismiss_Click(object sender, MouseButtonEventArgs e) => Dismiss();
}
