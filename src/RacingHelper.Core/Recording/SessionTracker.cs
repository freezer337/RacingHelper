using RacingHelper.Sim;

namespace RacingHelper.Recording;

public sealed class SessionContext
{
    public required string Key { get; init; }
    public required SessionInfo Info { get; set; }
    public required int SessionNum { get; init; }
    public required DateTime StartedAt { get; init; }
    public string SessionType => Info.Session(SessionNum)?.Type ?? "";
    public string SessionName => Info.Session(SessionNum)?.Name ?? "";
    public List<RecordedLap> Laps { get; } = new();
    public List<PitStopInfo> PitStops { get; } = new();
    public long DbId { get; set; }
}

public sealed class PitStopInfo
{
    public int Lap;
    public DateTime At;
    public double StationaryTime;
    public float FuelAdded;
    public bool TyresChanged;
}

/// <summary>
/// Turns a stream of frames into sessions, stints, laps and pit stops.
/// Works identically for live telemetry and .ibt import.
/// </summary>
public sealed class SessionTracker
{
    public event Action<SessionContext>? SessionStarted;
    public event Action<SessionContext>? SessionEnded;
    public event Action<SessionContext, RecordedLap>? LapCompleted;
    public event Action<SessionContext, PitStopInfo>? PitStopCompleted;
    public event Action<SessionContext>? StintStarted;

    public SessionContext? Current { get; private set; }
    public int Stint { get; private set; }
    public bool LapInProgress => _lap != null;
    public double CurrentLapStartTime => _lap?.StartTime ?? double.NaN;
    public bool CurrentLapIsOut => _lap?.OutLap ?? false;
    public int CurrentLapIncidents(Frame f) => _lap == null ? 0 : f.PlayerCarMyIncidentCount - _lap.StartIncidents;
    public int CurrentLapOffTracks => _lap?.OffTracks ?? 0;
    public string SourceName { get; set; } = "live";
    /// <summary>Current position: GPS-derived when available, otherwise dead-reckoned (drifts slowly).</summary>
    public double PosX => _posX;
    public double PosY => _posY;
    public bool PosValid => _posInit;

    SessionInfo? _info;
    LapBuffer? _lap;
    RecordedLap? _pending;
    double _pendingSince;
    float _lastLapTimeAtCross;
    bool _prevValid, _wasInCar, _prevPitRoad, _prevInStall, _ignoreStallExit;
    float _prevPct;
    double _prevTime;
    double _posX, _posY;
    bool _posInit;
    // pit stop tracking
    double _stallEnterTime;
    float _stallEnterFuel;
    int _stallEnterTyreSets;

    public void OnSessionInfo(SessionInfo info)
    {
        _info = info;
        if (Current != null) Current.Info = info;
    }

    public void Process(Frame f, DateTime wall)
    {
        var si = _info;
        if (si == null || si.TrackLengthM <= 0) return;

        string key = si.SessionKey(f.SessionNum, wall);
        if (Current == null || Current.Key != key)
        {
            EndSession();
            Current = new SessionContext { Key = key, Info = si, SessionNum = f.SessionNum, StartedAt = wall };
            Stint = 0;
            _wasInCar = false;
            SessionStarted?.Invoke(Current);
        }
        var ctx = Current;

        if (_pending != null) ResolvePending(f, ctx);

        bool inCar = f.IsOnTrack && !f.IsReplayPlaying;
        if (!inCar)
        {
            if (_lap != null) _lap = null;
            _prevValid = false;
            _wasInCar = false;
            _posInit = false;
            return;
        }
        if (!_wasInCar)
        {
            _wasInCar = true;
            _prevValid = false;
            _prevInStall = f.PlayerCarInPitStall;
            _ignoreStallExit = f.PlayerCarInPitStall; // driving out of the box after getting in is not a pit stop
            Stint++;
            StintStarted?.Invoke(ctx);
        }

        TrackPitStops(f, ctx, wall);

        float pct = f.LapDistPct;
        if (_prevValid)
        {
            float dp = pct - _prevPct;
            bool forwardWrap = _prevPct > 0.9f && pct < 0.1f;
            bool backwardWrap = _prevPct < 0.1f && pct > 0.9f;
            if (backwardWrap || (!forwardWrap && Math.Abs(dp) > 0.03f) || f.PlayerCarTowTime > 0)
            {
                _lap = null; // reset, tow or reversing over the line
                _prevValid = false;
            }
            else if (forwardWrap)
            {
                double frac = (1.0 - _prevPct) / ((1.0 - _prevPct) + pct);
                double tCross = _prevTime + frac * (f.SessionTime - _prevTime);
                if (_lap != null) CompleteLap(ctx, f, tCross, wall);
                _lap = new LapBuffer(tCross, f, wall, outLap: f.OnPitRoad, si);
                _lastLapTimeAtCross = f.LapLastLapTime;
            }
        }
        if (_lap == null && _prevValid == false)
        {
            // first frame (or after a reset): start a partial "out lap" that will become valid data at the next crossing
            _lap = new LapBuffer(f.SessionTime - (pct * si.TrackLengthM / Math.Max(f.Speed, 1f)), f, wall, outLap: true, si) { PartialStartPct = pct };
        }

        UpdatePosition(f, si);
        _lap!.Add(f, si, (float)_posX, (float)_posY);

        _prevPct = pct;
        _prevTime = f.SessionTime;
        _prevValid = true;
        _prevPitRoad = f.OnPitRoad;
    }

