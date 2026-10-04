using RacingHelper.Analysis;
using RacingHelper.Recording;
using RacingHelper.Sim;

namespace RacingHelper.Live;

/// <summary>
/// Looks after the car through the session type:
///  • practice — watches your handling over the last laps and has a setup change ready for the pit stop
///  • every stop — reads the crew's tyre temperatures in the pit box and turns them into camber / pressure advice
///  • qualifying — tells you whether there's time for another lap
/// </summary>
public sealed class CarManager
{
    readonly RaceEngineer _eng;
    readonly Func<AppSettings> _settings;
    string _kind = "practice";
    SessionInfo? _si;

    readonly List<LapData> _recent = new();
    int _lapsSinceAdvice;
    readonly HashSet<string> _advised = new();
    string? _pendingSetup;
    string _setupHash = "";

    bool _inStall;
    double _stallSince = double.NaN;
    bool _stallHandled;
    int _lapsSinceStall;
    double _lastLapTime = double.NaN;

    public string? PendingSetupAdvice => _pendingSetup;

    public CarManager(RaceEngineer engineer, Func<AppSettings> settings)
    {
        _eng = engineer;
        _settings = settings;
    }

    public void Reset(SessionInfo si, string sessionType)
    {
        _si = si;
        _kind = TyreManager.Kind(sessionType);
        _recent.Clear();
        _lapsSinceAdvice = 0;
        _advised.Clear();
        _pendingSetup = null;
        _inStall = false; _stallHandled = true; _lapsSinceStall = 0;
        _lastLapTime = double.NaN;
    }

    public void OnSessionInfo(SessionInfo si) => _si = si;

    public void OnLap(RecordedLap lap, TrackModel? model, string setupHash, double sessionTimeRemain, int lapsRemain)
    {
        _lapsSinceStall++;
        if (lap.Valid && !lap.OutLap && !lap.InLap) _lastLapTime = lap.LapTime;

        if (setupHash != _setupHash)
        {
            // new setup → judge it on its own laps
            _setupHash = setupHash;
            _recent.Clear(); _lapsSinceAdvice = 0; _advised.Clear(); _pendingSetup = null;
        }
        if (lap.Valid && !lap.OutLap && !lap.InLap && lap.Data != null)
        {
            _recent.Add(lap.Data);
            if (_recent.Count > 6) _recent.RemoveAt(0);
            _lapsSinceAdvice++;
        }
        if (_kind == "practice" && _settings().LiveSetupAdvice && _recent.Count >= 4 && _lapsSinceAdvice >= 4) SetupCheck(model);
        if (_kind == "quali") QualiTiming(sessionTimeRemain, lapsRemain);
    }

    void SetupCheck(TrackModel? model)
    {
        _lapsSinceAdvice = 0;
        var si = _si;
        if (si == null) return;
        HandlingReport rep;
        try { rep = HandlingAnalyzer.Analyze(_recent, model); } catch { return; }
        if (!rep.Valid) return;
        var reqs = SetupOptimiser.FromHandling(rep, si.CarCategory);
        foreach (var req in reqs)
        {
            string id = req.Symptom + "/" + req.Phase + "/" + req.Speed;
            if (_advised.Contains(id)) continue;
            var adv = SetupOptimiser.Advise(req, si.CarSetup);
            var change = adv.Changes.FirstOrDefault(c => !c.Why.Contains("not found"));
            if (change == null) continue;
            _advised.Add(id);
            var cell = rep.Cells.FirstOrDefault(c => c.Tendency == req.Symptom && c.Phase == req.Phase);
            string where = cell != null ? $"{req.Symptom} {(req.Phase == "mid" ? "mid-corner" : "on " + req.Phase)} in {cell.SpeedBand} corners" : req.Symptom;
            string now = change.Current.Count > 0 ? $" Currently {change.Current[0].Split(" = ").Last()}." : "";
            _pendingSetup = $"Setup idea: you've got {where}. {change.Parameter}: {change.Action}.{now}";
            _eng.Say(_pendingSetup + " I'll remind you in the pits.", "setup", 0, speak: false);
            return;
        }
    }

