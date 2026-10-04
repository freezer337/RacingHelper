using RacingHelper.Analysis;
using RacingHelper.Recording;
using RacingHelper.Sim;

namespace RacingHelper.Live;

/// <summary>
/// Tells you which in-car adjustments would help (brake bias, TC, ABS, in-car anti-roll bars), all in one sentence:
/// "Car adjustments: increase TC by 1, and move brake bias back 0.5." It only advises — you make the change —
/// and it notices when you have. Based on your handling at the limit over the last few clean laps.
/// </summary>
public sealed class InCarAdvisor
{
    const int LapsNeeded = 3;

    readonly RaceEngineer _eng;
    readonly Func<AppSettings> _settings;
    readonly HashSet<string> _carDc = new();
    readonly List<LapData> _laps = new();
    Dictionary<string, float> _values = new();
    List<AdjustAdvice> _advice = new();
    string _lastSaid = "";
    int _repeats, _quietUntilLap, _lapsSinceAdvice;
    TrackModel? _model;

    public IReadOnlyList<AdjustAdvice> Current => _advice;
    /// <summary>Advice given this session that you didn't act on (for the debrief).</summary>
    public IReadOnlyList<AdjustAdvice> Unresolved => _unresolved;
    List<AdjustAdvice> _unresolved = new();

    public InCarAdvisor(RaceEngineer engineer, Func<AppSettings> settings)
    {
        _eng = engineer;
        _settings = settings;
    }

    public void Reset()
    {
        _carDc.Clear(); _laps.Clear(); _values = new(); _advice = new(); _unresolved = new();
        _lastSaid = ""; _repeats = 0; _quietUntilLap = 0; _lapsSinceAdvice = 0;
    }

    public void OnModel(TrackModel? m) => _model = m;

    /// <param name="setupSessionRunning">a guided setup session is already working on the car: stay quiet</param>
    public void OnLap(RecordedLap lap, Frame f, bool setupSessionRunning, TyreManagerLive? tyres)
    {
        foreach (var kv in f.Dc) _carDc.Add(kv.Key);
        DetectChanges(f);
        if (lap.Valid && !lap.OutLap && !lap.InLap && lap.Data != null)
        {
            _laps.Add(lap.Data);
            if (_laps.Count > 5) _laps.RemoveAt(0);
        }
        _lapsSinceAdvice++;
        if (!_settings().InCarAdvice || setupSessionRunning || _carDc.Count == 0) return;
        if (_laps.Count < LapsNeeded || _lapsSinceAdvice < LapsNeeded || lap.LapNumber < _quietUntilLap) return;

        _advice = Work(tyres);
        if (_advice.Count == 0) { _lastSaid = ""; _repeats = 0; return; }
        string words = InCarAdjustments.Join(_advice);
        if (words == _lastSaid)
        {
            // same advice again and nothing changed: say it once more, then leave it for a while
            if (++_repeats >= 2) { _quietUntilLap = lap.LapNumber + 8; return; }
            _eng.Say($"Still worth it: {words}.", "setup", 1);
        }
        else
        {
            _repeats = 0;
            string why = string.Join(" and ", _advice.Select(a => a.Reason).Distinct());
            foreach (var a in _advice) { _unresolved.RemoveAll(u => u.Adj == a.Adj); _unresolved.Add(a); }
            _eng.Say($"Car adjustments: {words}. That's for {why}.", "setup", 1);
        }
        _lastSaid = words;
        _lapsSinceAdvice = 0;
    }

    void DetectChanges(Frame f)
    {
        var now = InCarAdjustments.All.Where(a => f.Dc.ContainsKey(a.Var)).ToDictionary(a => a.Var, a => f.Dc[a.Var]);
        if (_values.Count == 0) { _values = now; return; }
        var changed = InCarAdjustments.All.Where(a => now.ContainsKey(a.Var) && _values.TryGetValue(a.Var, out var o) && Math.Abs(o - now[a.Var]) > 1e-4f).ToList();
        if (changed.Count == 0) return;
        string what = string.Join(", ", changed.Select(a => $"{a.Spoken} {InCarAdjustments.Num(now[a.Var])}"));
        bool asked = changed.Any(c => _advice.Any(x => x.Adj == c) || _unresolved.Any(x => x.Adj == c));
        _unresolved.RemoveAll(u => changed.Contains(u.Adj));
        _eng.Say(asked ? $"Got it: {what}. I'll see how that goes." : $"Noted: {what}.", "setup", 0, minVerbosity: asked ? 0 : 1);
        _values = now;
        _laps.Clear();                 // judge the new settings on their own laps
        _advice = new(); _lastSaid = ""; _repeats = 0; _lapsSinceAdvice = 0;
    }

    List<AdjustAdvice> Work(TyreManagerLive? tyres)
    {
        HandlingReport rep;
        try { rep = HandlingAnalyzer.Analyze(_laps.TakeLast(4), _model); } catch { return new(); }
        var list = new List<AdjustAdvice>();
        bool Has(string id) => _carDc.Contains(InCarAdjustments.Get(id).Var);
        void Add(string id, int dir, float amount, bool byValue, string reason)
        {
            if (!Has(id) || list.Any(x => x.Adj.Id == id) || list.Count >= 3) return;
            list.Add(new AdjustAdvice(InCarAdjustments.Get(id), dir, amount, byValue, reason));
        }
        if (rep.Valid)
        {
            foreach (var c in rep.Cells.Where(c => c.Tendency != "neutral").OrderByDescending(c => Math.Max(c.UndersteerRate, c.OversteerRate)))
            {
                string reason = $"{c.Tendency} {(c.Phase == "mid" ? "mid-corner" : "on " + c.Phase)}";
                switch (c.Tendency, c.Phase)
                {
                    case ("oversteer", "exit"):
                        if (Has("tc")) Add("tc", +1, 1, false, reason); else Add("arbr", -1, 1, false, reason);
                        break;
                    case ("oversteer", "entry"): Add("bb", +1, 0.5f, true, reason); break;
                    case ("understeer", "entry"): Add("bb", -1, 0.5f, true, reason); break;
                    case ("understeer", "mid"):
                        if (Has("arbf")) Add("arbf", -1, 1, false, reason); else Add("arbr", +1, 1, false, reason);
                        break;
                    case ("oversteer", "mid"):
                        if (Has("arbr")) Add("arbr", -1, 1, false, reason); else Add("arbf", +1, 1, false, reason);
                        break;
                }
            }
        }
        // rears overheating from sliding/wheelspin: a touch more TC helps them too
        if (tyres?.State is "hot-rear" or "hot") Add("tc", +1, 1, false, "the rear tyres overheating");
        return list;
    }

    public string Describe()
    {
        if (_carDc.Count == 0) return "This car has no in-car adjustments I can see.";
        if (_advice.Count > 0) return $"Car adjustments: {InCarAdjustments.Join(_advice)}.";
        if (_laps.Count < LapsNeeded) return $"Give me {LapsNeeded - _laps.Count} more clean {(LapsNeeded - _laps.Count == 1 ? "lap" : "laps")} and I'll tell you what to change in the car.";
        return "The car's fine as it is, no in-car changes needed.";
    }
}
