using RacingHelper.Analysis;
using RacingHelper.Recording;
using RacingHelper.Sim;

namespace RacingHelper.Live;

/// <summary>
/// Extra engineer calls iRacing doesn't give you:
///  1. Gap trends — "gaining 0.4 a lap on the car ahead, on him in about 5 laps" / "car behind closing"
///  2. Off-track hot spots — "careful at turn 6, you've been off there twice", before the corner
///  3. Fuel-save coaching — how much to save per lap and exactly where to lift and coast
///  4. Braking consistency — "your braking into turn 1 moved around 20 metres, pick a marker"
///  5. Potential — "your best corners add up to 0.35 quicker than your best lap, mostly turn 4"
/// </summary>
public sealed class Insights
{
    readonly RaceEngineer _eng;
    readonly Func<AppSettings> _settings;
    string _kind = "practice";
    SessionInfo? _si;
    int Verbosity => _eng.Verbosity;

    public Insights(RaceEngineer engineer, Func<AppSettings> settings)
    {
        _eng = engineer;
        _settings = settings;
    }

    public void Reset(SessionInfo si, string sessionType)
    {
        _si = si;
        _kind = TyreManager.Kind(sessionType);
        _gaps.Clear(); _lastGapCall = -10;
        _spots.Clear(); _spotWarned.Clear(); _prevSurface = Irsdk.TrkLoc.OnTrack; _prevInc = -1; _lastSpotAt = (-1e9, -1e9);
        _fuelAdvice = null; _saving = false; _lastFuelCall = -10; _fullRateKgH = float.NaN;
        _brakes.Clear(); _brakeWarned.Clear(); _lastBrakeCall = -10;
        _segs.Clear(); _bestLapSegs = null; _bestLap = double.NaN; _validLaps = 0; _lastPotentialCall = 0;
        _hotspotLap = -1; _prevD = float.NaN;
    }

    // ================================================================== 1. gap trends

    readonly Dictionary<int, List<(int lap, double gap)>> _gaps = new();   // key: +idx ahead, -(idx+1) behind
    int _lastGapCall;
    string _gapText = "";

    public void OnLapGaps(int lap, List<CarRow> standings, int myIdx, double lapsRemaining)
    {
        if (_kind != "race") return;
        var me = standings.FirstOrDefault(r => r.CarIdx == myIdx);
        if (me == null || me.ClassPosition <= 0) return;
        var ahead = standings.FirstOrDefault(r => r.ClassName == me.ClassName && r.ClassPosition == me.ClassPosition - 1);
        var behind = standings.FirstOrDefault(r => r.ClassName == me.ClassName && r.ClassPosition == me.ClassPosition + 1);
        Record(ahead != null ? ahead.CarIdx : int.MinValue, lap, me.Interval);
        Record(behind != null ? -(behind.CarIdx + 1) : int.MinValue, lap, behind?.Interval ?? double.NaN);

        _gapText = GapText(ahead, me.Interval, behind, behind?.Interval ?? double.NaN, lapsRemaining, out bool worthSaying);
        if (worthSaying && lap - _lastGapCall >= 3)
        {
            _lastGapCall = lap;
            _eng.Say(_gapText, "strategy", 1, minVerbosity: 1);
        }
    }

    void Record(int key, int lap, double gap)
    {
        if (key == int.MinValue || !double.IsFinite(gap) || gap <= 0 || gap > 60) return;
        if (!_gaps.TryGetValue(key, out var l)) _gaps[key] = l = new();
        if (l.Count > 0 && l[^1].lap >= lap) return;
        if (l.Count > 0 && l[^1].lap < lap - 2) l.Clear();       // lost touch for a while: start over
        l.Add((lap, gap));
        if (l.Count > 4) l.RemoveAt(0);
    }

