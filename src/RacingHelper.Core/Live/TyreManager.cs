using System.Text.Json;
using RacingHelper.Recording;
using RacingHelper.Sim;
using RacingHelper.Storage;

namespace RacingHelper.Live;

/// <summary>
/// Live tyre management. iRacing doesn't publish tyre surface temperatures while you drive, so this watches the
/// cause instead: how much each tyre is sliding. Sliding = the car rotating less than the steering asks for (front)
/// or more (rear), weighted by speed, lateral g and which side is loaded. Checked against real .ibt surface temps,
/// lap-level sliding energy rises with the tyre temperatures and their peaks.
/// The last lap's sliding is compared with your own clean laps at the same places on track (the baseline), so the
/// calls mean "hotter than normal for you here", independent of car and track.
/// </summary>
public sealed class TyreManager
{
    public const int Bins = 100;
    const float HotRatio = 1.20f, CoolRatio = 1.08f;
    const int HotBins = 5, CoolBins = 30;   // tyres take about a lap to shed heat: cool only after a sustained normal stretch
    static readonly string[] Spoken = { "Left front", "Right front", "Left rear", "Right rear" };

    readonly RaceEngineer _eng;
    readonly Func<AppSettings> _settings;
    readonly TyreBaselineStore _store;

    // steering → yaw calibration (v·δ/r), online median
    readonly float[] _kSamples = new float[20000];   // first ~20k gentle-cornering samples: a converging median
    int _kCount, _kSinceSort;
    float _kLapStart = float.NaN;
    float _k = float.NaN;
    bool _kFrozen;      // fixed together with the baseline: sliding is only comparable with the same calibration

    // energy per tyre and distance bin: the latest pass, and the lap being driven
    readonly float[,] _last = new float[4, Bins];
    readonly bool[] _lastSet = new bool[Bins];
    readonly float[,] _lap = new float[4, Bins];
    readonly bool[] _lapSet = new bool[Bins];
    readonly float[] _acc = new float[4];
    float _lapWork;
    int _bin = -1, _setCount;
    float[,]? _done;            // the lap just completed at the line (the tracker reports it a little later)
    int _doneCovered;
    float _doneWork;
    bool _doneKStable;

    // baseline (per tyre per bin) and the laps it is built from
    float[,]? _base;
    float _baseLapWork = float.NaN;
    readonly List<(float[,] bins, float work)> _cands = new();
    bool _baseFromSession;
    string _key = "";
    string _sessionKind = "practice";

    // warm-up
    double _warmWork, _warmNeed = 1;
    bool _cold, _warmAnnounced;
    double _driveTime;

    // state
    readonly float[] _ratio = { float.NaN, float.NaN, float.NaN, float.NaN };
    float _front = float.NaN, _rear = float.NaN;
    bool _hotFront, _hotRear;
    int _hotFrontRun, _hotRearRun, _coolFrontRun, _coolRearRun;
    double _hotSince = double.NaN, _lastReminder = double.NaN;
    double _now;

    public TyreManager(RaceEngineer engineer, Func<AppSettings> settings)
    {
        _eng = engineer;
        _settings = settings;
        _store = new TyreBaselineStore(() => Path.Combine(settings().DataFolder, "tyre-baselines.json"));
    }

    bool Enabled => _settings().TyreManager;

    public TyreManagerLive Snapshot() => new()
    {
        State = _hotFront && _hotRear ? "hot" : _hotFront ? "hot-front" : _hotRear ? "hot-rear" : _cold ? "cold" : _base != null ? "ok" : "learning",
        WarmPct = _cold ? (float)Math.Min(1, _warmWork / Math.Max(1e-6, _warmNeed * BaseWork)) : 1,
        Load = (float[])_ratio.Clone(),
        FrontLoad = _front,
        RearLoad = _rear,
        HasBaseline = _base != null,
    };

    double BaseWork => float.IsFinite(_baseLapWork) && _baseLapWork > 0 ? _baseLapWork : double.NaN;

    // ------------------------------------------------------------------ session events

    public void OnSession(SessionInfo si, string sessionType)
    {
        string key = si.CarPath + "|" + si.TrackKey;
        _sessionKind = Kind(sessionType);
        if (key == _key) return;
        _key = key;
        _base = null; _baseLapWork = float.NaN; _baseFromSession = false; _cands.Clear();
        _kCount = 0; _k = float.NaN; _kFrozen = false; _kLapStart = float.NaN;
        var e = _store.Get(key);
        if (e != null && float.IsFinite(e.K) && Math.Abs(e.K) > 1e-3)
        {
            _base = e.ToArray();
            _baseLapWork = e.LapWork;
            _k = e.K;
            _kFrozen = true;
        }
        ResetLap();
    }

