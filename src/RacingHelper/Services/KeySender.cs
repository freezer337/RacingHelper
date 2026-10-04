using System.Runtime.InteropServices;
using System.Text;
using RacingHelper.Live;
using Keys = System.Windows.Forms.Keys;

namespace RacingHelper.App.Services;

/// <summary>
/// Presses keys in iRacing with SendInput using hardware scan codes (iRacing reads the keyboard through DirectInput,
/// which ignores virtual-key-only input). Only presses while iRacing is the active window.
/// </summary>
public sealed class KeySender : IKeySender
{
    const uint INPUT_KEYBOARD = 1, KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_SCANCODE = 0x8;

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort Vk; public ushort Scan; public uint Flags; public uint Time; public IntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int Dx; public int Dy; public uint MouseData; public uint Flags; public uint Time; public IntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion { [FieldOffset(0)] public MOUSEINPUT Mouse; [FieldOffset(0)] public KEYBDINPUT Key; }
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint Type; public InputUnion U; }

    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);

    static readonly HashSet<Keys> Extended = new()
    {
        Keys.Insert, Keys.Delete, Keys.Home, Keys.End, Keys.PageUp, Keys.PageDown, Keys.Up, Keys.Down, Keys.Left, Keys.Right,
        Keys.Divide, Keys.NumLock, Keys.RControlKey, Keys.RMenu,
    };

    public bool Press(string keySpec, out string? error)
    {
        error = null;
        if (!TryParse(keySpec, out var mods, out var key)) { error = $"I don't understand the key \"{keySpec}\". Use names like F13, NumPad7, T or Ctrl+Shift+T."; return false; }
        if (!IRacingInFront()) { error = "iRacing has to be the active window for me to press its keys."; return false; }
        var down = mods.Select(m => Make(m, false)).Append(Make(key, false)).ToArray();
        var up = new[] { Make(key, true) }.Concat(mods.AsEnumerable().Reverse().Select(m => Make(m, true))).ToArray();
        int size = Marshal.SizeOf<INPUT>();
        if (SendInput((uint)down.Length, down, size) != down.Length) { error = "Windows refused the key press."; return false; }
        Thread.Sleep(45);   // held for a few sim frames
        SendInput((uint)up.Length, up, size);
        return true;
    }

    static INPUT Make(Keys k, bool release)
    {
        uint flags = KEYEVENTF_SCANCODE | (release ? KEYEVENTF_KEYUP : 0) | (Extended.Contains(k) ? KEYEVENTF_EXTENDEDKEY : 0);
        return new INPUT { Type = INPUT_KEYBOARD, U = new InputUnion { Key = new KEYBDINPUT { Scan = (ushort)MapVirtualKey((uint)k, 0), Flags = flags } } };
    }

    public static bool TryParse(string spec, out List<Keys> mods, out Keys key)
    {
        mods = new(); key = Keys.None;
        foreach (var raw in spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string p = raw.ToLowerInvariant();
            if (p is "ctrl" or "control") { mods.Add(Keys.ControlKey); continue; }
            if (p is "shift") { mods.Add(Keys.ShiftKey); continue; }
            if (p is "alt") { mods.Add(Keys.Menu); continue; }
            string name = p.Length == 1 && char.IsDigit(p[0]) ? "D" + p : p.StartsWith("num") && !p.StartsWith("numpad") && p.Length > 3 ? "NumPad" + p[3..] : raw;
            if (!Enum.TryParse(name, true, out key) || key == Keys.None) return false;
        }
        return key != Keys.None;
    }

    static bool IRacingInFront()
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero) return false;
        var cls = new StringBuilder(128); GetClassName(h, cls, cls.Capacity);
        var title = new StringBuilder(256); GetWindowText(h, title, title.Capacity);
        return cls.ToString() == "SimWinClass" || title.ToString().Contains("iRacing", StringComparison.OrdinalIgnoreCase);
    }
}
