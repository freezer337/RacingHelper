namespace RacingHelper.Sim;

public struct TyreFrame
{
    public float Pressure, ColdPressure;            // kPa
    public float TempL, TempM, TempR;               // surface temps across tread (C), sim's left→right
    public float TempCL, TempCM, TempCR;            // carcass temps (C)
    public float WearL, WearM, WearR;               // fraction remaining 0..1
    public float WheelSpeed;                        // m/s
    public float RideHeight, ShockDefl;             // m

    public static TyreFrame Empty => new()
    {
        Pressure = float.NaN, ColdPressure = float.NaN, TempL = float.NaN, TempM = float.NaN, TempR = float.NaN,
        TempCL = float.NaN, TempCM = float.NaN, TempCR = float.NaN, WearL = float.NaN, WearM = float.NaN, WearR = float.NaN,
        WheelSpeed = float.NaN, RideHeight = float.NaN, ShockDefl = float.NaN,
    };
}

/// <summary>
/// One telemetry sample. Fields missing from a given source (live vs .ibt) stay NaN / default.
/// The instance is reused by its source between ticks, so consumers must copy what they keep.
/// </summary>
public sealed class Frame
{
    public const int MaxCars = 64;
    public static readonly string[] TyreNames = { "LF", "RF", "LR", "RR" };

    // session
    public double SessionTime;
    public int SessionNum, SessionState, SessionUniqueID;
    public uint SessionFlags;
    public double SessionTimeRemain = double.NaN;
    public int SessionLapsRemainEx = -1;
    public float SessionTimeOfDay = float.NaN;
    public bool IsOnTrack, IsOnTrackCar, IsReplayPlaying, OnPitRoad, PlayerCarInPitStall;

    // player
    public int PlayerTrackSurface = Irsdk.TrkLoc.OnTrack, PlayerCarIdx, PlayerCarMyIncidentCount, PlayerCarTeamIncidentCount;
    public int PlayerCarPosition, PlayerCarClassPosition, PlayerTireCompound = -1;
    public float PlayerCarTowTime;
    public int Lap, LapCompleted;
    public float LapDist = float.NaN, LapDistPct, LapCurrentLapTime = float.NaN, LapLastLapTime = float.NaN, LapBestLapTime = float.NaN;
    public float LapDeltaToBestLap = float.NaN;

    // driving
    public float Speed, Throttle, Brake, Clutch, Steer, SteerMax = float.NaN, RPM;
    public int Gear;
    public float LatAccel, LongAccel, VertAccel, YawRate, Yaw, YawNorth, Pitch, Roll, VelX, VelY;
    public double Lat = double.NaN, Lon = double.NaN;
    public float Alt = float.NaN;
    public bool AbsActive;
    public float BrakeBias = float.NaN, TcSetting = float.NaN, AbsSetting = float.NaN;

    // engine / fuel
    public float FuelLevel = float.NaN, FuelLevelPct = float.NaN, FuelUsePerHour = float.NaN;
    public float WaterTemp = float.NaN, OilTemp = float.NaN, OilPress = float.NaN, FuelPress = float.NaN, Voltage = float.NaN;
    public uint EngineWarnings;

    public readonly TyreFrame[] Tyres = { TyreFrame.Empty, TyreFrame.Empty, TyreFrame.Empty, TyreFrame.Empty };

    // weather
    public float AirTemp = float.NaN, TrackTemp = float.NaN, Precipitation = float.NaN, WindVel = float.NaN, WindDir = float.NaN;
    public float Humidity = float.NaN, FogLevel = float.NaN, AirPressure = float.NaN;
    public int Skies = -1, TrackWetness = -1;
    public bool DeclaredWet;

    // proximity
    public int CarLeftRight;
    public float CarDistAhead = float.NaN, CarDistBehind = float.NaN;

    // pit service
    public uint PitSvFlags;
    public float PitSvFuel = float.NaN;
    public readonly float[] PitSvPressure = { float.NaN, float.NaN, float.NaN, float.NaN };
    public int TireSetsUsed = -1, FastRepairUsed = -1;
    public float PitRepairLeft = float.NaN, PitOptRepairLeft = float.NaN; // seconds of mandatory / optional repair = damage indicator
    public bool DiskLoggingEnabled, DiskLoggingActive;
    public bool HasDiskLoggingVar;

