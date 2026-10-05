using RacingHelper.Analysis;
using RacingHelper.Sim;

namespace RacingHelper.Live;

/// <summary>
/// Turns live events into short, prioritised radio messages (text feed + optional voice).
/// Rate-limited and deduplicated so it never talks over itself in a corner.
/// </summary>
public sealed class RaceEngineer
{
    readonly Func<AppSettings> _settings;
    readonly List<EngineerMessage> _log = new();
    readonly Dictionary<string, DateTime> _lastByKey = new();
    readonly object _lock = new();

    public event Action<EngineerMessage>? Said;

    public RaceEngineer(Func<AppSettings> settings) { _settings = settings; }

    public List<EngineerMessage> Recent(int n = 12)
    {
        lock (_lock) return _log.TakeLast(n).ToList();
    }

    /// <summary>practice / quali / race from the current session type (set by the hub).</summary>
    public string SessionKind { get; set; } = "practice";

    public static readonly string[] Modes = { "auto", "practice", "quali", "race", "minimal" };

    /// <summary>The radio profile in use: the one fixed in settings, or the session's own when it's on auto.</summary>
    public string Profile
    {
        get
        {
            string m = _settings().RadioMode;
            return m is "practice" or "quali" or "race" or "minimal" ? m : SessionKind;
        }
    }

    /// <summary>0 minimal, 1 normal (race, and qualifying before its own filter), 2 lots (practice).</summary>
    public int Verbosity => Profile switch { "minimal" => 0, "practice" => 2, _ => 1 };

    public static string ModeName(string mode) => mode switch
    {
        "practice" => "practice radio", "quali" => "qualifying radio", "race" => "race radio", "minimal" => "minimal radio", _ => "automatic radio",
    };

    /// <summary>What the profile means, spoken when it changes.</summary>
    public static string ProfileText(string profile) => profile switch
    {
        "practice" => "Lots of info: lap times, where you gain and lose, setup and tyre calls",
        "quali" => "Half silent. Only tyre warm-up, when to push, and where you're losing time",
        "minimal" => "Only flags, fuel, damage and personal bests",
        _ => "The normal race calls",
    };

    /// <param name="key">dedupe key; the same key is not repeated within <paramref name="cooldownSec"/>.</param>
    /// <returns>false when suppressed by the cooldown.</returns>
    public bool Say(string text, string category = "info", int priority = 1, string? key = null, double cooldownSec = 0, bool speak = true, int minVerbosity = 0,
                    float lapDist = float.NaN, float validUntil = float.NaN, bool immediate = false)
    {
        if (Verbosity < minVerbosity) speak = false;
        lock (_lock)
        {
            if (key != null)
            {
                if (_lastByKey.TryGetValue(key, out var last) && (DateTime.Now - last).TotalSeconds < cooldownSec) return false;
                _lastByKey[key] = DateTime.Now;
            }
            var m = new EngineerMessage { Text = text, Category = category, Priority = priority, Speak = speak, LapDist = lapDist, ValidUntil = validUntil, Immediate = immediate, Key = key ?? "" };
            _log.Add(m);
            if (_log.Count > 200) _log.RemoveRange(0, 50);
            Said?.Invoke(m);
            return true;
        }
    }

    // ---------------- events ----------------

    public void SessionStart(SessionInfo si, string sessionType, double personalBest, string referenceLabel, string? setupChange, string conditions)
    {
        string pb = personalBest > 0 ? $" Your best here is {Speak(personalBest)}." : " First time here with this car — I'll build the track map on your first clean lap.";
        Say($"{si.CarName} at {si.TrackDisplayName}, {sessionType.ToLowerInvariant()}.{pb}", "info", 1, "session-start", 30);
        if (!string.IsNullOrEmpty(referenceLabel)) Say($"Delta reference: {referenceLabel}.", "info", 0, speak: false);
        if (!string.IsNullOrEmpty(conditions)) Say(conditions, "info", 0, speak: Verbosity >= 2);
        if (setupChange != null) Say(setupChange, "setup", 1, speak: Verbosity >= 1);
    }

    public void LapDone(int lap, double lapTime, bool valid, string invalidReason, double deltaToRef, string refLabel, bool sessionBest, bool personalBest,
                        LapComparison? cmp, int position, bool isRace)
    {
        if (!valid)
        {
            if (invalidReason is "off track" or "incident")
                Say($"Lap {lap} invalid, {invalidReason}.", "lap", 1, speak: Verbosity >= 1);
            return;
        }
        if (personalBest) { Say($"New personal best! {Speak(lapTime)}.", "pb", 2); }
        else if (sessionBest) { Say($"Session best, {Speak(lapTime)}.", "pb", 1); }
        else
        {
            string delta = double.IsFinite(deltaToRef) ? $", {Signed(deltaToRef)} to {refLabel}" : "";
            Say($"{Speak(lapTime)}{delta}.", "lap", 1, speak: Verbosity >= 1);
        }

        if (cmp != null)
        {
            var top = cmp.TopLosses;
            if (top.Count > 0 && Verbosity >= 1)
            {
                var c = top[0];
                string tip = string.IsNullOrEmpty(c.Verdict) ? "" : $", {c.Verdict}";
                Say($"Most time lost at {Corner(c.Name)}: {c.TimeDelta:0.0#}{tip}.", "corner", 1);
            }
            var gained = cmp.Corners.Where(x => x.TimeDelta < -0.08f).OrderBy(x => x.TimeDelta).FirstOrDefault();
            if (gained != null && Verbosity >= 2)
                Say($"Good {Corner(gained.Name)}, gained {-gained.TimeDelta:0.0#}.", "corner", 0);
        }
    }

