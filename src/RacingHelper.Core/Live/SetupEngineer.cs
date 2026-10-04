using System.Security.Cryptography;
using System.Text;
using RacingHelper.Analysis;
using RacingHelper.Recording;
using RacingHelper.Sim;

namespace RacingHelper.Live;

/// <summary>
/// A guided setup session, like working with an engineer in practice:
///   run (N clean laps) → analyse → "box, change X" → run on the new setup → compare → keep or revert → next change.
/// iRacing doesn't let other apps change the car's setup, so you make the change in the garage (or with the in-car
/// adjustment) and this checks that it happened and whether it worked. In fixed-setup sessions only in-car adjustments
/// (brake bias, traction control, ABS) are suggested.
/// </summary>
public sealed class SetupEngineer
{
    public enum Phase { Off, Baseline, WaitChange, Evaluate, Done }

    sealed class Run
    {
        public string Hash = "";
        public string Label = "";
        public readonly List<LapData> Laps = new();
        public readonly List<double> Times = new();
        public HandlingReport? Handling;
        public double Pace => Times.Count == 0 ? double.NaN : Times.OrderBy(t => t).Take(3).Average();
    }

    readonly RaceEngineer _eng;
    readonly Func<AppSettings> _settings;
    SessionInfo? _si;
    string _kind = "practice";
    bool _fixed;
    TrackModel? _model;

    Run? _base, _test;
    SetupRequest? _req;
    SetupChange? _change;
    string _setupHash = "", _inCar = "";
    readonly HashSet<string> _tried = new();
    int _iteration;
    bool _reverting;      // the last change made it worse: waiting for it to be put back
    bool _autoPending;

    public Phase State { get; private set; } = Phase.Off;
    public InCarAdjuster? Adjuster { get; set; }
    readonly HashSet<string> _carDc = new();   // in-car adjustments this car has
    public int LapsPerRun => Math.Clamp(_settings().SetupRunLaps, 3, 10);
    public string Status { get; private set; } = "";
    public string? Instruction { get; private set; }

    public SetupEngineer(RaceEngineer engineer, Func<AppSettings> settings)
    {
        _eng = engineer;
        _settings = settings;
    }

    public void Reset(SessionInfo si, string sessionType)
    {
        _si = si;
        _kind = TyreManager.Kind(sessionType);
        _fixed = si.IsFixedSetup;
        _setupHash = Hash(si);
        _inCar = "";
        _base = _test = null; _req = null; _change = null; _tried.Clear(); _iteration = 0; _reverting = false;
        State = Phase.Off; Status = ""; Instruction = null;
        _autoPending = _kind == "practice" && _settings().LiveSetupAdvice;
    }

    /// <summary>Car on track: practice sessions start the setup work automatically (if enabled).</summary>
    public void OnStint()
    {
        if (!_autoPending) return;
        _autoPending = false;
        Start(auto: true);
    }

    public void OnModel(TrackModel? m) => _model = m;

    /// <summary>Start a setup session now (asked from the wheel / dashboard).</summary>
    public void Start(bool auto = false)
    {
        if (_si == null) { _eng.Say("Get in the car first, then we'll start the setup work.", "setup", 1, immediate: !auto); return; }
        _base = NewRun("current setup");
        _test = null; _req = null; _change = null; _iteration = 0; _reverting = false;
        State = Phase.Baseline;
        string fixedNote = _fixed ? " It's a fixed setup, so I'll only suggest in-car adjustments." : "";
        Say($"Setup session. Give me {LapsPerRun} clean laps at a steady pace and I'll work out the first change.{fixedNote}", 1, auto);
    }

    public void Stop()
    {
        if (State == Phase.Off) return;
        State = Phase.Off; Instruction = null; Status = "";
        _eng.Say("Setup session stopped.", "setup", 1, immediate: true);
    }

    Run NewRun(string label) => new() { Hash = _setupHash + "|" + _inCar, Label = label };

    void Say(string text, int prio = 1, bool auto = true)
    {
        Status = text;
        _eng.Say(text, "setup", prio, immediate: !auto);
    }

    // ------------------------------------------------------------------ events

    /// <summary>Session info changed: the garage setup may have changed.</summary>
    public void OnSessionInfo(SessionInfo si)
    {
        var old = _si;
        _si = si;
        string h = Hash(si);
        if (h == _setupHash) return;
        _setupHash = h;
        if (old == null || old.CarPath != si.CarPath || old.CarSetup == null) return;   // first look at this car, not a change
        var diff = SetupOptimiser.Diff(old?.CarSetup, si.CarSetup);
        SetupChanged(diff.Select(d => $"{Short(d.key)} {d.a} to {d.b}").ToList(), diff.Select(d => d.key).ToList());
    }