    public static string Kind(string sessionType) =>
        sessionType.Contains("Qual", StringComparison.OrdinalIgnoreCase) ? "quali" :
        sessionType.Contains("Race", StringComparison.OrdinalIgnoreCase) ? "race" : "practice";

    /// <param name="fullWarmUp">false after a short stop on old tyres (they only cooled a little).</param>
    public void OnStintStart(double stationarySeconds = double.NaN, bool newTyres = true)
    {
        ResetLap();
        Array.Clear(_lastSet); Array.Clear(_last); _setCount = 0;
        _hotFront = _hotRear = false; _hotFrontRun = _hotRearRun = _coolFrontRun = _coolRearRun = 0;
        for (int i = 0; i < 4; i++) _ratio[i] = float.NaN;
        _front = _rear = float.NaN;
        _warmWork = 0;
        _warmNeed = newTyres || !double.IsFinite(stationarySeconds) ? 1.0 : Math.Clamp(stationarySeconds / 60.0, 0.15, 1.0);
        _cold = true; _warmAnnounced = false;
        _driveTime = 0;
        if (!Enabled) return;
        string msg = _sessionKind == "quali"
            ? "Out lap. Get heat into the tyres, then push from the line."
            : _warmNeed < 0.5 ? "Tyres have cooled a little in the stop. Easy on the first couple of corners."
            : "Cold tyres. Build the temperature over the first corners, careful on the brakes.";
        _eng.Say(msg, "tyres", 1, "tyres-cold", 20);
    }

    void ResetLap()
    {
        Array.Clear(_lap); Array.Clear(_lapSet); Array.Clear(_acc);
        _lapWork = 0; _bin = -1; _done = null;
    }

    /// <summary>Lap finished: clean laps at normal pace build the baseline (first five clean laps of the session).</summary>
    public void OnLap(RecordedLap lap, double sessionBest)
    {
        var bins = _done;
        int covered = _doneCovered;
        float work = _doneWork;
        _done = null;
        if (bins == null) return;
        // a stored baseline (with its calibration) is replaced by this session's own once 3 clean laps exist
        bool normal = _doneKStable && !lap.OutLap && !lap.InLap && lap.Incidents <= 1 && covered >= Bins * 0.9
                      && (!double.IsFinite(sessionBest) || lap.LapTime <= sessionBest * 1.03);
        if (!normal || _baseFromSession && _cands.Count >= 5 || !float.IsFinite(_k)) return;
        _cands.Add((bins, work));
        if (_cands.Count < 3) return;
        // median of the most recent three clean laps: robust to one scruffy lap
        var use = _cands.TakeLast(3).ToList();
        var b = new float[4, Bins];
        for (int t = 0; t < 4; t++)
            for (int i = 0; i < Bins; i++)
            {
                float a0 = use[0].bins[t, i], a1 = use[1].bins[t, i], a2 = use[2].bins[t, i];
                b[t, i] = Math.Max(Math.Min(a0, a1), Math.Min(Math.Max(a0, a1), a2));
            }
        bool first = _base == null;
        _base = b;
        _baseLapWork = use.Select(x => x.work).OrderBy(x => x).ElementAt(1);
        _baseFromSession = true;
        _kFrozen = true;
        _store.Put(_key, b, _baseLapWork, _k);
        if (first) _eng.Info("Tyre manager: learned your normal tyre load here — overheating and push calls are active.");
    }

    // ------------------------------------------------------------------ per frame