    // other cars (live only)
    public bool HasCarIdx;
    public readonly int[] CarIdxLap = new int[MaxCars], CarIdxLapCompleted = new int[MaxCars], CarIdxPosition = new int[MaxCars];
    public readonly int[] CarIdxClassPosition = new int[MaxCars], CarIdxTrackSurface = new int[MaxCars], CarIdxTireCompound = new int[MaxCars];
    public readonly int[] CarIdxGear = new int[MaxCars];
    public readonly float[] CarIdxLapDistPct = new float[MaxCars], CarIdxF2Time = new float[MaxCars], CarIdxEstTime = new float[MaxCars];
    public readonly float[] CarIdxLastLapTime = new float[MaxCars], CarIdxBestLapTime = new float[MaxCars];
    public readonly bool[] CarIdxOnPitRoad = new bool[MaxCars];

    public bool HasGps => !double.IsNaN(Lat) && !double.IsNaN(Lon) && (Lat != 0 || Lon != 0);
    public bool Flag(Irsdk.Flags f) => (SessionFlags & (uint)f) != 0;
}

/// <summary>Binds variable offsets once per var map, then fills a <see cref="Frame"/> from a raw buffer each tick.</summary>
public sealed class FrameReader
{
    delegate void Setter(Frame f, ReadOnlySpan<byte> b);
    readonly List<Setter> _setters = new();
    public VarMap Vars { get; }