    /// <summary>s per lap, + = gap growing. Needs three laps behind the same car.</summary>
    double Trend(int key)
    {
        if (!_gaps.TryGetValue(key, out var l) || l.Count < 3) return double.NaN;
        var a = l[^3]; var b = l[^1];
        return (b.gap - a.gap) / Math.Max(1, b.lap - a.lap);
    }

    string GapText(CarRow? ahead, double gapAhead, CarRow? behind, double gapBehind, double lapsRemaining, out bool worth)
    {
        worth = false;
        var parts = new List<string>();
        if (ahead != null)
        {
            double gap = gapAhead;
            double t = Trend(ahead.CarIdx);
            if (double.IsFinite(gap))
            {
                if (double.IsFinite(t) && t < -0.1)
                {
                    double laps = Math.Max(0, gap - 0.5) / -t;
                    bool inTime = !double.IsFinite(lapsRemaining) || laps <= lapsRemaining;
                    parts.Add($"Car ahead {gap:0.0} seconds, you're gaining {-t:0.0} a lap" + (inTime ? $", on him in about {LapsWord(laps)}." : ", but not enough to catch him before the end."));
                    worth = true;
                }
                else if (double.IsFinite(t) && t > 0.15) { parts.Add($"Car ahead {gap:0.0} seconds and pulling away, {t:0.0} a lap."); worth = true; }
                else parts.Add($"Car ahead {gap:0.0} seconds{(double.IsFinite(t) ? ", gap steady" : "")}.");
            }
        }
        if (behind != null)
        {
            int key = -(behind.CarIdx + 1);
            double gap = gapBehind;
            double t = Trend(key);
            if (double.IsFinite(gap))
            {
                if (double.IsFinite(t) && t < -0.1)
                {
                    double laps = Math.Max(0, gap - 0.5) / -t;
                    bool inTime = !double.IsFinite(lapsRemaining) || laps <= lapsRemaining;
                    parts.Add($"Car behind {gap:0.0} back and {-t:0.0} a lap quicker" + (inTime ? $", with you in about {LapsWord(laps)}." : ", but he runs out of laps."));
                    worth = true;
                }
                else if (double.IsFinite(t) && t > 0.15) { parts.Add($"You're pulling away from the car behind, {t:0.0} a lap, he's {gap:0.0} back."); worth = true; }
                else parts.Add($"Car behind {gap:0.0} back{(double.IsFinite(t) ? ", steady" : "")}.");
            }
        }
        return string.Join(" ", parts);
    }

    public string DescribeGaps(List<CarRow> standings, int myIdx, double lapsRemaining)
    {
        var me = standings.FirstOrDefault(r => r.CarIdx == myIdx);
        if (me == null || me.ClassPosition <= 0) return "No gaps yet.";
        var ahead = standings.FirstOrDefault(r => r.ClassName == me.ClassName && r.ClassPosition == me.ClassPosition - 1);
        var behind = standings.FirstOrDefault(r => r.ClassName == me.ClassName && r.ClassPosition == me.ClassPosition + 1);
        if (_kind != "race")
        {
            string a = ahead != null && double.IsFinite(me.Gap) && double.IsFinite(ahead.Gap) ? $" {me.Gap - ahead.Gap:0.00} off P{ahead.ClassPosition}." : "";
            return $"P{me.ClassPosition}.{a}";
        }
        // live gaps now; the trend comes from the lap-by-lap records
        string t = GapText(ahead, me.Interval, behind, behind?.Interval ?? double.NaN, lapsRemaining, out _);
        return $"P{me.ClassPosition}. " + (t.Length > 0 ? t : "No gap data yet.");
    }

    // ================================================================== 2. off-track hot spots

    readonly Dictionary<int, int> _spots = new();          // corner index → offs this session
    readonly Dictionary<int, int> _spotWarned = new();     // corner index → lap warned
    int _prevSurface = Irsdk.TrkLoc.OnTrack, _prevInc = -1, _hotspotLap = -1;
    (double t, double d) _lastSpotAt = (-1e9, -1e9);
    float _prevD = float.NaN;

