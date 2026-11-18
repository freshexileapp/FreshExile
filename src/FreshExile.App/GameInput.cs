using System.Runtime.InteropServices;
using System.Windows;

namespace FreshExile.App;

/// <summary>
/// Copying the item under the mouse: one Ctrl+Alt+C sent to the game per press of the price check hotkey —
/// the same single keystroke the player would make, no clicks, nothing that acts in the game world.
/// </summary>
public static class GameInput
{
    const ushort VK_CONTROL = 0x11, VK_MENU = 0x12, VK_C = 0x43;
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