    public void CornerDone(CornerLive c)
    {
        if (!_settings().CornerCallouts) return;
        if (Math.Abs(c.TimeDelta) < 0.08f) return;
        string text = c.TimeDelta > 0
            ? $"{Corner(c.Name)} plus {c.TimeDelta:0.0#}{(string.IsNullOrEmpty(c.Verdict) ? "" : ", " + c.Verdict)}"
            : $"{Corner(c.Name)} minus {-c.TimeDelta:0.0#}";
        Say(text, "corner", 0, "corner-" + c.Name, 5);
    }

    public void Fuel(LiveFuel f, bool isRace, int lapsCompleted)
    {
        if (!double.IsFinite(f.LapsInTank)) return;
        if (f.LapsInTank < 1.2 && double.IsFinite(f.LapsRemaining) && f.LapsRemaining > f.LapsInTank)
            Say("Box this lap, fuel is critical.", "fuel", 3, "fuel-critical", 60);
        else if (f.LapsInTank < 2.5 && double.IsFinite(f.LapsRemaining) && f.LapsRemaining > f.LapsInTank)
            Say($"Fuel for {f.LapsInTank:0.#} laps. Plan to box.", "fuel", 2, "fuel-low", 90);
        else if (isRace && lapsCompleted > 0 && lapsCompleted % 5 == 0 && double.IsFinite(f.LapsRemaining))
        {
            string msg = f.FuelToAdd > 0.1
                ? $"Fuel: {f.LapsInTank:0.#} laps in the tank, add {Math.Ceiling(f.FuelToAdd):0} litres at the stop."
                : $"Fuel is good to the end, {f.Spare:0.#} litres spare.";
            Say(msg, "fuel", 1, "fuel-status-" + lapsCompleted, 30, minVerbosity: 1);
        }
    }

    public void Incident(int added, int total, int limit)
    {
        if (added <= 0) return;
        string lim = limit > 0 ? $" of {limit}" : "";
        int prio = limit > 0 && total >= limit * 0.75 ? 3 : 1;
        Say($"{added}x incident. {total}{lim} total.", "warning", prio, "incident", 4, minVerbosity: 0);
    }

    public void Flags(uint flags, uint prev, int position)
    {
        bool On(Irsdk.Flags f) => (flags & (uint)f) != 0 && (prev & (uint)f) == 0;
        if (On(Irsdk.Flags.Black)) Say("Black flag! Serve your penalty.", "flag", 3, "black", 20);
        if (On(Irsdk.Flags.Repair)) Say("Meatball flag — damage needs repair. Box.", "flag", 3, "repair", 30);
        if (On(Irsdk.Flags.Disqualify)) Say("Disqualified.", "flag", 3, "dq", 60);
        if (On(Irsdk.Flags.Checkered)) Say(position > 0 ? $"Checkered flag. P{position}. Bring it home." : "Checkered flag.", "flag", 2, "checkered", 60);
        else if (On(Irsdk.Flags.White)) Say("White flag, last lap.", "flag", 2, "white", 60);
        if (On(Irsdk.Flags.Blue)) Say("Blue flag, faster car behind.", "flag", 1, "blue", 25, minVerbosity: 1);
        if (On(Irsdk.Flags.Yellow) || On(Irsdk.Flags.YellowWaving)) Say("Yellow flag ahead.", "flag", 2, "yellow", 12);
        if (On(Irsdk.Flags.Caution) || On(Irsdk.Flags.CautionWaving)) Say("Full course caution.", "flag", 2, "caution", 30);
        if (On(Irsdk.Flags.Green) && (prev & (uint)(Irsdk.Flags.Caution | Irsdk.Flags.CautionWaving)) != 0) Say("Green, green, green!", "flag", 2, "green", 20);
    }

    public void Damage(float repairSec, float optRepairSec)
    {
        if (repairSec > 1) Say($"Damage. Mandatory repairs {repairSec:0} seconds.", "warning", 3, "damage-req", 120);
        else if (optRepairSec > 5) Say($"Some damage. Optional repairs {optRepairSec:0} seconds.", "warning", 2, "damage-opt", 120);
    }

    public void PossibleDamage(float paceLossPct) =>
        Say($"Pace is down {paceLossPct:0.0} percent since the contact. Possible damage.", "warning", 2, "pace-damage", 300);

    public void EngineWarning(string w) => Say($"Warning: {w}.", "warning", 3, "eng-" + w, 60);

    public void PitStop(PitStopSummary s) =>
        Say($"Stop {s.Stationary:0.0} seconds{(s.FuelAdded > 0.5 ? $", {s.FuelAdded:0} litres" : "")}{(s.Tyres ? ", new tyres" : "")}.", "info", 1, speak: Verbosity >= 1);

    public void Weather(string text) => Say(text, "info", 1, "weather-" + text, 600, minVerbosity: 1);

    public void Info(string text, bool speak = false) => Say(text, "info", 0, speak: speak);

    // ---------------- speech formatting ----------------

    public static string Speak(double lapTime)
    {
        if (!double.IsFinite(lapTime) || lapTime <= 0) return "no time";
        int m = (int)(lapTime / 60);
        double s = lapTime - m * 60;
        return m > 0 ? $"{m} {s:00.00}" : $"{s:0.00}";
    }

    static string Signed(double d) => d >= 0 ? $"plus {d:0.00}" : $"minus {-d:0.00}";
    static string Corner(string name) => name.StartsWith('T') ? "turn " + name[1..] : name;
}

public sealed record PitStopSummary(double Stationary, float FuelAdded, bool Tyres);