    public void Update(Frame f, float d, TrackModel? model, ReferenceLap? reference, DrivingCoach coach)
    {
        if (model == null || !f.IsOnTrack) { _prevSurface = f.PlayerTrackSurface; _prevD = float.NaN; return; }
        // record offs and incidents by corner
        bool off = f.PlayerTrackSurface == Irsdk.TrkLoc.OffTrack && _prevSurface == Irsdk.TrkLoc.OnTrack && f.Speed > 10;
        bool inc = _prevInc >= 0 && f.PlayerCarMyIncidentCount > _prevInc;
        _prevSurface = f.PlayerTrackSurface;
        _prevInc = f.PlayerCarMyIncidentCount;
        if ((off || inc) && !(f.SessionTime - _lastSpotAt.t < 4 && Math.Abs(d - _lastSpotAt.d) < 150))
        {
            _lastSpotAt = (f.SessionTime, d);
            var c = model.CornerAt(d) ?? model.Corners.OrderBy(x => Math.Abs(x.Apex - d)).FirstOrDefault(x => Math.Abs(x.Apex - d) < 200);
            if (c != null) _spots[c.Index] = _spots.GetValueOrDefault(c.Index) + 1;
        }

        // warn before a corner where you keep going off
        float prev = _prevD;
        _prevD = d;
        if (!_settings().HotspotWarnings || float.IsNaN(prev) || d <= prev || d - prev > 50) return;
        if (_hotspotLap == f.Lap) return;
        for (int i = 0; i < model.Corners.Count; i++)
        {
            var c = model.Corners[i];
            int n = _spots.GetValueOrDefault(c.Index);
            if (n < 2) continue;
            float call = DrivingCoach.CallPoint(model, i, reference, f.Speed, out float brake);
            if (float.IsNaN(call) || !(prev < call && d >= call)) continue;
            if (coach.TippedThisLap(c.Index, f.Lap)) return;
            if (_spotWarned.TryGetValue(c.Index, out int l) && l > f.Lap - 3) return;
            _spotWarned[c.Index] = f.Lap;
            _hotspotLap = f.Lap;
            string times = n == 2 ? "twice" : $"{n} times";
            _eng.Say($"Careful at {Spoken(c.Name)}. You've been off there {times}.", "coach", 1, lapDist: d, validUntil: brake);
            return;
        }
    }

    // ================================================================== 3. fuel-save coaching

    float _fullRateKgH = float.NaN;
    string? _fuelAdvice;
    bool _saving;
    int _lastFuelCall;

    /// <summary>Per frame: learn the fuel flow at full throttle (what lifting saves).</summary>
    public void SampleFuel(Frame f)
    {
        if (f.Throttle > 0.95f && f.Speed > 30 && float.IsFinite(f.FuelUsePerHour) && f.FuelUsePerHour > 0)
            _fullRateKgH = float.IsNaN(_fullRateKgH) ? f.FuelUsePerHour : _fullRateKgH + (f.FuelUsePerHour - _fullRateKgH) * 0.01f;
    }

    public void OnLapFuel(int lap, LiveFuel? fuel, ReferenceLap? reference, TrackModel? model)
    {
        if (_kind != "race" || fuel == null || reference == null || model == null) return;
        double spare = fuel.Spare, left = fuel.LapsRemaining;
        if (!double.IsFinite(spare) || !double.IsFinite(left) || left < 1.5) return;
        if (fuel.StopsRemaining > 0) { _fuelAdvice = null; _saving = false; return; }   // a stop is planned anyway

        if (spare >= 0)
        {
            if (_saving)
            {
                _saving = false; _fuelAdvice = null;
                _eng.Say("Fuel saving has done the job, you'll make it to the end. Back to full pace.", "strategy", 2);
            }
            return;
        }
        if (_saving && lap - _lastFuelCall < 3) return;
        _fuelAdvice = FuelPlan(-spare, left, fuel.PerLapPredicted, reference);
        if (_fuelAdvice == null) return;
        _saving = true;
        _lastFuelCall = lap;
        _eng.Say(_fuelAdvice, "strategy", 2);
    }

