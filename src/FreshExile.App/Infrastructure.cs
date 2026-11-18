using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;

namespace FreshExile.App;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand(Action<object?> run, Func<object?, bool>? can = null) : ICommand
{
    public RelayCommand(Action run, Func<object?, bool>? can = null) : this(_ => run(), can) { }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? p) => can?.Invoke(p) ?? true;
    public void Execute(object? p) => run(p);
}

public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type t, object p, CultureInfo c) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public static class Native
{
    const int GWL_EXSTYLE = -20;
    const long WS_EX_TRANSPARENT = 0x20;
    const long WS_EX_TOOLWINDOW = 0x80;
    const long WS_EX_NOACTIVATE = 0x08000000;

    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
    public const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);

    static void SetFlag(Window w, long flag, bool on)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (h == IntPtr.Zero) return;
        var style = (long)GetWindowLongPtr(h, GWL_EXSTYLE);
        style = on ? style | flag : style & ~flag;
        SetWindowLongPtr(h, GWL_EXSTYLE, (IntPtr)style);
    }

    /// <summary>Mouse goes through the window into the game.</summary>
    public static void SetClickThrough(Window w, bool on) => SetFlag(w, WS_EX_TRANSPARENT, on);

    /// <summary>Clicks work but the game keeps focus.</summary>
    public static void SetNoActivate(Window w, bool on) => SetFlag(w, WS_EX_NOACTIVATE, on);

    public static void SetToolWindow(Window w) => SetFlag(w, WS_EX_TOOLWINDOW, true);

    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);

    /// <summary>Window stays on the monitor but is left out of screen captures (Win10 2004+).</summary>
    public static void ExcludeFromCapture(Window w) =>
        SetWindowDisplayAffinity(new WindowInteropHelper(w).Handle, 0x11);

    /// <summary>Back into screenshots and recordings (WDA_NONE).</summary>
    public static void IncludeInCapture(Window w) =>
        SetWindowDisplayAffinity(new WindowInteropHelper(w).Handle, 0);
}

/// <summary>Global hotkeys (RegisterHotKey) bound to a window's message loop.</summary>
public sealed class HotkeyHost : IDisposable
{
    readonly IntPtr _hwnd;
    readonly HwndSource _source;
    readonly Dictionary<int, Action> _actions = new();
    int _next = 0x5100;

    public HotkeyHost(Window w)
    {
        _hwnd = new WindowInteropHelper(w).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd);
        _source.AddHook(Hook);
    }

    public bool Register(uint mods, Key key, Action action)
    {
        var id = _next++;
        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (!Native.RegisterHotKey(_hwnd, id, mods | Native.MOD_NOREPEAT, vk)) return false;
        _actions[id] = action;
        return true;
    }

    public void Clear()
    {
        foreach (var id in _actions.Keys) Native.UnregisterHotKey(_hwnd, id);
        _actions.Clear();
    }

    IntPtr Hook(IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && _actions.TryGetValue(wp.ToInt32(), out var a))
        {
            a();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys) Native.UnregisterHotKey(_hwnd, id);
        _source.RemoveHook(Hook);
    }
}

/// <summary>Who is in front: the game, this overlay, or something else (a browser, the desktop).</summary>
public enum FrontWindow { Game, Overlay, Other }

public static class GameFocus
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    static readonly int OwnPid = Environment.ProcessId;
    static int _gamePid;
    static bool _running;
    static DateTime _runningCheckedAt;

    public static FrontWindow Front()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        if (pid == OwnPid) return FrontWindow.Overlay;
        if (pid != 0 && pid == _gamePid) return FrontWindow.Game;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            if (!IsGameName(p.ProcessName)) return FrontWindow.Other;
            _gamePid = (int)pid;
            return FrontWindow.Game;
        }
        catch (ArgumentException) { return FrontWindow.Other; }
        catch (InvalidOperationException) { return FrontWindow.Other; }
    }

    /// <summary>Is any PoE client running; the process list is read at most every 5 s.</summary>
    public static bool GameRunning()
    {
        if ((DateTime.UtcNow - _runningCheckedAt).TotalSeconds < 5) return _running;
        _runningCheckedAt = DateTime.UtcNow;
        var all = System.Diagnostics.Process.GetProcesses();
        _running = all.Any(p => IsGameName(p.ProcessName));
        foreach (var p in all) p.Dispose();
        return _running;
    }

    static bool IsGameName(string name) => name.StartsWith("PathOfExile", StringComparison.OrdinalIgnoreCase);
}