    /// <summary>Call when the source ends (file finished / sim closed).</summary>
    public void Flush()
    {
        if (_pending != null && Current != null) Finalize(Current, _pending);
        _pending = null;
        EndSession();
    }

    void EndSession()
    {
        if (_pending != null && Current != null) { Finalize(Current, _pending); _pending = null; }
        _lap = null;
        _prevValid = false;
        if (Current != null) SessionEnded?.Invoke(Current);
        Current = null;
    }

    void UpdatePosition(Frame f, SessionInfo si)
    {
        if (f.HasGps)
        {
            double lat0 = si.TrackLat, lon0 = si.TrackLon;
            _posX = (f.Lon - lon0) * 111412.84 * Math.Cos(lat0 * Math.PI / 180.0);
            _posY = (f.Lat - lat0) * 111132.92;
            _posInit = true;
            return;
        }
        // Dead-reckoning from body velocity and compass heading (verified against GPS: ~0.1% drift).
        double dt = _prevValid ? f.SessionTime - _prevTime : 0;
        if (!_posInit) { _posX = 0; _posY = 0; _posInit = true; dt = 0; }
        if (dt <= 0 || dt > 0.5) return;
        double h = float.IsNaN(f.YawNorth) ? f.Yaw : f.YawNorth;
        double s = Math.Sin(h), c = Math.Cos(h);
        _posX += (f.VelX * s - f.VelY * c) * dt;
        _posY += (f.VelX * c + f.VelY * s) * dt;
    }

    void TrackPitStops(Frame f, SessionContext ctx, DateTime wall)
    {
        if (f.PlayerCarInPitStall && !_prevInStall)
        {
            _stallEnterTime = f.SessionTime;
            _stallEnterFuel = f.FuelLevel;
            _stallEnterTyreSets = f.TireSetsUsed;
        }
        else if (!f.PlayerCarInPitStall && _prevInStall && _ignoreStallExit)
        {
            _ignoreStallExit = false;
        }
        else if (!f.PlayerCarInPitStall && _prevInStall)
        {
            var stop = new PitStopInfo
            {
                Lap = f.Lap,
                At = wall,
                StationaryTime = f.SessionTime - _stallEnterTime,
                FuelAdded = float.IsNaN(f.FuelLevel) ? 0 : Math.Max(0, f.FuelLevel - _stallEnterFuel),
                TyresChanged = f.TireSetsUsed > _stallEnterTyreSets && _stallEnterTyreSets >= 0,
            };
            if (stop.StationaryTime > 1)
            {
                ctx.PitStops.Add(stop);
                PitStopCompleted?.Invoke(ctx, stop);
                Stint++;
                StintStarted?.Invoke(ctx);
            }
        }
        _prevInStall = f.PlayerCarInPitStall;
    }