    string? FuelPlan(double short_, double lapsLeft, double perLap, ReferenceLap reference)
    {
        double perLapSave = short_ / lapsLeft;
        float density = _si?.FuelKgPerL is > 0.3f ? _si.FuelKgPerL : 0.75f;
        if (float.IsNaN(_fullRateKgH) || !double.IsFinite(perLap)) return null;
        double rate = _fullRateKgH / 3600.0 / density;                 // litres per second flat out
        if (perLapSave > perLap * 0.12)
            return $"Fuel: {short_:0.0} litres short to the end. That's too much to save by lifting, you'll need a splash of about {Math.Ceiling(short_ + 0.5):0} litres.";
        var spots = reference.Analysis.Corners.Where(c => float.IsFinite(c.BrakePoint) && float.IsFinite(c.EntrySpeed))
                                              .OrderByDescending(c => c.EntrySpeed).ToList();
        foreach (int n in new[] { 2, 3, 4 })
        {
            if (spots.Count < Math.Min(n, 1)) break;
            var use = spots.Take(n).ToList();
            double metres = use.Average(c => perLapSave / use.Count / (0.9 * rate) * c.EntrySpeed);
            if (metres > 150 && n < 4 && spots.Count > n) continue;
            if (metres > 200) return $"Fuel: {short_:0.0} litres short. Lifting alone won't cover it: lift and coast about 150 metres before every big stop, and short-shift on the straights.";
            double m = Math.Max(20, Math.Round(metres / 10) * 10);
            string where = JoinAnd(use.OrderBy(c => c.BrakePoint).Select(c => Spoken(c.Name)).ToList());
            return $"Fuel: {short_:0.0} litres short to the end. Save {perLapSave:0.00} a lap: lift and coast about {m:0} metres before the braking points into {where}.";
        }
        return null;
    }

    public string DescribeFuel(LiveFuel? f)
    {
        if (f == null || !double.IsFinite(f.LapsInTank)) return "No fuel data yet. Give me a lap.";
        string s = $"{f.FuelLevel:0.0} litres, {f.LapsInTank:0.0} laps at {f.PerLapPredicted:0.00} a lap.";
        if (double.IsFinite(f.Spare) && double.IsFinite(f.LapsRemaining))
            s += f.FuelToAdd > 0.1 ? $" Add {Math.Ceiling(f.FuelToAdd):0} litres at the stop." : f.Spare >= 0 ? $" Enough to the end, {f.Spare:0.0} spare." : $" {-f.Spare:0.0} short to the end.";
        if (_fuelAdvice != null) s += " " + _fuelAdvice;
        return s;
    }

    // ================================================================== 4. braking consistency

    readonly Dictionary<int, List<float>> _brakes = new();
    readonly Dictionary<int, int> _brakeWarned = new();
    int _lastBrakeCall;

    void OnLapBraking(int lap, LapAnalysis la)
    {
        foreach (var c in la.Corners)
        {
            if (!float.IsFinite(c.BrakePoint)) continue;
            if (!_brakes.TryGetValue(c.Corner, out var l)) _brakes[c.Corner] = l = new();
            l.Add(c.BrakePoint);
            if (l.Count > 5) l.RemoveAt(0);
        }
        if (Verbosity < 1 || (_kind == "race" && Verbosity < 2) || lap - _lastBrakeCall < 3) return;
        (int corner, float spread, string name)? worst = null;
        foreach (var (corner, l) in _brakes)
        {
            if (l.Count < 5) continue;
            float mean = l.Average(), sd = MathF.Sqrt(l.Average(x => (x - mean) * (x - mean))), spread = l.Max() - l.Min();
            if (sd < 6 || spread < 18) continue;
            if (_brakeWarned.TryGetValue(corner, out int w) && w > lap - 6) continue;
            if (worst == null || spread > worst.Value.spread)
                worst = (corner, spread, la.Corners.FirstOrDefault(x => x.Corner == corner)?.Name ?? $"turn {corner}");
        }
        if (worst == null) return;
        _brakeWarned[worst.Value.corner] = lap;
        _lastBrakeCall = lap;
        _eng.Say($"{Cap(Spoken(worst.Value.name))}: your braking point moved around {MathF.Round(worst.Value.spread / 5) * 5:0} metres over the last five laps. Pick one marker and brake there every lap.", "coach", 0);
    }