    public void Update(Frame f, float trackLen, double dt, double now)
    {
        _now = now;
        if (dt <= 0 || dt > 0.5 || trackLen <= 0) return;
        if (!f.IsOnTrack || f.OnPitRoad || f.PlayerTrackSurface == Irsdk.TrkLoc.NotInWorld) { _bin = -1; return; }
        float v = f.Speed;
        if (v < 5) return;
        _driveTime += dt;

        float ay = f.LatAccel, r = f.YawRate, st = f.Steer;
        // calibration from gentle cornering (same idea as the handling analyser)
        if (!_kFrozen && v > 15 && Math.Abs(ay) < 4 && Math.Abs(r) > 0.04f && Math.Abs(st) > 0.015f)
        {
            if (_kCount < _kSamples.Length) _kSamples[_kCount++] = v * st / r;
            if (++_kSinceSort >= 300 && _kCount >= 200)
            {
                _kSinceSort = 0;
                var tmp = _kSamples.AsSpan(0, _kCount).ToArray();
                Array.Sort(tmp);
                float k = tmp[tmp.Length / 2];
                if (Math.Abs(k) > 1e-3) _k = k;
            }
        }

        float work = Math.Abs(ay) * v * (float)dt;
        _lapWork += work;
        _warmWork += work;

        if (float.IsFinite(_k))
        {
            float turn = Math.Sign(r);
            float ex = v * st / _k * turn - Math.Abs(r);
            float fs = ex > 0.02f ? ex * v * Math.Abs(ay) : 0;
            float rs = ex < -0.02f ? -ex * v * Math.Abs(ay) : 0;
            for (int t = 0; t < 4; t++)
            {
                float side = t == 1 || t == 3 ? 1 : -1;          // + = right side; a left turn (ay > 0) loads the right
                float w = Math.Clamp(0.5f + 0.5f * side * ay / 15f, 0.05f, 1f);
                _acc[t] += (t < 2 ? fs : rs) * w * (float)dt;
            }
        }

        int b = Math.Clamp((int)(f.LapDistPct * Bins), 0, Bins - 1);
        if (b != _bin)
        {
            if (_bin >= 0 && (b == (_bin + 1) % Bins)) CloseBin(_bin);
            if (_bin >= Bins - 3 && b <= 2) LineCrossed();
            Array.Clear(_acc);
            _bin = b;
        }
        Warm();
    }

    void LineCrossed()
    {
        _done = (float[,])_lap.Clone();
        _doneCovered = _lapSet.Count(x => x);
        _doneWork = _lapWork;
        _doneKStable = float.IsFinite(_k) && float.IsFinite(_kLapStart) && Math.Abs(_k / _kLapStart - 1) < 0.03f;
        _kLapStart = _k;
        Array.Clear(_lap); Array.Clear(_lapSet); _lapWork = 0;
    }

    void CloseBin(int b)
    {
        for (int t = 0; t < 4; t++) { _lap[t, b] = _acc[t]; _last[t, b] = _acc[t]; }
        _lapSet[b] = true;
        if (!_lastSet[b]) { _lastSet[b] = true; _setCount++; }
        Evaluate();
    }

    void Warm()
    {
        if (!_cold) return;
        // without a baseline: about a lap and a half of driving
        bool warm = double.IsFinite(BaseWork) ? _warmWork >= _warmNeed * BaseWork : _driveTime >= 75 * _warmNeed && _setCount >= Bins * 0.6 * _warmNeed;
        if (!warm) return;
        _cold = false;
        if (_warmAnnounced || !Enabled) return;
        _warmAnnounced = true;
        _eng.Say(_sessionKind == "quali" ? "Tyres are in. Push now." : "Tyres are up to temperature. You can push.", "tyres", 1, "tyres-warm", 20);
    }

    void Evaluate()
    {
        var bs = _base;
        if (bs == null || _setCount < Bins / 2) return;
        var num = new double[4]; var den = new double[4];
        for (int i = 0; i < Bins; i++)
        {
            if (!_lastSet[i]) continue;
            for (int t = 0; t < 4; t++) { num[t] += _last[t, i]; den[t] += bs[t, i]; }
        }
        for (int t = 0; t < 4; t++) _ratio[t] = den[t] > 1e-6 ? (float)(num[t] / den[t]) : float.NaN;
        _front = den[0] + den[1] > 1e-6 ? (float)((num[0] + num[1]) / (den[0] + den[1])) : float.NaN;
        _rear = den[2] + den[3] > 1e-6 ? (float)((num[2] + num[3]) / (den[2] + den[3])) : float.NaN;
        if (_cold || !Enabled) return;

        bool wasFront = _hotFront, wasRear = _hotRear;
        Step(_front, ref _hotFront, ref _hotFrontRun, ref _coolFrontRun);
        Step(_rear, ref _hotRear, ref _hotRearRun, ref _coolRearRun);

        if (_hotFront && _hotRear && !(wasFront && wasRear))
            Announce("All four tyres are overheating. You're overdriving. Back off for a lap and let them cool.", 2);
        else if (_hotFront && !wasFront)
            Announce($"{Which(0, "Front tyres")} overheating. Cool them for a lap: brake a little earlier, less steering, let the car rotate.", 2);
        else if (_hotRear && !wasRear)
            Announce($"{Which(2, "Rear tyres")} overheating. Smooth on the throttle out of the slow corners for a lap, don't slide the rear.", 2);
        else if (wasFront && wasRear && (_hotFront != _hotRear))
            _eng.Say(_hotFront ? "Rears are OK again. Fronts still hot, keep cooling them." : "Fronts are OK again. Rears still hot, keep cooling them.", "tyres", 1, "tyres-partial", 30);
        else if (!_hotFront && !_hotRear && (wasFront || wasRear))
        {
            _hotSince = double.NaN;
            _eng.Say(_sessionKind == "quali" ? "Tyres are back. Push." : "Tyres have cooled down. Good to push again.", "tyres", 1, "tyres-ok", 30);
        }
        else if ((_hotFront || _hotRear) && double.IsFinite(_lastReminder) && _now - _lastReminder > 240)
        {
            _lastReminder = _now;
            _eng.Say($"{(_hotFront && _hotRear ? "Tyres" : _hotFront ? "Fronts" : "Rears")} still hot. Keep cooling them.", "tyres", 1, "tyres-still", 120);
        }
    }

