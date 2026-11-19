using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FreshExile.Core;

namespace FreshExile.App;

public partial class TreeWindow : Window
{
    readonly MainViewModel _vm;
    bool _forceClose;

    public TreeWindow(MainViewModel vm)
    {
        InitializeComponent();
        UiScale.Attach(this, sizeNow: false);
        DataContext = _vm = vm;
        Placement.Restore(this, vm.State.Tree, defaultRight: false);

        vm.PlanChanged += OnPlanChanged;
        vm.PropertyChanged += OnVmChanged;
        Canvas.HoverChanged += OnHover;
        SourceInitialized += (_, _) => { Native.SetToolWindow(this); ApplyClickThrough(); };
        Loaded += (_, _) => OnPlanChanged();
        Closing += OnClosing;
    }

    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_forceClose) return;
        e.Cancel = true;
        Hide();
    }

    int _shownStage = -1;

    void OnPlanChanged()
    {
        Canvas.SetData(_vm.Tree, _vm.Plan);
        // new stage (also when Auto switched it): jump to what has to be taken now
        if (_vm.StageIndex != _shownStage)
        {
            _shownStage = _vm.StageIndex;
            Canvas.FitToPlan();
        }
    }

    void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.TreeClickThrough)) ApplyClickThrough();
        else if (e.PropertyName == nameof(MainViewModel.HighlightNodeId)) Canvas.Highlight = _vm.HighlightNodeId;
    }

    void ApplyClickThrough()
    {
        var on = _vm.TreeClickThrough;
        Native.SetClickThrough(this, on);
        Native.SetNoActivate(this, on);
        Frame.BorderBrush = on ? Brushes.Transparent : (Brush)FindResource("Line");
        if (on) Tip.Visibility = Visibility.Collapsed;
    }

    void OnHover(PassiveNode? node, Point p)
    {
        if (node == null || _vm.TreeClickThrough) { Tip.Visibility = Visibility.Collapsed; return; }

        var plan = _vm.Plan;
        var step = plan?.Order is { } order ? IndexOf(order, node.Id) : -1;
        var state = step >= 0 ? Loc.T("взять")
            : plan?.Refund.Contains(node.Id) == true ? Loc.T("снять при респеке")
            : plan != null && plan.Have.Contains(node.Id) && plan.Target.Contains(node.Id) ? Loc.T("уже взято")
            : null;

        TipTitle.Text = node.Name.Length > 0 ? node.Name : Loc.T("Пассивка");
        TipKind.Text = (KindName(node) + (state != null ? " · " + state : "")).ToUpperInvariant();
        TipStepBox.Visibility = step >= 0 ? Visibility.Visible : Visibility.Collapsed;
        TipStep.Text = $"#{step + 1}";

        var stats = node.Kind == NodeKind.Mastery
            ? _vm.MasteryText(node) is { } chosen ? chosen : Loc.T($"{node.MasteryEffects.Count} вариантов мастерства")
            : string.Join("\n", node.Stats);
        TipStats.Text = stats;
        TipStats.Visibility = stats.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        Tip.Visibility = Visibility.Visible;
        Tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = Tip.DesiredSize;
        var host = (FrameworkElement)Tip.Parent;
        var x = p.X + 18;
        var y = p.Y + 18;
        if (x + size.Width > host.ActualWidth - 8) x = p.X - size.Width - 14;
        if (y + size.Height > host.ActualHeight - 8) y = p.Y - size.Height - 14;
        System.Windows.Controls.Canvas.SetLeft(Tip, Math.Max(4, x));
        System.Windows.Controls.Canvas.SetTop(Tip, Math.Max(4, y));
    }

    static int IndexOf(IReadOnlyList<int> list, int id)
    {
        for (var i = 0; i < list.Count; i++) if (list[i] == id) return i;
        return -1;
    }

    static string KindName(PassiveNode n) => n.Kind switch
    {
        NodeKind.Notable => "Notable",
        NodeKind.Keystone => "Keystone",
        NodeKind.Mastery => Loc.T("Мастерство"),
        NodeKind.Jewel => Loc.T("Гнездо самоцвета"),
        _ => Loc.T("Пассивка"),
    };

    void Drag_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    void Fit_Click(object sender, RoutedEventArgs e) => Canvas.FitToPlan();
    void ZoomIn_Click(object sender, RoutedEventArgs e) => Canvas.ZoomBy(1.3);
    void ZoomOut_Click(object sender, RoutedEventArgs e) => Canvas.ZoomBy(1 / 1.3);
    void Hide_Click(object sender, RoutedEventArgs e) => Hide();
}
