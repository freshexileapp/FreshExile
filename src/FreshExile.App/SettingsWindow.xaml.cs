using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using FreshExile.Core;

namespace FreshExile.App;

/// <summary>
/// Settings in a window of their own, sections on the left. An ordinary window that takes the focus
/// (unlike the overlay panel): fields are typed in here, and hotkeys are captured here.
/// </summary>
public partial class SettingsWindow : Window
{
    readonly MainViewModel _vm;

    public SettingsWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        PreviewKeyDown += OnPreviewKeyDown;
        // a capture left running would keep the hotkeys off
        Closed += (_, _) => { if (_vm.CapturingRow != null) _vm.CompleteCapture(null, cancel: true); };
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm.CapturingRow == null)
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            return;
        }
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = true;
        if (HotkeyBinding.IsModifierKey(key)) return;

        if (key == Key.Escape) _vm.CompleteCapture(null, cancel: true);
        else if (key is Key.Back or Key.Delete) _vm.CompleteCapture(null);
        else _vm.CompleteCapture(new HotkeyBinding(Keyboard.Modifiers, key));
    }

    void Drag_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void Report_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _vm.SaveNow();
            var path = Report.Create(_vm.State, _vm.ReportWithFrames);
            _vm.Toast(Loc.T($"Отчёт на рабочем столе: {System.IO.Path.GetFileName(path)}"));
        }
        catch (Exception ex) { _vm.Toast(Loc.T($"Отчёт не сохранился: {ex.Message}")); }
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = Loc.T("Client.txt из папки Path of Exile\\logs"),
            Filter = Loc.T("Client.txt|Client.txt|Текстовые файлы|*.txt"),
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) == true) _vm.ClientLogPath = dlg.FileName;
    }
}
