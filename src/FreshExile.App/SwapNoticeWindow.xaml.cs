using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FreshExile.App;

/// <summary>Short over-the-game plate when the game moved the guide to a new stage.</summary>
public partial class SwapNoticeWindow : Window
{
    static readonly TimeSpan ShowFor = TimeSpan.FromSeconds(8);

    readonly Action<bool> _openPanel;
    AnimationClock? _clock;

    /// <param name="openPanel">Opens the panel: true on the gems tab, false on the links tab.</param>
    public SwapNoticeWindow(MainViewModel vm, Action<bool> openPanel)
    {
        _openPanel = openPanel;
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
        // only this announcement's own countdown hides it, not the clock of an earlier one
        timer.Completed += (sender, _) => { if (ReferenceEquals(sender, _clock)) Dismiss(); };
        _clock?.Controller?.Stop();
        _clock = timer.CreateClock();
        TimerScale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, _clock);
    }

    void Dismiss()
    {
        if (!IsVisible) return;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
        fade.Completed += (_, _) => Hide();
        BeginAnimation(OpacityProperty, fade);
    }

    void Dismiss_Click(object sender, MouseButtonEventArgs e) => Dismiss();
    void Close_Click(object sender, RoutedEventArgs e) => Dismiss();

    // the countdown waits while the mouse is on the plate (reading it, reaching for a chip)
    void Hover_Enter(object sender, MouseEventArgs e) => _clock?.Controller?.Pause();
    void Hover_Leave(object sender, MouseEventArgs e) => _clock?.Controller?.Resume();

    void Gems_Click(object sender, RoutedEventArgs e) { _openPanel(true); Dismiss(); }
    void Links_Click(object sender, RoutedEventArgs e) { _openPanel(false); Dismiss(); }
}
