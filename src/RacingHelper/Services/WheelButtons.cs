using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Interop;
using RacingHelper.Web;

namespace RacingHelper.App.Services;

/// <summary>
/// Buttons on wheels and button boxes (Moza, Fanatec, Simucube, …) via Windows Raw Input. Works while iRacing has
/// focus (RIDEV_INPUTSINK) and sees every button the device reports, not only the first 32.
/// </summary>
public sealed class WheelButtons : IDisposable
{
    const int WM_INPUT = 0x00FF;
    const uint RID_INPUT = 0x10000003, RIDI_PREPARSEDDATA = 0x20000005, RIDI_DEVICENAME = 0x20000007;
    const uint RIDEV_INPUTSINK = 0x00000100, RIM_TYPEHID = 2;
    const int HIDP_STATUS_SUCCESS = 0x00110000;
    const ushort UsagePageButton = 0x09;

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE { public ushort UsagePage; public ushort Usage; public uint Flags; public IntPtr Target; }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTHEADER { public uint Type; public uint Size; public IntPtr Device; public IntPtr WParam; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll")]
    static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, IntPtr data, ref uint size);
    [DllImport("hid.dll")]
    static extern int HidP_GetUsages(int reportType, ushort usagePage, ushort linkCollection, [Out] ushort[] usageList, ref uint usageLength, IntPtr preparsed, IntPtr report, uint reportLength);
    [DllImport("hid.dll")]
    static extern uint HidP_MaxUsageListLength(int reportType, ushort usagePage, IntPtr preparsed);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    static extern bool HidD_GetProductString(IntPtr device, StringBuilder buffer, int length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    sealed class Device
    {
        public IntPtr Preparsed;            // unmanaged copy, freed on dispose
        public uint MaxUsages;
        public string Key = "", Name = "";
        public HashSet<ushort> Down = new();
    }

    readonly HwndSource _src;
    readonly Dictionary<IntPtr, Device> _devices = new();
    TaskCompletionSource<ButtonPress?>? _learn;

    /// <summary>A button went down (not while learning). Raised on the UI thread.</summary>
    public event Action<ButtonPress>? Pressed;

    public WheelButtons()
    {
        _src = new HwndSource(new HwndSourceParameters("RacingHelperWheel") { Width = 0, Height = 0, WindowStyle = 0 });
        _src.AddHook(Hook);
        var size = (uint)Marshal.SizeOf<RAWINPUTDEVICE>();
        var regs = new[] { (ushort)0x04, (ushort)0x05, (ushort)0x08 }   // joystick, gamepad, multi-axis controller
            .Select(u => new RAWINPUTDEVICE { UsagePage = 0x01, Usage = u, Flags = RIDEV_INPUTSINK, Target = _src.Handle }).ToArray();
        if (!RegisterRawInputDevices(regs, (uint)regs.Length, size)) App.Log("Wheel buttons: RegisterRawInputDevices failed " + Marshal.GetLastWin32Error());
    }

    /// <summary>Waits for the next button press on any controller (for binding).</summary>
    public async Task<ButtonPress?> Learn(int timeoutMs)
    {
        var tcs = new TaskCompletionSource<ButtonPress?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _learn = tcs;
        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
        if (done != tcs.Task) { _learn = null; return null; }
        return await tcs.Task;
    }

    public IReadOnlyList<string> Controllers() => _devices.Values.Select(d => d.Name).Distinct().ToList();

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_INPUT) return IntPtr.Zero;
        try { OnInput(lParam); } catch (Exception ex) { App.Log("Wheel buttons: " + ex.Message); }
        return IntPtr.Zero;
    }

    void OnInput(IntPtr hRaw)
    {
        uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        uint size = 0;
        GetRawInputData(hRaw, RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0) return;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(hRaw, RID_INPUT, buf, ref size, headerSize) != size) return;
            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buf);
            if (header.Type != RIM_TYPEHID) return;
            var dev = DeviceFor(header.Device);
            if (dev == null) return;
            // RAWHID: DWORD dwSizeHid, DWORD dwCount, BYTE bRawData[dwSizeHid * dwCount]
            IntPtr hid = buf + (int)headerSize;
            uint reportSize = (uint)Marshal.ReadInt32(hid);
            uint count = (uint)Marshal.ReadInt32(hid, 4);
            IntPtr data = hid + 8;
            var down = new HashSet<ushort>();
            var usages = new ushort[Math.Max(1, dev.MaxUsages)];
            for (uint i = 0; i < count; i++)
            {
                uint n = (uint)usages.Length;
                if (HidP_GetUsages(0, UsagePageButton, 0, usages, ref n, dev.Preparsed, data + (int)(i * reportSize), reportSize) == HIDP_STATUS_SUCCESS)
                    for (int k = 0; k < n; k++) down.Add(usages[k]);
            }
            foreach (var b in down.Where(b => !dev.Down.Contains(b)).OrderBy(b => b))
            {
                var press = new ButtonPress(dev.Key, dev.Name, b);
                var learn = _learn;
                if (learn != null) { _learn = null; learn.TrySetResult(press); }
                else Pressed?.Invoke(press);
            }
            dev.Down = down;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    Device? DeviceFor(IntPtr handle)
    {
        if (_devices.TryGetValue(handle, out var d)) return d;
        uint size = 0;
        GetRawInputDeviceInfo(handle, RIDI_PREPARSEDDATA, IntPtr.Zero, ref size);
        if (size == 0) return null;
        IntPtr pp = Marshal.AllocHGlobal((int)size);
        if (GetRawInputDeviceInfo(handle, RIDI_PREPARSEDDATA, pp, ref size) == unchecked((uint)-1)) { Marshal.FreeHGlobal(pp); return null; }
        uint max = HidP_MaxUsageListLength(0, UsagePageButton, pp);

        string path = "";
        uint chars = 0;
        GetRawInputDeviceInfo(handle, RIDI_DEVICENAME, IntPtr.Zero, ref chars);
        if (chars > 0)
        {
            IntPtr nb = Marshal.AllocHGlobal((int)chars * 2 + 2);
            try { if (GetRawInputDeviceInfo(handle, RIDI_DEVICENAME, nb, ref chars) != unchecked((uint)-1)) path = Marshal.PtrToStringUni(nb) ?? ""; }
            finally { Marshal.FreeHGlobal(nb); }
        }
        var m = Regex.Match(path, @"VID_[0-9A-F]{4}&PID_[0-9A-F]{4}(&MI_[0-9A-F]{2})?(&Col[0-9A-F]{2})?", RegexOptions.IgnoreCase);
        string key = m.Success ? m.Value.ToUpperInvariant() : path;
        d = new Device { Preparsed = pp, MaxUsages = max, Key = key, Name = ProductName(path) ?? key };
        _devices[handle] = d;
        return d;
    }

    static string? ProductName(string path)
    {
        if (path.Length == 0) return null;
        IntPtr h = CreateFile(path, 0, 3 /* share read|write */, IntPtr.Zero, 3 /* open existing */, 0, IntPtr.Zero);
        if (h == IntPtr.Zero || h == new IntPtr(-1)) return null;
        try
        {
            var sb = new StringBuilder(128);
            return HidD_GetProductString(h, sb, sb.Capacity * 2) && sb.Length > 0 ? sb.ToString().Trim() : null;
        }
        finally { CloseHandle(h); }
    }

    public void Dispose()
    {
        _src.Dispose();
        foreach (var d in _devices.Values) Marshal.FreeHGlobal(d.Preparsed);
        _devices.Clear();
    }
}