    void CompleteLap(SessionContext ctx, Frame f, double tCross, DateTime wall)
    {
        var buf = _lap!;
        var lap = buf.Build(tCross, f, ctx.Info, Stint);
        lap.Source = SourceName;
        if (buf.PartialStartPct > 0.02f) lap.OutLap = true;
        // The lap counter in the frame after the crossing is the *new* lap; the completed one is one less.
        lap.LapNumber = Math.Max(0, f.Lap - 1);
        if (lap.LapTime < 1) return;
        _pending = lap;
        _pendingSince = f.SessionTime;
    }

    void ResolvePending(Frame f, SessionContext ctx)
    {
        var p = _pending!;
        float official = f.LapLastLapTime;
        bool changed = official > 0 && Math.Abs(official - _lastLapTimeAtCross) > 1e-4;
        if (changed && Math.Abs(official - p.LapTime) < 0.5)
        {
            p.LapTime = official;
            Finalize(ctx, p);
        }
        else if (f.SessionTime - _pendingSince > 3.0 || f.SessionTime < _pendingSince)
            Finalize(ctx, p);
    }

    void Finalize(SessionContext ctx, RecordedLap lap)
    {
        _pending = null;
        ApplyValidity(lap, ctx.Info);
        ctx.Laps.Add(lap);
        LapCompleted?.Invoke(ctx, lap);
    }

    static void ApplyValidity(RecordedLap lap, SessionInfo si)
    {
        string reason = "";
        if (lap.OutLap) reason = "out lap";
        else if (lap.InLap) reason = "in lap";
        else if (lap.OffTracks > 0) reason = "off track";
        else if (lap.Incidents > 0) reason = "incident";
        else if (lap.Data == null || lap.Data.Count < 10) reason = "no data";
        else if (si.EstLapTime > 0 && lap.LapTime < si.EstLapTime * 0.6) reason = "too short";
        else if (lap.Metrics.TryGetValue("coverage", out var cov) && cov < 0.97) reason = "incomplete";
        lap.Valid = reason.Length == 0;
        lap.InvalidReason = reason;
    }

    /// <summary>Accumulates samples for the lap in progress.</summary>
    sealed class LapBuffer
    {
        public readonly double StartTime;
        public readonly DateTime StartWall;
        public bool OutLap;
        public float PartialStartPct;
        public readonly int StartIncidents;
        public int OffTracks;
        readonly float _fuelStart;
        readonly int _setupVersion;
        bool _wasOff, _touchedPit;
        int _compound;
        readonly List<float> t = new(6000), d = new(6000), spd = new(6000), thr = new(6000), brk = new(6000), clu = new(6000), str = new(6000);
        readonly List<float> gear = new(6000), rpm = new(6000), latg = new(6000), lon = new(6000), yawr = new(6000), x = new(6000), y = new(6000), abs = new(6000);
        readonly List<float>[] ws = { new(6000), new(6000), new(6000), new(6000) };
        bool _hasWheelSpeed, _hasGps;
        // tyre aggregates
        readonly double[] _pSum = new double[4], _rhSum = new double[4];
        readonly double[,] _tSum = new double[4, 3];
        readonly float[,] _tMax = new float[4, 3];
        readonly float[] _pMax = new float[4], _rhMin = { float.MaxValue, float.MaxValue, float.MaxValue, float.MaxValue };
        readonly int[] _pN = new int[4], _tN = new int[4], _rhN = new int[4];
        TyreFrame[] _lastTyres = new TyreFrame[4];
        double _airSum, _trackSum; int _wxN;
        float _maxSpeed, _bias = float.NaN;
        int _wetness = -1;

        public LapBuffer(double startTime, Frame f, DateTime wall, bool outLap, SessionInfo si)
        {
            StartTime = startTime;
            StartWall = wall;
            OutLap = outLap;
            StartIncidents = f.PlayerCarMyIncidentCount;
            _fuelStart = f.FuelLevel;
            _setupVersion = si.SetupUpdateCount;
            _compound = f.PlayerTireCompound;
        }