    /// <summary>Per lap: in-car adjustments count as a setup change too.</summary>
    public void OnLap(RecordedLap lap, Frame f)
    {
        foreach (var k in f.Dc.Keys) _carDc.Add(k);
        var adj = InCarAdjuster.All;
        string inCar = string.Join("|", adj.Select(a => f.Dc.TryGetValue(a.Var, out var v) ? Fmt(v) : "-"));
        if (_inCar == "") _inCar = inCar;
        else if (inCar != _inCar)
        {
            var parts = new List<string>(); var keys = new List<string>();
            var o = _inCar.Split('|'); var n = inCar.Split('|');
            for (int i = 0; i < adj.Length; i++) if (o[i] != n[i]) { parts.Add($"{adj[i].Label} {o[i]} to {n[i]}"); keys.Add(adj[i].Param); }
            _inCar = inCar;
            SetupChanged(parts, keys);
        }

        if (State is Phase.Off or Phase.Done or Phase.WaitChange) return;
        var run = State == Phase.Baseline ? _base! : _test!;
        bool clean = lap.Valid && !lap.OutLap && !lap.InLap && lap.Data != null;
        if (!clean)
        {
            if (!lap.OutLap && !lap.InLap) _eng.Say("That lap doesn't count for the setup run.", "setup", 0, minVerbosity: 1);
            return;
        }
        run.Laps.Add(lap.Data!);
        run.Times.Add(lap.LapTime);
        int left = LapsPerRun - run.Laps.Count;
        Status = $"{(State == Phase.Baseline ? "Baseline run" : $"Testing: {_change?.Parameter}")} — {run.Laps.Count}/{LapsPerRun} clean laps";
        if (left > 0)
        {
            if (left == 1) _eng.Say("Last lap of this run.", "setup", 1);
            else if (left == LapsPerRun / 2 || left == 2) _eng.Say($"{left} more laps for the setup run.", "setup", 0);
            return;
        }
        run.Handling = Safe(() => HandlingAnalyzer.Analyze(run.Laps, _model));
        if (State == Phase.Baseline) Recommend(run, first: true);
        else Verdict();
    }

    void SetupChanged(List<string> changes, List<string> keys)
    {
        if (changes.Count == 0) return;
        string what = string.Join(", ", changes.Take(3)) + (changes.Count > 3 ? $" and {changes.Count - 3} more" : "");
        if (State == Phase.WaitChange && _reverting)
        {
            _reverting = false;
            bool back = _base != null && _base.Hash == _setupHash + "|" + _inCar;
            Say(back ? "Back on the previous setup." : $"Setup changed: {what}. I'll take that as the new starting point.");
            if (!back && _base != null) _base = NewRun("current setup");
            if (_base is { Laps.Count: > 0 }) Recommend(_base, first: false);
            else { State = Phase.Baseline; Say($"Give me {LapsPerRun} clean laps on this."); }
        }
        else if (State == Phase.WaitChange && _change != null)
        {
            if (Adjuster != null) Adjuster.Pending = null;
            bool expected = keys.Any(k => SetupOptimiser.Matches(_change.Parameter, k));
            _test = NewRun(_change.Parameter);
            State = Phase.Evaluate;
            Instruction = null;
            Say(expected ? $"Got it: {what}. Now {LapsPerRun} clean laps on that." : $"Setup changed: {what}. That's not what I suggested, but I'll test it. {LapsPerRun} clean laps.");
        }
        else if (State is Phase.Baseline or Phase.Evaluate)
        {
            // changed mid-run: start the run again on the new setup
            var run = NewRun(State == Phase.Baseline ? "current setup" : _change?.Parameter ?? "change");
            if (State == Phase.Baseline) _base = run; else _test = run;
            Say($"Setup changed: {what}. Starting this run again, {LapsPerRun} laps.");
        }
        else _eng.Info($"Setup changed: {what}.");
    }

