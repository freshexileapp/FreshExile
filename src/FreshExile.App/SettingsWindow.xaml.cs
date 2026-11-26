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
        UiScale.Attach(this);
        DataContext = _vm = vm;
        PreviewKeyDown += OnPreviewKeyDown;
        // a setting changed here: "Сохранено" for a moment (they apply and save at once, there is no button)
        PreviewMouseDown += (_, _) => _touched = DateTime.UtcNow;
        PreviewKeyDown += (_, _) => _touched = DateTime.UtcNow;
        vm.PropertyChanged += OnVmChanged;
        Closed += (_, _) => vm.PropertyChanged -= OnVmChanged;
        DataObject.AddPastingHandler(TopPhraseBox, OnTopPhrasePaste);
        // a capture left running would keep the hotkeys off
        Closed += (_, _) => { if (_vm.CapturingRow != null) _vm.CompleteCapture(null, cancel: true); };
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // a recent build's name being typed: Enter keeps it, Esc drops it (the window stays)
        if (_vm.RenamingRecent != null && e.Key is Key.Enter or Key.Escape)
        {
            if (e.Key == Key.Enter) _vm.CommitRename(); else _vm.CancelRename();
            e.Handled = true;
            return;
        }
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

    DateTime _touched;

    static readonly HashSet<string> NotSettings = new(StringComparer.Ordinal)
    {
        nameof(MainViewModel.ToastText), nameof(MainViewModel.IsToastVisible), nameof(MainViewModel.ConfirmingClear),
        nameof(MainViewModel.NotConfirmingClear), "IsSetGame", "IsSetTracking", "IsSetBuild", "IsSetTree", "IsSetTrade", "IsSetTop",
        "IsSetKeys", "IsSetAbout", "SettingsSectionTitle", "RunTimerText", "RunTimerLabel", "RunTimerTime", "HasRunTimer", "RunTimerTip",
        "CapturingRow", "BuildInput", "IsBusy", "LoadButtonText", "SaveStageText", "SyncButtonText",
    };

    void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // only what the user just did in this window, and never over a toast that says more ("открыт билд…")
        if (!IsActive || (DateTime.UtcNow - _touched).TotalSeconds > 1.5 || NotSettings.Contains(e.PropertyName ?? "") || _vm.IsToastVisible) return;
        var fade = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(1, TimeSpan.FromMilliseconds(120)));
        fade.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(1, TimeSpan.FromMilliseconds(1200)));
        fade.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(0, TimeSpan.FromMilliseconds(1450)));
        SavedToast.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>The shadow over the bottom edge while there is more of the section below.</summary>
    void Scroll_Changed(object sender, System.Windows.Controls.ScrollChangedEventArgs e) =>
        MoreShadow.Visibility = Scroll.VerticalOffset + Scroll.ViewportHeight < Scroll.ExtentHeight - 2 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Enter adds the typed phrase as a chip.</summary>
    void TopPhrase_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || TopPhraseBox.Text.Trim().Length == 0) return;
        _vm.AddTopPhrases(TopPhraseBox.Text);
        TopPhraseBox.Clear();
        e.Handled = true;
    }

    /// <summary>Several lines pasted at once: each its own chip.</summary>
    void OnTopPhrasePaste(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(DataFormats.UnicodeText) is not string text || !text.Contains('\n')) return;
        _vm.AddTopPhrases(text);
        e.CancelCommand();
    }

    void Drag_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>The name box takes the keyboard as soon as it shows, its text selected for typing over.</summary>
    void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox box || !box.IsVisible) return;
        Dispatcher.BeginInvoke(() => { box.Focus(); box.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>A click elsewhere keeps the typed name, as Enter would.</summary>
    void RenameBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox { DataContext: RecentBuildVm r } && r == _vm.RenamingRecent) _vm.CommitRename();
    }

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