    void QualiTiming(double remain, int lapsRemain)
    {
        if (lapsRemain is > 0 and <= 1) { _eng.Say("Last lap. Make it count.", "strategy", 2, "quali-last", 60); return; }
        if (!double.IsFinite(remain) || remain <= 0 || remain > 3600 || !double.IsFinite(_lastLapTime)) return;
        if (remain < _lastLapTime * 1.05)
            _eng.Say("This is your last lap. Make it count.", "strategy", 2, "quali-last", 60);
        else if (remain < _lastLapTime * 2.05)
            _eng.Say("Time for one more lap after this one.", "strategy", 1, "quali-one-more", 60);
    }

    /// <summary>Per frame: the pit box is where the crew reads the tyres (iRacing only updates carcass temps there).</summary>
    public void Update(Frame f)
    {
        bool stall = f.PlayerCarInPitStall && f.Speed < 0.5f;
        if (stall && !_inStall) { _stallSince = f.SessionTime; _stallHandled = false; }
        _inStall = stall;
        if (!stall || _stallHandled || f.SessionTime - _stallSince < 2.5) return;
        _stallHandled = true;
        bool driven = _lapsSinceStall >= 1;
        _lapsSinceStall = 0;
        if (driven) TyreReport(f);
        if (_pendingSetup != null)
        {
            _eng.Say(_pendingSetup, "setup", 1);
            _pendingSetup = null;
        }
    }

    void TyreReport(Frame f)
    {
        var si = _si;
        if (si == null) return;
        var t = f.Tyres;
        var inner = new float[4]; var mid = new float[4]; var outer = new float[4]; var avg = new float[4];
        for (int i = 0; i < 4; i++)
        {
            bool left = i == 0 || i == 2;
            inner[i] = left ? t[i].TempCR : t[i].TempCL;
            outer[i] = left ? t[i].TempCL : t[i].TempCR;
            mid[i] = t[i].TempCM;
            avg[i] = (inner[i] + mid[i] + outer[i]) / 3;
        }
        if (avg.Any(a => !float.IsFinite(a) || a <= 0)) return;
        var target = _settings().TyreTargetFor(si.CarPath, si.CarCategory);

        _eng.Say($"Tyre temps in the box (in/mid/out): " + string.Join(", ", Enumerable.Range(0, 4).Select(i => $"{Frame.TyreNames[i]} {inner[i]:0}/{mid[i]:0}/{outer[i]:0}")) + ".", "tyres", 0, speak: false);

        var notes = new List<string>();
        float front = (avg[0] + avg[1]) / 2, rear = (avg[2] + avg[3]) / 2;
        notes.Add($"Tyres: fronts {front:0}, rears {rear:0} degrees.");
        string[] names = { "left front", "right front", "left rear", "right rear" };
        int worst = -1; float worstOff = 0;
        if (target.IdealSpread > 0)
            for (int i = 0; i < 4; i++)
            {
                float off = inner[i] - outer[i] - target.IdealSpread;
                if (Math.Abs(off) > 6 && Math.Abs(off) > Math.Abs(worstOff)) { worst = i; worstOff = off; }
            }
        if (worst >= 0)
        {
            float spread = inner[worst] - outer[worst];
            notes.Add(worstOff > 0
                ? $"The {names[worst]} is {spread:0} degrees hotter inside than outside. Too much negative camber, take out about a quarter of a degree."
                : $"The {names[worst]} is {(spread >= 0 ? $"only {spread:0} degrees hotter inside" : $"{-spread:0} degrees hotter outside")}. Add about a quarter of a degree of negative camber.");
        }
        else
        {
            int shape = -1; float shapeOff = 0;
            for (int i = 0; i < 4; i++)
            {
                float d = mid[i] - (inner[i] + outer[i]) / 2;
                if (Math.Abs(d) > 4 && Math.Abs(d) > Math.Abs(shapeOff)) { shape = i; shapeOff = d; }
            }
            if (shape >= 0)
                notes.Add(shapeOff > 0 ? $"The {names[shape]} is hottest in the middle. Pressure is a bit high." : $"The {names[shape]} is coolest in the middle. Pressure is a bit low.");
        }
        if (front - rear > 8) notes.Add("Fronts are much hotter than rears. The front is sliding, that's understeer.");
        else if (rear - front > 8) notes.Add("Rears are much hotter than fronts. Too much rear sliding or wheelspin.");
        _eng.Say(string.Join(" ", notes), "tyres", 1);
    }
}