        public void Add(Frame f, SessionInfo si, float px, float py)
        {
            t.Add((float)(f.SessionTime - StartTime));
            d.Add(f.LapDistPct * si.TrackLengthM);
            spd.Add(f.Speed); thr.Add(f.Throttle); brk.Add(f.Brake); clu.Add(f.Clutch); str.Add(f.Steer);
            gear.Add(f.Gear); rpm.Add(f.RPM); latg.Add(f.LatAccel); lon.Add(f.LongAccel); yawr.Add(f.YawRate);
            x.Add(px); y.Add(py); abs.Add(f.AbsActive ? 1 : 0);
            _hasGps |= f.HasGps;
            for (int i = 0; i < 4; i++)
            {
                var ty = f.Tyres[i];
                ws[i].Add(ty.WheelSpeed);
                if (!float.IsNaN(ty.WheelSpeed)) _hasWheelSpeed = true;
                if (!float.IsNaN(ty.Pressure) && ty.Pressure > 0) { _pSum[i] += ty.Pressure; _pN[i]++; _pMax[i] = Math.Max(_pMax[i], ty.Pressure); }
                if (!float.IsNaN(ty.TempM) && ty.TempM > 0)
                {
                    // Left-side tyres: L = outer; right-side tyres: R = outer → normalise to inner/mid/outer.
                    bool left = i % 2 == 0;
                    float tin = left ? ty.TempR : ty.TempL, tout = left ? ty.TempL : ty.TempR;
                    _tSum[i, 0] += tin; _tSum[i, 1] += ty.TempM; _tSum[i, 2] += tout; _tN[i]++;
                    _tMax[i, 0] = Math.Max(_tMax[i, 0], tin); _tMax[i, 1] = Math.Max(_tMax[i, 1], ty.TempM); _tMax[i, 2] = Math.Max(_tMax[i, 2], tout);
                }
                if (!float.IsNaN(ty.RideHeight) && f.Speed > 20) { _rhSum[i] += ty.RideHeight; _rhN[i]++; _rhMin[i] = Math.Min(_rhMin[i], ty.RideHeight); }
                _lastTyres[i] = ty;
            }
            if (!float.IsNaN(f.AirTemp)) { _airSum += f.AirTemp; _trackSum += f.TrackTemp; _wxN++; }
            _maxSpeed = Math.Max(_maxSpeed, f.Speed);
            if (!float.IsNaN(f.BrakeBias)) _bias = f.BrakeBias;
            if (f.TrackWetness >= 0) _wetness = f.TrackWetness;
            if (f.PlayerTireCompound >= 0) _compound = f.PlayerTireCompound;

            bool off = f.PlayerTrackSurface == Irsdk.TrkLoc.OffTrack;
            if (off && !_wasOff) OffTracks++;
            _wasOff = off;
            if (f.OnPitRoad && t.Count > 1 && !OutLap) _touchedPit = true;
        }