    // ================================================================== 5. potential (best corners combined)

    readonly Dictionary<int, (float best, string name)> _segs = new();
    Dictionary<int, float>? _bestLapSegs;
    double _bestLap = double.NaN;
    int _validLaps, _lastPotentialCall;
    string _potentialText = "";

    void OnLapPotential(RecordedLap lap, LapAnalysis la)
    {
        _validLaps++;
        foreach (var c in la.Corners)
        {
            if (!float.IsFinite(c.SegTime)) continue;
            if (!_segs.TryGetValue(c.Corner, out var s) || c.SegTime < s.best) _segs[c.Corner] = (c.SegTime, c.Name);
        }
        if (!(lap.LapTime >= _bestLap))
        {
            _bestLap = lap.LapTime;
            _bestLapSegs = la.Corners.Where(c => float.IsFinite(c.SegTime)).ToDictionary(c => c.Corner, c => c.SegTime);
        }
        var (total, top) = Potential();
        _potentialText = total >= 0.05
            ? $"Your best corners add up to {total:0.00} quicker than your best lap. Most of it at {string.Join(" and ", top.Select(t => $"{Spoken(t.name)}, {t.gain:0.00}"))}."
            : "Your best lap already has nearly all your best corners in it.";
        if (_kind == "race" || Verbosity < 1 || _validLaps - _lastPotentialCall < 4 || total < 0.15) return;
        _lastPotentialCall = _validLaps;
        _eng.Say(_potentialText, "coach", 0);
    }

    (double total, List<(string name, double gain)> top) Potential()
    {
        if (_bestLapSegs == null) return (0, new());
        var gains = _segs.Where(kv => _bestLapSegs.ContainsKey(kv.Key))
                         .Select(kv => (name: kv.Value.name, gain: (double)Math.Max(0, _bestLapSegs[kv.Key] - kv.Value.best)))
                         .Where(g => g.gain > 0.01).OrderByDescending(g => g.gain).ToList();
        return (gains.Sum(g => g.gain), gains.Take(2).ToList());
    }

    /// <summary>Best clean lap, what your best corners together are worth on top of it, and the two corners with most of it.</summary>
    public (double bestLap, double gain, List<(string name, double gain)> top) PotentialInfo()
    {
        var (total, top) = Potential();
        return (_bestLap, total, top);
    }

    public string DescribePotential() => _bestLapSegs == null ? "I need a couple of clean laps to work out your potential." : _potentialText;

    // ================================================================== lap hook

    public void OnLap(RecordedLap lap, LapAnalysis? la)
    {
        if (la == null || !lap.Valid || lap.OutLap || lap.InLap) return;
        OnLapBraking(lap.LapNumber, la);
        OnLapPotential(lap, la);
    }

    static string LapsWord(double laps) { int n = (int)Math.Max(1, Math.Round(laps)); return n == 1 ? "a lap" : $"{n} laps"; }

    static string JoinAnd(List<string> items) => items.Count <= 1 ? string.Join("", items) : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];

    static string Spoken(string name) => name.StartsWith('T') && name.Length > 1 && char.IsDigit(name[1]) ? "turn " + name[1..] : name;
    static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
