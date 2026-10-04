using System.Globalization;
using System.Text.RegularExpressions;

namespace RacingHelper.Live;

/// <summary>Presses keyboard keys in the sim (implemented by the desktop app).</summary>
public interface IKeySender
{
    /// <summary>Presses (down + up) a key like "F13", "Ctrl+Shift+T", "NumPad7". False if it couldn't (bad key, iRacing not in front).</summary>
    bool Press(string keySpec, out string? error);
}

/// <summary>An in-car adjustment iRacing exposes as a live "dc" variable.</summary>
public sealed record Adjustable(string Id, string Label, string Var, string Param);

/// <summary>
/// Makes in-car adjustments (brake bias, TC, ABS, in-car anti-roll bars) by pressing the keys you've bound to them in
/// iRacing, and reads the live value back after each press, so it stops exactly on target and notices a wrong binding.
/// Only ever runs when you ask for it (Apply button / question). Garage setup items and the wing can't be changed this
/// way: iRacing only accepts those in the garage.
/// </summary>
public sealed class InCarAdjuster
{
    public static readonly Adjustable[] All =
    {
        new("bb", "Brake bias", "dcBrakeBias", "Brake bias"),
        new("tc", "Traction control", "dcTractionControl", "Traction control"),
        new("abs", "ABS", "dcABS", "ABS"),
        new("arbf", "Front anti-roll bar", "dcAntiRollFront", "Front anti-roll bar"),
        new("arbr", "Rear anti-roll bar", "dcAntiRollRear", "Rear anti-roll bar"),
    };

    public static Adjustable? ForParam(string param) => All.FirstOrDefault(a => a.Param == param);

    /// <summary>Direction (+1 more / −1 less) and amount (value for brake bias, steps otherwise) from an optimiser action.</summary>
    public static (int dir, float amount, bool byValue)? Parse(Adjustable a, string action)
    {
        var s = action.ToLowerInvariant();
        int dir = s.Contains("rearward") || s.Contains("soften") || s.Contains("reduce") || s.Contains("decrease") || s.Contains("less") ? -1
                : s.Contains("forward") || s.Contains("stiffen") || s.Contains("increase") || s.Contains("more") || s.Contains("add") ? 1 : 0;
        if (dir == 0) return null;
        if (a.Id == "bb")
        {
            var m = Regex.Match(s, @"(\d+(?:\.\d+)?)");
            float amt = m.Success ? float.Parse(m.Value, CultureInfo.InvariantCulture) : 0.5f;
            return (dir, Math.Clamp(amt, 0.1f, 2f), true);
        }
        var n = Regex.Match(s, @"(\d+)\s*(step|click)");
        return (dir, n.Success ? int.Parse(n.Groups[1].Value) : 1, false);
    }

    public sealed record Request(Adjustable Adj, int Dir, float Amount, bool ByValue, string Why);

    readonly RaceEngineer _eng;
    readonly Func<AppSettings> _settings;
    readonly Func<LiveState> _state;
    readonly SemaphoreSlim _busy = new(1, 1);

    public IKeySender? Keys { get; set; }
    /// <summary>The change the engineer would like made (set by the setup session), applied on request.</summary>
    public Request? Pending { get; set; }

    public InCarAdjuster(RaceEngineer engineer, Func<AppSettings> settings, Func<LiveState> state)
    {
        _eng = engineer;
        _settings = settings;
        _state = state;
    }

    static string KeyId(Adjustable a, int dir) => a.Id + (dir > 0 ? "+" : "-");

    /// <summary>Can the app press the keys for this adjustment (enabled, keys entered, running on Windows)?</summary>
    public bool CanApply(Adjustable a, int dir)
        => Keys != null && _settings().InCarAutomation && !string.IsNullOrWhiteSpace(_settings().InCarKeys.GetValueOrDefault(KeyId(a, dir)));

    public bool CarHas(Adjustable a) => _state().InCarValues.ContainsKey(a.Var);

    /// <summary>Apply the pending change. Returns what happened, in words.</summary>
    public string ApplyPending()
    {
        var p = Pending;
        if (p == null) return "There's no change waiting to be made.";
        var result = Apply(p.Adj, p.Dir, p.Amount, p.ByValue);
        if (result.ok) Pending = null;
        return result.text;
    }

    public (bool ok, string text) Apply(Adjustable a, int dir, float amount, bool byValue)
    {
        if (!_settings().InCarAutomation) return (false, "In-car changes by the app are switched off. Turn them on in Settings, or make the change yourself.");
        if (Keys == null) return (false, "I can't press keys from here.");
        if (!_state().InCarValues.TryGetValue(a.Var, out float start)) return (false, $"This car has no in-car {Lower(a.Label)} adjustment.");
        string key = _settings().InCarKeys.GetValueOrDefault(KeyId(a, dir)) ?? "";
        if (string.IsNullOrWhiteSpace(key)) return (false, $"No key set for {a.Label} {(dir > 0 ? "up" : "down")}. Add it in Settings, the same key you bound in iRacing.");
        if (!_busy.Wait(0)) return (false, "Still making the last change.");
        try
        {
            float cur = start, target = start + dir * amount;
            int changes = 0, stuck = 0;
            for (int press = 0; press < 25; press++)
            {
                if (byValue ? dir * (cur - target) >= -0.01f : changes >= amount) break;
                if (!Keys.Press(key, out var err)) return (false, err ?? "Couldn't press the key.");
                float next = WaitForChange(a.Var, cur, 450);
                if (Math.Abs(next - cur) < 1e-4f)
                {
                    if (++stuck >= 2) return (changes > 0, changes > 0
                        ? $"{a.Label} {Fmt(start)} to {Fmt(cur)}, it won't go further."
                        : $"{a.Label} didn't change. Check that {key} is the key bound to it in iRacing's controls, and that iRacing is the active window.");
                    continue;
                }
                if (Math.Sign(next - cur) != dir)
                    return (false, $"That key moved {Lower(a.Label)} the wrong way ({Fmt(cur)} to {Fmt(next)}). Swap the up and down keys in Settings.");
                stuck = 0; changes++; cur = next;
            }
            return (true, $"Done. {a.Label} {Fmt(start)} to {Fmt(cur)}.");
        }
        finally { _busy.Release(); }
    }

    float WaitForChange(string var, float from, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        float v = from;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            Thread.Sleep(30);
            if (_state().InCarValues.TryGetValue(var, out v) && Math.Abs(v - from) > 1e-4f)
            {
                Thread.Sleep(60);   // let it settle (some cars step in two frames)
                return _state().InCarValues.TryGetValue(var, out var s) ? s : v;
            }
        }
        return v;
    }

    static string Lower(string label) => label == label.ToUpperInvariant() ? label : label.ToLowerInvariant();

    static string Fmt(float v) => v.ToString(Math.Abs(v - MathF.Round(v)) < 1e-3f ? "0" : "0.0#", CultureInfo.InvariantCulture);
}
