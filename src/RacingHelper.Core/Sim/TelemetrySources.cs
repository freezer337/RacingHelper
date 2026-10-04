using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace RacingHelper.Sim;

public enum SourceStatus { Frame, NoData, Disconnected, Ended }

public interface ITelemetrySource : IDisposable
{
    string Name { get; }
    bool IsLive { get; }
    Frame Frame { get; }
    SessionInfo? Session { get; }
    /// <summary>Increments whenever <see cref="Session"/> is replaced.</summary>
    int SessionVersion { get; }
    VarMap? Vars { get; }
    SourceStatus Next(int timeoutMs);
    DateTime WallClock { get; }
}

/// <summary>Live connection to the iRacing simulator through its shared memory map.</summary>
public sealed unsafe class IRacingLiveSource : ITelemetrySource
{
    const string MapName = "Local\\IRSDKMemMapFileName";
    const string EventName = "Local\\IRSDKDataValidEvent";

    MemoryMappedFile? _mmf;
    MemoryMappedViewAccessor? _view;
    byte* _ptr;
    EventWaitHandle? _event;
    FrameReader? _reader;
    int _lastSessionUpdate = -1, _lastTick = -1, _varsSignature;
    byte[] _buf = Array.Empty<byte>();
    DateTime _lastConnectAttempt = DateTime.MinValue;

    public string Name => "iRacing";
    public bool IsLive => true;
    public Frame Frame { get; } = new();
    public SessionInfo? Session { get; private set; }
    public int SessionVersion { get; private set; }
    public VarMap? Vars => _reader?.Vars;
    public DateTime WallClock => DateTime.Now;
    public bool Connected { get; private set; }

    bool TryOpen()
    {
        if (_ptr != null) return true;
        if ((DateTime.Now - _lastConnectAttempt).TotalSeconds < 1) return false;
        _lastConnectAttempt = DateTime.Now;
        try
        {
            _mmf = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
            _view = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _ptr);
            _ptr += _view.PointerOffset;
            // the data-ready event is a nicety; without it we simply poll for new ticks
            try { _event = EventWaitHandle.OpenExisting(EventName); } catch { _event = null; }
            return true;
        }
        catch
        {
            Close();
            return false;
        }
    }

    void Close()
    {
        if (_ptr != null && _view != null) _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _ptr = null;
        _view?.Dispose(); _view = null;
        _mmf?.Dispose(); _mmf = null;
        _event?.Dispose(); _event = null;
        _reader = null;
        _lastSessionUpdate = -1;
        _lastTick = -1;
        Connected = false;
    }

    IrsdkHeader ReadHeader() => IrsdkHeader.Parse(new ReadOnlySpan<byte>(_ptr, IrsdkHeader.Size));

    public SourceStatus Next(int timeoutMs)
    {
        if (!TryOpen()) { Thread.Sleep(Math.Min(timeoutMs, 250)); return SourceStatus.Disconnected; }

        if (_event != null) _event.WaitOne(timeoutMs);
        else Thread.Sleep(8);
        var h = ReadHeader();
        if (!h.Connected)
        {
            if (Connected) { Connected = false; Session = null; SessionVersion++; }
            return SourceStatus.Disconnected;
        }
        Connected = true;

        int sig = h.NumVars * 31 + h.VarHeaderOffset * 7 + h.BufLen;
        if (_reader == null || sig != _varsSignature)
        {
            var vh = new ReadOnlySpan<byte>(_ptr + h.VarHeaderOffset, h.NumVars * IrsdkHeader.VarHeaderSize);
            _reader = new FrameReader(VarMap.Parse(vh, h.NumVars));
            _varsSignature = sig;
            _buf = new byte[h.BufLen];
        }

        if (h.SessionInfoUpdate != _lastSessionUpdate)
        {
            string yaml = Irsdk.DecodeYaml(new ReadOnlySpan<byte>(_ptr + h.SessionInfoOffset, h.SessionInfoLen));
            try
            {
                Session = new SessionInfo(yaml);
                SessionVersion++;
                _lastSessionUpdate = h.SessionInfoUpdate;
            }
            catch { /* partial write; retry on next tick */ }
        }

        // Copy the newest buffer and make sure the sim did not overwrite it mid-copy.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int bi = h.LatestBuffer();
            int tick = h.BufTick[bi];
            if (tick == _lastTick) return SourceStatus.NoData;
            new ReadOnlySpan<byte>(_ptr + h.BufOffset[bi], h.BufLen).CopyTo(_buf);
            var check = ReadHeader();
            if (check.BufTick[bi] == tick)
            {
                _lastTick = tick;
                _reader.Read(_buf, Frame);
                return SourceStatus.Frame;
            }
            h = check;
        }
        return SourceStatus.NoData;
    }

    public void Dispose() => Close();
}