    public FrameReader(VarMap vars)
    {
        Vars = vars;
        D("SessionTime", (f, v) => f.SessionTime = v);
        I("SessionNum", (f, v) => f.SessionNum = v);
        I("SessionState", (f, v) => f.SessionState = v);
        I("SessionUniqueID", (f, v) => f.SessionUniqueID = v);
        D("SessionFlags", (f, v) => f.SessionFlags = (uint)v);
        D("SessionTimeRemain", (f, v) => f.SessionTimeRemain = v);
        I("SessionLapsRemainEx", (f, v) => f.SessionLapsRemainEx = v);
        F("SessionTimeOfDay", (f, v) => f.SessionTimeOfDay = v);
        B("IsOnTrack", (f, v) => f.IsOnTrack = v);
        B("IsOnTrackCar", (f, v) => f.IsOnTrackCar = v);
        B("IsReplayPlaying", (f, v) => f.IsReplayPlaying = v);
        B("OnPitRoad", (f, v) => f.OnPitRoad = v);
        B("PlayerCarInPitStall", (f, v) => f.PlayerCarInPitStall = v);
        I("PlayerTrackSurface", (f, v) => f.PlayerTrackSurface = v);
        I("PlayerCarIdx", (f, v) => f.PlayerCarIdx = v);
        I("PlayerCarMyIncidentCount", (f, v) => f.PlayerCarMyIncidentCount = v);
        I("PlayerCarTeamIncidentCount", (f, v) => f.PlayerCarTeamIncidentCount = v);
        I("PlayerCarPosition", (f, v) => f.PlayerCarPosition = v);
        I("PlayerCarClassPosition", (f, v) => f.PlayerCarClassPosition = v);
        I("PlayerTireCompound", (f, v) => f.PlayerTireCompound = v);
        F("PlayerCarTowTime", (f, v) => f.PlayerCarTowTime = v);
        I("Lap", (f, v) => f.Lap = v);
        I("LapCompleted", (f, v) => f.LapCompleted = v);
        F("LapDist", (f, v) => f.LapDist = v);
        F("LapDistPct", (f, v) => f.LapDistPct = v);
        F("LapCurrentLapTime", (f, v) => f.LapCurrentLapTime = v);
        F("LapLastLapTime", (f, v) => f.LapLastLapTime = v);
        F("LapBestLapTime", (f, v) => f.LapBestLapTime = v);
        F("LapDeltaToBestLap", (f, v) => f.LapDeltaToBestLap = v);

        F("Speed", (f, v) => f.Speed = v);
        F("Throttle", (f, v) => f.Throttle = v);
        F("Brake", (f, v) => f.Brake = v);
        F("Clutch", (f, v) => f.Clutch = v);
        F("SteeringWheelAngle", (f, v) => f.Steer = v);
        F("SteeringWheelAngleMax", (f, v) => f.SteerMax = v);
        F("RPM", (f, v) => f.RPM = v);
        I("Gear", (f, v) => f.Gear = v);
        F("LatAccel", (f, v) => f.LatAccel = v);
        F("LongAccel", (f, v) => f.LongAccel = v);
        F("VertAccel", (f, v) => f.VertAccel = v);
        F("YawRate", (f, v) => f.YawRate = v);
        F("Yaw", (f, v) => f.Yaw = v);
        F("YawNorth", (f, v) => f.YawNorth = v);
        F("Pitch", (f, v) => f.Pitch = v);
        F("Roll", (f, v) => f.Roll = v);
        F("VelocityX", (f, v) => f.VelX = v);
        F("VelocityY", (f, v) => f.VelY = v);
        D("Lat", (f, v) => f.Lat = v);
        D("Lon", (f, v) => f.Lon = v);
        F("Alt", (f, v) => f.Alt = v);
        B("BrakeABSactive", (f, v) => f.AbsActive = v);
        F("dcBrakeBias", (f, v) => f.BrakeBias = v);
        F("dcTractionControl", (f, v) => f.TcSetting = v);
        F("dcABS", (f, v) => f.AbsSetting = v);

        F("FuelLevel", (f, v) => f.FuelLevel = v);
        F("FuelLevelPct", (f, v) => f.FuelLevelPct = v);
        F("FuelUsePerHour", (f, v) => f.FuelUsePerHour = v);
        F("WaterTemp", (f, v) => f.WaterTemp = v);
        F("OilTemp", (f, v) => f.OilTemp = v);
        F("OilPress", (f, v) => f.OilPress = v);
        F("FuelPress", (f, v) => f.FuelPress = v);
        F("Voltage", (f, v) => f.Voltage = v);
        D("EngineWarnings", (f, v) => f.EngineWarnings = (uint)v);

        for (int t = 0; t < 4; t++)
        {
            int i = t;
            string p = Frame.TyreNames[t];
            F(p + "pressure", (f, v) => f.Tyres[i].Pressure = v);
            F(p + "coldPressure", (f, v) => f.Tyres[i].ColdPressure = v);
            F(p + "tempL", (f, v) => f.Tyres[i].TempL = v);
            F(p + "tempM", (f, v) => f.Tyres[i].TempM = v);
            F(p + "tempR", (f, v) => f.Tyres[i].TempR = v);
            F(p + "tempCL", (f, v) => f.Tyres[i].TempCL = v);
            F(p + "tempCM", (f, v) => f.Tyres[i].TempCM = v);
            F(p + "tempCR", (f, v) => f.Tyres[i].TempCR = v);
            F(p + "wearL", (f, v) => f.Tyres[i].WearL = v);
            F(p + "wearM", (f, v) => f.Tyres[i].WearM = v);
            F(p + "wearR", (f, v) => f.Tyres[i].WearR = v);
            F(p + "speed", (f, v) => f.Tyres[i].WheelSpeed = v);
            F(p + "rideHeight", (f, v) => f.Tyres[i].RideHeight = v);
            F(p + "shockDefl", (f, v) => f.Tyres[i].ShockDefl = v);
            F("PitSv" + p + "P", (f, v) => f.PitSvPressure[i] = v);
        }

        F("AirTemp", (f, v) => f.AirTemp = v);
        F(vars.Has("TrackTempCrew") ? "TrackTempCrew" : "TrackTemp", (f, v) => f.TrackTemp = v);
        F("Precipitation", (f, v) => f.Precipitation = v);
        F("WindVel", (f, v) => f.WindVel = v);
        F("WindDir", (f, v) => f.WindDir = v);
        F("RelativeHumidity", (f, v) => f.Humidity = v);
        F("FogLevel", (f, v) => f.FogLevel = v);
        F("AirPressure", (f, v) => f.AirPressure = v);
        I("Skies", (f, v) => f.Skies = v);
        I("TrackWetness", (f, v) => f.TrackWetness = v);
        B("WeatherDeclaredWet", (f, v) => f.DeclaredWet = v);

        I("CarLeftRight", (f, v) => f.CarLeftRight = v);
        F("CarDistAhead", (f, v) => f.CarDistAhead = v);
        F("CarDistBehind", (f, v) => f.CarDistBehind = v);
        D("PitSvFlags", (f, v) => f.PitSvFlags = (uint)v);
        F("PitSvFuel", (f, v) => f.PitSvFuel = v);
        I("TireSetsUsed", (f, v) => f.TireSetsUsed = v);
        I("FastRepairUsed", (f, v) => f.FastRepairUsed = v);
        F("PitRepairLeft", (f, v) => f.PitRepairLeft = v);
        F("PitOptRepairLeft", (f, v) => f.PitOptRepairLeft = v);
        B("DiskLoggingEnabled", (f, v) => { f.DiskLoggingEnabled = v; f.HasDiskLoggingVar = true; });
        B("DiskLoggingActive", (f, v) => f.DiskLoggingActive = v);

        if (vars.Has("CarIdxLapDistPct"))
        {
            _setters.Add((f, _) => f.HasCarIdx = true);
            IA("CarIdxLap", f => f.CarIdxLap);
            IA("CarIdxLapCompleted", f => f.CarIdxLapCompleted);
            IA("CarIdxPosition", f => f.CarIdxPosition);
            IA("CarIdxClassPosition", f => f.CarIdxClassPosition);
            IA("CarIdxTrackSurface", f => f.CarIdxTrackSurface);
            IA("CarIdxTireCompound", f => f.CarIdxTireCompound);
            IA("CarIdxGear", f => f.CarIdxGear);
            FA("CarIdxLapDistPct", f => f.CarIdxLapDistPct);
            FA("CarIdxF2Time", f => f.CarIdxF2Time);
            FA("CarIdxEstTime", f => f.CarIdxEstTime);
            FA("CarIdxLastLapTime", f => f.CarIdxLastLapTime);
            FA("CarIdxBestLapTime", f => f.CarIdxBestLapTime);
            BA("CarIdxOnPitRoad", f => f.CarIdxOnPitRoad);
        }
    }

