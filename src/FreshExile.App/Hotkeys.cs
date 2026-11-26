using System.Windows.Input;
using FreshExile.Core;

namespace FreshExile.App;

public sealed record HotkeyBinding(ModifierKeys Mods, Key Key)
{
    public uint NativeMods =>
        (Mods.HasFlag(ModifierKeys.Alt) ? Native.MOD_ALT : 0) |
        (Mods.HasFlag(ModifierKeys.Control) ? Native.MOD_CONTROL : 0) |
        (Mods.HasFlag(ModifierKeys.Shift) ? 0x4u : 0) |
        (Mods.HasFlag(ModifierKeys.Windows) ? 0x8u : 0);

    /// <summary>A plain key would be taken away from the game, so Ctrl or Alt is required.</summary>
    public bool IsSafe => Mods.HasFlag(ModifierKeys.Control) || Mods.HasFlag(ModifierKeys.Alt);

    /// <summary>Storage form: "Ctrl+Alt+Left".</summary>
    public string Serialize() => string.Join("+", ModNames().Append(Key.ToString()));

    public string Display => string.Join("+", ModNames().Append(KeyName(Key)));

    IEnumerable<string> ModNames()
    {
        if (Mods.HasFlag(ModifierKeys.Control)) yield return "Ctrl";
        if (Mods.HasFlag(ModifierKeys.Alt)) yield return "Alt";
        if (Mods.HasFlag(ModifierKeys.Shift)) yield return "Shift";
        if (Mods.HasFlag(ModifierKeys.Windows)) yield return "Win";
    }

    static string KeyName(Key k) => k switch
    {
        Key.Left => "←",
        Key.Right => "→",
        Key.Up => "↑",
        Key.Down => "↓",
        Key.PageUp => "PgUp",
        Key.PageDown => "PgDn",
        Key.Oem3 => "`",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        >= Key.D0 and <= Key.D9 => ((int)(k - Key.D0)).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num" + (int)(k - Key.NumPad0),
        _ => k.ToString(),
    };

    public static HotkeyBinding? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var mods = ModifierKeys.None;
        Key? key = null;
        foreach (var part in s.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": mods |= ModifierKeys.Control; break;
                case "alt": mods |= ModifierKeys.Alt; break;
                case "shift": mods |= ModifierKeys.Shift; break;
                case "win": mods |= ModifierKeys.Windows; break;
                default:
                    if (Enum.TryParse<Key>(part, true, out var k)) key = k;
                    else return null;
                    break;
            }
        }
        return key is { } kk ? new HotkeyBinding(mods, kk) : null;
    }

    public static bool IsModifierKey(Key k) => k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;
}

public static class HotkeyActions
{
    public const string Main = "main", Tree = "tree", Through = "through", Prev = "prev", Next = "next", Compact = "compact", Price = "price", Search = "search", Merc = "merc";

    public static readonly (string Id, string Title, string Default)[] All =
    [
        (Main, Loc.T("Показать / скрыть панель"), "Ctrl+Alt+G"),
        (Compact, Loc.T("Панель: компактно / полностью"), "Ctrl+Alt+C"),
        (Tree, Loc.T("Показать / скрыть дерево"), "Ctrl+Alt+T"),
        (Through, Loc.T("Дерево: сквозные клики"), "Ctrl+Alt+E"),
        (Prev, Loc.T("Предыдущий этап"), "Ctrl+Alt+Left"),
        (Next, Loc.T("Следующий этап"), "Ctrl+Alt+Right"),
        // not Ctrl+D: Awakened PoE Trade and Scalpel take it by default, and players often run one of them
        (Price, Loc.T("Прайс-чек предмета"), "Ctrl+Alt+D"),
        (Search, Loc.T("Поиск на трейде по названию"), "Ctrl+Alt+F"),
        (Merc, Loc.T("Прайс-чек наёмника по его окну"), "Ctrl+Alt+M"),
    ];
}

public sealed class HotkeyRowVm(string id, string title) : Observable
{
    HotkeyBinding? _binding;
    bool _capturing;
    string? _error;

    public string Id { get; } = id;
    public string Title { get; } = title;

    public HotkeyBinding? Binding
    {
        get => _binding;
        set { if (Set(ref _binding, value)) Raise(nameof(Text)); }
    }

    public bool IsCapturing
    {
        get => _capturing;
        set { if (Set(ref _capturing, value)) Raise(nameof(Text)); }
    }

    public string Text => IsCapturing ? Loc.T("Нажми сочетание…") : Binding?.Display ?? Loc.T("не назначено");

    public string? Error
    {
        get => _error;
        set { Set(ref _error, value); Raise(nameof(HasError)); }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);

    string? _clash;
    bool _isDefault = true;

    /// <summary>"Занято: Прайс-чек": another action has the same key (kept, drawn in red).</summary>
    public string? Clash { get => _clash; set { Set(ref _clash, value); Raise(nameof(HasClash)); } }
    public bool HasClash => !string.IsNullOrEmpty(_clash);

    /// <summary>The key is the action's default: no ↺ beside it.</summary>
    public bool IsDefault { get => _isDefault; set { Set(ref _isDefault, value); Raise(nameof(CanReset)); } }
    public bool CanReset => !_isDefault;
}