/// <summary>Plays an .ibt file back as if it were live (optionally in real time). Used for import and demo replay.</summary>
public sealed class IbtSource : ITelemetrySource
{
    readonly IbtFile _file;
    readonly FrameReader _reader;
    readonly byte[] _buf;
    readonly IEnumerator<int> _records;
    readonly double _speed;
    readonly System.Diagnostics.Stopwatch _clock = new();
    double _firstSessionTime = double.NaN;

    public string Name => "Replay: " + System.IO.Path.GetFileName(_file.Path);
    public bool IsLive => false;
    public Frame Frame { get; } = new();
    public SessionInfo? Session { get; }
    public int SessionVersion => 1;
    public VarMap? Vars => _file.Vars;
    public IbtFile File => _file;
    public int Index { get; private set; } = -1;
    public DateTime WallClock => _file.SessionStartLocal.AddSeconds(Math.Max(0, Frame.SessionTime - _file.StartTime));

    /// <param name="realtimeSpeed">0 = as fast as possible, 1 = real time, 2 = double speed...</param>
    public IbtSource(string path, double realtimeSpeed = 0, int startRecord = 0)
    {
        _file = IbtFile.Open(path);
        _reader = new FrameReader(_file.Vars);
        _buf = new byte[_file.Header.BufLen];
        _records = _file.Records(_buf, startRecord).GetEnumerator();
        _speed = realtimeSpeed;
        Session = new SessionInfo(_file.SessionYaml);
    }

    public SourceStatus Next(int timeoutMs)
    {
        if (!_records.MoveNext()) return SourceStatus.Ended;
        Index = _records.Current;
        _reader.Read(_buf, Frame);
        if (_speed > 0)
        {
            if (double.IsNaN(_firstSessionTime)) { _firstSessionTime = Frame.SessionTime; _clock.Restart(); }
            double target = (Frame.SessionTime - _firstSessionTime) / _speed;
            double wait = target - _clock.Elapsed.TotalSeconds;
            if (wait > 0.001) Thread.Sleep(TimeSpan.FromSeconds(Math.Min(wait, 0.5)));
        }
        return SourceStatus.Frame;
    }

    public void Dispose()
    {
        _records.Dispose();
        _file.Dispose();
    }
}

/// <summary>Sends irsdk broadcast messages to the simulator (pit commands, telemetry recording...).</summary>
public static class IRacingBroadcast
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string lpString);
    [DllImport("user32.dll")] static extern bool SendNotifyMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    static readonly IntPtr HwndBroadcast = new(0xFFFF);
    static uint _msgId;

    public enum Msg { CamSwitchPos = 0, CamSwitchNum, CamSetState, ReplaySetPlaySpeed, ReplaySetPlayPosition, ReplaySearch, ReplaySetState, ReloadTextures, ChatCommand, PitCommand, TelemCommand, FFBCommand, ReplaySearchSessionTime, VideoCapture }
    public enum PitCmd { Clear = 0, WS, Fuel, LF, RF, LR, RR, ClearTires, FR, ClearWS, ClearFR, ClearFuel, TC }
    public enum TelemCmd { Stop = 0, Start, Restart }

    static bool Send(Msg msg, int var1, int var2)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (_msgId == 0) _msgId = RegisterWindowMessage("IRSDK_BROADCASTMSG");
        if (_msgId == 0) return false;
        int wParam = ((int)msg & 0xFFFF) | ((var1 & 0xFFFF) << 16);
        return SendNotifyMessage(HwndBroadcast, _msgId, new IntPtr(wParam), new IntPtr(var2));
    }

    /// <summary>Requests a tyre change with the given cold pressure (kPa) at the next stop. tyre: 0 LF, 1 RF, 2 LR, 3 RR.</summary>
    public static bool SetTyrePressure(int tyre, double kpa) => Send(Msg.PitCommand, (int)PitCmd.LF + tyre, (int)Math.Round(kpa));
    public static bool SetFuel(double litres) => Send(Msg.PitCommand, (int)PitCmd.Fuel, (int)Math.Ceiling(litres));
    public static bool StartDiskTelemetry() => Send(Msg.TelemCommand, (int)TelemCmd.Start, 0);
}