    void Announce(string text, int prio)
    {
        if (double.IsNaN(_hotSince)) _hotSince = _now;
        _lastReminder = _now;
        _eng.Say(text, "tyres", prio, "tyres-hot-" + text[..8], 45);
    }

    static void Step(float ratio, ref bool hot, ref int hotRun, ref int coolRun)
    {
        if (!float.IsFinite(ratio)) return;
        hotRun = ratio >= HotRatio ? hotRun + 1 : 0;
        coolRun = ratio <= CoolRatio ? coolRun + 1 : 0;
        if (!hot && hotRun >= HotBins) hot = true;
        else if (hot && coolRun >= CoolBins) hot = false;
    }

    /// <summary>Name the tyre when one side of the axle is doing the work, otherwise the axle.</summary>
    string Which(int first, string axle)
    {
        float a = _ratio[first], b = _ratio[first + 1];
        if (float.IsFinite(a) && float.IsFinite(b))
        {
            if (a >= HotRatio && b < HotRatio - 0.08f) return Spoken[first];
            if (b >= HotRatio && a < HotRatio - 0.08f) return Spoken[first + 1];
        }
        return axle;
    }
}

/// <summary>Per car+track tyre-load baselines, kept between sessions so calls work from the first lap.</summary>
public sealed class TyreBaselineStore
{
    public sealed class Entry
    {
        public float K { get; set; } = float.NaN;
        public float LapWork { get; set; } = float.NaN;
        public float[][] Bins { get; set; } = Array.Empty<float[]>();
        public DateTime Updated { get; set; }

        public float[,]? ToArray()
        {
            if (Bins.Length != 4 || Bins.Any(b => b.Length != TyreManager.Bins)) return null;
            var a = new float[4, TyreManager.Bins];
            for (int t = 0; t < 4; t++) for (int i = 0; i < TyreManager.Bins; i++) a[t, i] = Bins[t][i];
            return a;
        }
    }

    readonly Func<string> _path;
    Dictionary<string, Entry>? _cache;
    readonly object _lock = new();

    public TyreBaselineStore(Func<string> path) { _path = path; }

    Dictionary<string, Entry> Load()
    {
        if (_cache != null) return _cache;
        try
        {
            var p = _path();
            _cache = File.Exists(p) ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(p), Database.Json) ?? new() : new();
        }
        catch { _cache = new(); }
        return _cache;
    }

    public Entry? Get(string key)
    {
        lock (_lock) return Load().TryGetValue(key, out var e) && e.ToArray() != null ? e : null;
    }

    public void Put(string key, float[,] bins, float lapWork, float k)
    {
        lock (_lock)
        {
            var d = Load();
            d[key] = new Entry
            {
                K = k, LapWork = lapWork, Updated = DateTime.Now,
                Bins = Enumerable.Range(0, 4).Select(t => Enumerable.Range(0, TyreManager.Bins).Select(i => bins[t, i]).ToArray()).ToArray(),
            };
            try
            {
                var p = _path();
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllText(p, JsonSerializer.Serialize(d, Database.Json));
            }
            catch { /* best effort */ }
        }
    }
}