    void Recommend(Run run, bool first)
    {
        string intro = first ? $"Run done, best {RaceEngineer.Speak(run.Times.Min())}. " : "Next: ";
        var si = _si;
        if (si == null) return;
        var rep = run.Handling;
        SetupRequest? pick = null; SetupChange? change = null;
        if (rep is { Valid: true })
        {
            foreach (var req in SetupOptimiser.FromHandling(rep, si.CarCategory))
            {
                var adv = SetupOptimiser.Advise(req, si.CarSetup);
                change = adv.Changes.FirstOrDefault(c => !c.Why.Contains("not found") && !_tried.Contains(c.Parameter) && (!_fixed || InCar(c.Parameter)));
                if (change != null) { pick = req; break; }
            }
        }
        if (pick == null || change == null)
        {
            State = Phase.Done; Instruction = null;
            Say(_iteration == 0
                ? $"{intro}The balance looks neutral{(_fixed ? " and there's nothing to change in the car" : "")}. The setup's in a good window, the time is in the driving now."
                : "That's as far as the data takes us. The setup's in a good window now.");
            return;
        }
        _req = pick; _change = change; _iteration++;
        _tried.Add(change.Parameter);
        var cell = rep!.Cells.FirstOrDefault(c => c.Tendency == pick.Symptom && c.Phase == pick.Phase);
        string where = cell != null ? $"{pick.Symptom} {(pick.Phase == "mid" ? "mid-corner" : "on " + pick.Phase)} in {cell.SpeedBand} corners" : pick.Symptom;
        string now = change.Current.Count > 0 ? $" It's on {change.Current[0].Split(" = ").Last()} now." : "";
        State = Phase.WaitChange;
        Instruction = $"{change.Parameter}: {change.Action}";
        string offer = "";
        if (Adjuster != null && InCarAdjuster.ForParam(change.Parameter) is { } a && _carDc.Contains(a.Var) && InCarAdjuster.Parse(a, change.Action) is { } how)
        {
            Adjuster.Pending = new InCarAdjuster.Request(a, how.dir, how.amount, how.byValue, where);
            if (Adjuster.CanApply(a, how.dir)) offer = " Press Apply and I'll do it for you.";
        }
        Say(InCar(change.Parameter)
            ? $"{intro}You've got {where}. Change it in the car: {change.Parameter}, {change.Action}.{now}{offer} I'll see it when you do."
            : $"{intro}You've got {where}. Box, and in the garage: {change.Parameter}, {change.Action}.{now} Then {LapsPerRun} laps.", 2);
    }

    void Verdict()
    {
        var a = _base!; var b = _test!;
        double dt = b.Pace - a.Pace;                               // + = slower
        float before = Rate(a.Handling), after = Rate(b.Handling);
        bool better = dt < -0.1 || (after < before * 0.7f && dt < 0.1);
        bool worse = dt > 0.15 && !(after < before * 0.7f);
        string pace = double.IsFinite(dt) ? (dt < 0 ? $"{-dt:0.00} quicker" : $"{dt:0.00} slower") : "no lap times";
        string balance = float.IsFinite(before) && float.IsFinite(after) && before > 0
            ? (after < before * 0.7f ? $", and the {_req?.Symptom} is down by {(1 - after / before) * 100:0} percent" : after > before * 1.3f ? $", and the {_req?.Symptom} got worse" : "")
            : "";
        if (_iteration >= 4 && !worse)
        {
            State = Phase.Done; Instruction = null;
            Say($"{(better ? "That change works" : "No clear difference")}: {pace}{balance}. {(better ? "Keep it." : "Keep whichever feels better.")} That's four changes, the setup's dialled in as far as the data goes.", 2);
            return;
        }
        if (worse)
        {
            // the old setup stays the baseline; carry on once the change is undone
            State = Phase.WaitChange; _reverting = true;
            Instruction = $"put {_change?.Parameter} back";
            Say($"That's worse: {pace}{balance}. Put the {_change?.Parameter} back to how it was.", 2);
            return;
        }
        Say(better ? $"That change works: {pace}{balance}. Keep it." : $"No clear difference: {pace}{balance}. Keep whichever feels better, I'll carry on from this one.", 2);
        _base = b;   // the current setup is the baseline for the next step
        Recommend(b, first: false);
    }

    float Rate(HandlingReport? rep)
    {
        if (rep == null || !rep.Valid || _req == null) return float.NaN;
        var cells = rep.Cells.Where(c => c.Phase == _req.Phase || _req.Phase == "all").ToList();
        if (cells.Count == 0) return float.NaN;
        return cells.Average(c => _req.Symptom == "understeer" ? c.UndersteerRate : c.OversteerRate);
    }

    public string Describe() => State switch
    {
        Phase.Off => "No setup session running. Ask me to start one in practice.",
        Phase.Baseline => $"Setup run: {_base?.Laps.Count ?? 0} of {LapsPerRun} clean laps done.",
        Phase.WaitChange => $"Waiting for the change: {Instruction}.",
        Phase.Evaluate => $"Testing {_change?.Parameter}: {_test?.Laps.Count ?? 0} of {LapsPerRun} clean laps done.",
        _ => "Setup session finished. " + Status,
    };

    // ------------------------------------------------------------------ helpers

    bool InCar(string param) => param is "Brake bias" or "Traction control" or "ABS"
        || (InCarAdjuster.ForParam(param) is { } a && _carDc.Contains(a.Var));   // e.g. in-car anti-roll bars

    static string Hash(SessionInfo si)
    {
        if (si.CarSetup == null) return "";
        var flat = new List<KeyValuePair<string, string>>();
        si.CarSetup.Flatten("", flat);
        var stable = flat.Where(kv => !SetupOptimiser.Volatile(kv.Key)).Select(kv => kv.Key + "=" + kv.Value);
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(string.Join("\n", stable))))[..12];
    }

    static string Short(string key)
    {
        var p = key.Split('.');
        return string.Join(" ", p.TakeLast(2));
    }

    static string Fmt(float v) => float.IsFinite(v) ? v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "-";

    static T? Safe<T>(Func<T> f) where T : class { try { return f(); } catch { return null; } }
}
