using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace RacingHelper.App.Services;

/// <summary>System-wide hotkeys (work while iRacing has focus).</summary>
public sealed class Hotkeys : IDisposable
{
    public const uint Alt = 1, Ctrl = 2, Shift = 4, NoRepeat = 0x4000;
    public const uint F5 = 0x74, F6 = 0x75, F7 = 0x76, F8 = 0x77, F9 = 0x78, F10 = 0x79, F11 = 0x7A, F12 = 0x7B;
    const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    readonly HwndSource _src;
    readonly Dictionary<int, Action> _actions = new();

    public Hotkeys()
    {
        _src = new HwndSource(new HwndSourceParameters("RacingHelperHotkeys") { Width = 0, Height = 0, WindowStyle = 0 });
        _src.AddHook(Hook);
    }

    /// <summary>Parses "Ctrl+Shift+F5", "F13", "Alt+NumPad7", "Ctrl+K" into RegisterHotKey modifiers and a virtual-key code.</summary>
    public static bool TryParse(string spec, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        foreach (var raw in spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string p = raw.ToLowerInvariant();
            if (p is "ctrl" or "control") { mods |= Ctrl; continue; }
            if (p is "shift") { mods |= Shift; continue; }
            if (p is "alt") { mods |= Alt; continue; }
            string name = p.Length == 1 && char.IsDigit(p[0]) ? "D" + p : raw;
            if (!Enum.TryParse<System.Windows.Forms.Keys>(name, true, out var key) || key == System.Windows.Forms.Keys.None) return false;
            vk = (uint)key;
        }
        return vk != 0;
    }

    /// <param name="vk">Win32 virtual-key code (e.g. <see cref="F9"/>).</param>
    public bool Register(int id, uint modifiers, uint vk, Action action)
    {
        _actions[id] = action;
        return RegisterHotKey(_src.Handle, id, modifiers | NoRepeat, vk);
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var a))
        {
            handled = true;
            try { a(); } catch (Exception ex) { App.Log("Hotkey: " + ex.Message); }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_src.Handle, id);
        _src.Dispose();
    }
}