        public RecordedLap Build(double endTime, Frame f, SessionInfo si, int stint)
        {
            int n = t.Count;
            var data = new LapData(n);
            data.Set(Ch.T, t.ToArray()); data.Set(Ch.D, d.ToArray()); data.Set(Ch.Speed, spd.ToArray());
            data.Set(Ch.Throttle, thr.ToArray()); data.Set(Ch.Brake, brk.ToArray()); data.Set(Ch.Clutch, clu.ToArray());
            data.Set(Ch.Steer, str.ToArray()); data.Set(Ch.Gear, gear.ToArray()); data.Set(Ch.Rpm, rpm.ToArray());
            data.Set(Ch.LatG, latg.ToArray()); data.Set(Ch.LonG, lon.ToArray()); data.Set(Ch.YawRate, yawr.ToArray());
            data.Set(Ch.X, x.ToArray()); data.Set(Ch.Y, y.ToArray()); data.Set(Ch.Abs, abs.ToArray());
            if (_hasWheelSpeed) for (int i = 0; i < 4; i++) data.Set(Ch.WheelSpeed[i], ws[i].ToArray());

            var lap = new RecordedLap
            {
                StartedAt = StartWall,
                LapTime = endTime - StartTime,
                OutLap = OutLap,
                InLap = _touchedPit || f.OnPitRoad,
                Incidents = f.PlayerCarMyIncidentCount - StartIncidents,
                OffTracks = OffTracks,
                FuelStart = _fuelStart,
                FuelEnd = f.FuelLevel,
                Stint = stint,
                Data = data,
                HasGps = _hasGps,
                MaxSpeed = _maxSpeed,
                BrakeBias = _bias,
                TrackWetness = _wetness,
                SetupVersion = _setupVersion,
                TyreCompound = _compound,
                AirTemp = _wxN > 0 ? (float)(_airSum / _wxN) : float.NaN,
                TrackTemp = _wxN > 0 ? (float)(_trackSum / _wxN) : float.NaN,
            };
            lap.FuelUsed = float.IsNaN(_fuelStart) || float.IsNaN(f.FuelLevel) ? float.NaN : _fuelStart - f.FuelLevel;
            if (lap.FuelUsed < 0) lap.FuelUsed = float.NaN; // refuelled mid-lap

            // coverage: largest gap in distance samples relative to track length
            float maxGap = 0, first = n > 0 ? d[0] : 0, last = n > 0 ? d[n - 1] : 0;
            for (int i = 1; i < n; i++) { float g = d[i] - d[i - 1]; if (g > maxGap) maxGap = g; }
            maxGap = Math.Max(maxGap, Math.Max(first, si.TrackLengthM - last));
            lap.Metrics["coverage"] = 1.0 - maxGap / si.TrackLengthM;

            lap.SectorTimes = SectorTimes(si.SectorStarts, si.TrackLengthM, lap.LapTime);
            lap.Tyres = BuildTyres();
            return lap;
        }

        float[] SectorTimes(float[] starts, float len, double lapTime)
        {
            if (starts.Length < 2 || t.Count < 10) return Array.Empty<float>();
            var times = new float[starts.Length];
            float prev = 0;
            for (int s = 1; s <= starts.Length; s++)
            {
                float boundary = s < starts.Length ? TimeAt(starts[s] * len) : (float)lapTime;
                times[s - 1] = boundary - prev;
                prev = boundary;
            }
            return times;
        }

        float TimeAt(float dist)
        {
            for (int i = 1; i < d.Count; i++)
                if (d[i] >= dist && d[i - 1] <= dist)
                {
                    float span = d[i] - d[i - 1];
                    float a = span > 0 ? (dist - d[i - 1]) / span : 0;
                    return t[i - 1] + a * (t[i] - t[i - 1]);
                }
            return float.NaN;
        }

        TyreLapStats[]? BuildTyres()
        {
            bool any = false;
            var res = new TyreLapStats[4];
            for (int i = 0; i < 4; i++)
            {
                var s = new TyreLapStats();
                var last = _lastTyres[i];
                if (_pN[i] > 0) { s.PressAvg = (float)(_pSum[i] / _pN[i]); s.PressMax = _pMax[i]; s.PressEnd = last.Pressure; any = true; }
                s.ColdPress = last.ColdPressure;
                if (_tN[i] > 0)
                {
                    s.TempInAvg = (float)(_tSum[i, 0] / _tN[i]); s.TempMidAvg = (float)(_tSum[i, 1] / _tN[i]); s.TempOutAvg = (float)(_tSum[i, 2] / _tN[i]);
                    s.TempInMax = _tMax[i, 0]; s.TempMidMax = _tMax[i, 1]; s.TempOutMax = _tMax[i, 2];
                    any = true;
                }
                bool left = i % 2 == 0;
                s.CarcassIn = left ? last.TempCR : last.TempCL; s.CarcassMid = last.TempCM; s.CarcassOut = left ? last.TempCL : last.TempCR;
                s.WearIn = left ? last.WearR : last.WearL; s.WearMid = last.WearM; s.WearOut = left ? last.WearL : last.WearR;
                if (!float.IsNaN(s.CarcassMid) || !float.IsNaN(s.WearMid)) any = true;
                if (_rhN[i] > 0) { s.RideHeightAvg = (float)(_rhSum[i] / _rhN[i]); s.RideHeightMin = _rhMin[i]; }
                res[i] = s;
            }
            return any ? res : null;
        }
    }
}
