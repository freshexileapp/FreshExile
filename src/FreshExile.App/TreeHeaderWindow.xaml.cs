using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace FreshExile.App;

public partial class TreeHeaderWindow : Window
{
    readonly MainViewModel _vm;
    bool _forceClose;

    public TreeHeaderWindow(MainViewModel vm)
    {
        InitializeComponent();
        UiScale.Attach(this);
        DataContext = _vm = vm;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = SystemParameters.WorkArea.Left + 16;
        Top = SystemParameters.WorkArea.Top + 12;
        SourceInitialized += (_, _) =>
        {
            // clicks work, the game keeps focus
            Native.SetNoActivate(this, true);
            Native.SetToolWindow(this);
        };
        Closing += (_, e) => { if (!_forceClose) { e.Cancel = true; Hide(); } };
    }

    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    void Drag_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    void Step_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PlanStepVm step }) _vm.HighlightNodeId = step.NodeId;
    }

    void Step_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PlanStepVm step } && _vm.HighlightNodeId == step.NodeId) _vm.HighlightNodeId = null;
    }

    void Recalibrate_Click(object sender, RoutedEventArgs e) => _vm.Recalibrate();

    void Off_Click(object sender, RoutedEventArgs e) => _vm.TreeOverlayEnabled = false;
}
