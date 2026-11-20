using System.Runtime.InteropServices;
using System.Windows;

namespace FreshExile.App;

/// <summary>
/// Copying the item under the mouse: one Ctrl+Alt+C sent to the game per press of the price check hotkey —
/// the same single keystroke the player would make, no clicks, nothing that acts in the game world.
/// </summary>
public static class GameInput
{
    const ushort VK_CONTROL = 0x11, VK_MENU = 0x12, VK_C = 0x43, VK_V = 0x56, VK_RETURN = 0x0D, VK_SHIFT = 0x10;
    const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    // the union is as large as MOUSEINPUT: pad the keyboard variant to that size
    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public MOUSEPAD pad; }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEPAD { public int dx, dy; public uint data, flags, time; public IntPtr extra; }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public InputUnion u; }

    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);

    static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        // the scan code too: the game reads raw input, where a key without one may not count
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = (ushort)MapVirtualKey(vk, 0), dwFlags = up ? KEYEVENTF_KEYUP : 0 } },
    };

    static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>
    /// Ctrl+Alt+C into the game (the advanced copy, with mod tiers), the item text read back from the clipboard,
    /// whatever the clipboard held before put back. null when the game copied nothing (no item under the mouse).
    /// </summary>
    public static async Task<string?> CopyItemAsync()
    {
        var before = GetClipboardSequenceNumber();
        var old = ReadClipboard();

        // modifiers the player is already holding (the hotkey's Ctrl) are left alone, the rest pressed and released
        var keys = new List<INPUT>();
        var ctrl = !Down(VK_CONTROL);
        var alt = !Down(VK_MENU);
        if (ctrl) keys.Add(Key(VK_CONTROL, false));
        if (alt) keys.Add(Key(VK_MENU, false));
        keys.Add(Key(VK_C, false));
        keys.Add(Key(VK_C, true));
        if (alt) keys.Add(Key(VK_MENU, true));
        if (ctrl) keys.Add(Key(VK_CONTROL, true));
        SendInput((uint)keys.Count, keys.ToArray(), Marshal.SizeOf<INPUT>());

        string? text = null;
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(20);
            if (GetClipboardSequenceNumber() == before) continue;
            text = ReadClipboard();
            if (text != null) break;
        }
        if (text != null && old != null) TrySet(old);
        return text;
    }

    /// <summary>
    /// A seller's message into the game's chat at the player's press: the game brought to the front, then Enter (opens the
    /// chat), Ctrl+V, Enter (sends) — what the player would type, one message per press. The clipboard is put back after.
    /// </summary>
    public static async Task<bool> SendChatAsync(IntPtr game, string text)
    {
        if (game == IntPtr.Zero) { TrySet(text); return false; }
        var old = ReadClipboard();
        TrySet(text);
        SetForegroundWindow(game);
        // the game takes the focus a moment later; a key sent before that lands in our own window
        for (var i = 0; i < 15 && GetForegroundWindow() != game; i++) await Task.Delay(20);
        if (GetForegroundWindow() != game) return false;
        await Task.Delay(60);

        var keys = new List<INPUT>();
        // a modifier still held (Shift, Ctrl) would turn Enter into something else
        foreach (var vk in new[] { VK_SHIFT, VK_CONTROL, VK_MENU })
            if (Down(vk)) keys.Add(Key(vk, true));
        keys.Add(Key(VK_RETURN, false));
        keys.Add(Key(VK_RETURN, true));
        Send(keys);
        await Task.Delay(50);
        Send([Key(VK_CONTROL, false), Key(VK_V, false), Key(VK_V, true), Key(VK_CONTROL, true)]);
        await Task.Delay(50);
        Send([Key(VK_RETURN, false), Key(VK_RETURN, true)]);

        // the game reads the clipboard on Ctrl+V: what the player had there comes back only after it
        await Task.Delay(250);
        if (old != null) TrySet(old);
        return true;
    }

    static void Send(List<INPUT> keys) => SendInput((uint)keys.Count, keys.ToArray(), Marshal.SizeOf<INPUT>());

    /// <summary>The game's main window, whether in front or not; zero when no client runs.</summary>
    public static IntPtr FindGameWindow()
    {
        var all = System.Diagnostics.Process.GetProcesses();
        try
        {
            return all.Where(p => p.ProcessName.StartsWith("PathOfExile", StringComparison.OrdinalIgnoreCase))
                .Select(p => { try { return p.MainWindowHandle; } catch (InvalidOperationException) { return IntPtr.Zero; } })
                .FirstOrDefault(h => h != IntPtr.Zero);
        }
        finally { foreach (var p in all) p.Dispose(); }
    }

    public static string? ReadClipboard()
    {
        for (var i = 0; i < 3; i++)
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch (COMException) { Thread.Sleep(30); }
        }
        return null;
    }

    static void TrySet(string text)
    {
        for (var i = 0; i < 3; i++)
        {
            try { Clipboard.SetText(text); return; }
            catch (COMException) { Thread.Sleep(30); }
        }
    }
}