    public void Read(ReadOnlySpan<byte> buffer, Frame frame)
    {
        foreach (var s in _setters) s(frame, buffer);
    }

    void F(string name, Action<Frame, float> set)
    {
        var v = Vars.Get(name); if (v == null) return;
        _setters.Add((f, b) => set(f, (float)VarMap.Read(b, v)));
    }
    void D(string name, Action<Frame, double> set)
    {
        var v = Vars.Get(name); if (v == null) return;
        _setters.Add((f, b) => set(f, VarMap.Read(b, v)));
    }
    void I(string name, Action<Frame, int> set)
    {
        var v = Vars.Get(name); if (v == null) return;
        _setters.Add((f, b) => { var d = VarMap.Read(b, v); if (!double.IsNaN(d)) set(f, (int)d); });
    }
    void B(string name, Action<Frame, bool> set)
    {
        var v = Vars.Get(name); if (v == null) return;
        _setters.Add((f, b) => set(f, VarMap.Read(b, v) != 0));
    }
    void FA(string name, Func<Frame, float[]> arr)
    {
        var v = Vars.Get(name); if (v == null) return;
        _setters.Add((f, b) => { var a = arr(f); int n = Math.Min(a.Length, v.Count); for (int i = 0; i < n; i++) a[i] = (float)VarMap.Read(b, v, i); });
    }
    void IA(string name, Func<Frame, int[]> arr)
    {
        var v = Vars.Get(name); if (v == null) return;
        _setters.Add((f, b) => { var a = arr(f); int n = Math.Min(a.Length, v.Count); for (int i = 0; i < n; i++) a[i] = (int)VarMap.Read(b, v, i); });
    }
    void BA(string name, Func<Frame, bool[]> arr)
    {
        var v = Vars.Get(name); if (v == null) return;
        _setters.Add((f, b) => { var a = arr(f); int n = Math.Min(a.Length, v.Count); for (int i = 0; i < n; i++) a[i] = VarMap.Read(b, v, i) != 0; });
    }
}
