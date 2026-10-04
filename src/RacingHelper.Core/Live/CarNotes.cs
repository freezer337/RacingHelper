using System.Text;
using System.Text.Json;
using RacingHelper.Analysis;
using RacingHelper.Sim;
using RacingHelper.Storage;

namespace RacingHelper.Live;

/// <summary>A change worth trying next time (from a crash pattern or a race debrief).</summary>
public sealed class SheetTodo
{
    public string Symptom { get; set; } = "";     // understeer / oversteer / kerbs …
    public string Phase { get; set; } = "all";
    public string Speed { get; set; } = "all";
    public string Reason { get; set; } = "";      // "2 spins with power oversteer in the race"
    public DateTime At { get; set; } = DateTime.Now;
}

public sealed class CrashRecord
{
    public DateTime At { get; set; } = DateTime.Now;
    public string Session { get; set; } = "";
    public int Lap { get; set; }
    public string Corner { get; set; } = "";
    public string Kind { get; set; } = "";        // power-oversteer / brake-oversteer / lift-oversteer / ran-wide / kerb / contact
    public bool SelfInflicted { get; set; } = true;
    public string Summary { get; set; } = "";
}

public sealed class RaceSummary
{
    public DateTime At { get; set; } = DateTime.Now;
    public int Position { get; set; }
    public int Laps { get; set; }
    public int Incidents { get; set; }
    public double Best { get; set; } = double.NaN;
    public double PaceDrop { get; set; } = double.NaN;     // s/lap, last laps vs first laps
    public List<string> Findings { get; set; } = new();
}

/// <summary>Everything the engineer remembers about a car at a track.</summary>
public sealed class CarTrackNotes
{
    public string Car { get; set; } = "";
    public string Track { get; set; } = "";
    /// <summary>The setup preset: the values that proved better (flattened setup key → value). Updated in place.</summary>
    public Dictionary<string, string> Preset { get; set; } = new();
    public Dictionary<string, string> PresetWhy { get; set; } = new();
    public List<SheetTodo> Todo { get; set; } = new();
    public List<CrashRecord> Crashes { get; set; } = new();
    public RaceSummary? LastRace { get; set; }
    public DateTime Updated { get; set; }
}

/// <summary>
/// One notes file per car + track, overwritten whenever something changes (never a new file per change), plus a
/// readable setup sheet next to it: Documents\RacingHelper\SetupSheets\&lt;car&gt;\&lt;track&gt;.txt.
/// iRacing's .sto setup files can't be written by other apps, so the preset is a list of values to set in the garage;
/// the engineer reads it out and checks your car against it.
/// </summary>
public sealed class CarNotesStore
{
    readonly Func<AppSettings> _settings;
    readonly object _lock = new();
    readonly Dictionary<string, CarTrackNotes> _cache = new();

    public CarNotesStore(Func<AppSettings> settings) { _settings = settings; }

    string Dir => Path.Combine(_settings().DataFolder, "notes");
    static string Safe(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c));
    string FileFor(string car, string track) => Path.Combine(Dir, Safe(car) + "__" + Safe(track) + ".json");
    public string SheetFile(string car, string track) => Path.Combine(_settings().DataFolder, "SetupSheets", Safe(car), Safe(track) + ".txt");

    public CarTrackNotes Get(string car, string track)
    {
        lock (_lock)
        {
            string key = car + "|" + track;
            if (_cache.TryGetValue(key, out var n)) return n;
            try
            {
                var f = FileFor(car, track);
                n = File.Exists(f) ? JsonSerializer.Deserialize<CarTrackNotes>(File.ReadAllText(f), Database.Json) : null;
            }
            catch { n = null; }
            n ??= new CarTrackNotes { Car = car, Track = track };
            _cache[key] = n;
            return n;
        }
    }

    public void Update(string car, string track, Action<CarTrackNotes> change, SessionInfo? si = null)
    {
        lock (_lock)
        {
            var n = Get(car, track);
            change(n);
            if (n.Crashes.Count > 30) n.Crashes.RemoveRange(0, n.Crashes.Count - 30);
            if (n.Todo.Count > 6) n.Todo.RemoveRange(0, n.Todo.Count - 6);
            n.Updated = DateTime.Now;
            try
            {
                Directory.CreateDirectory(Dir);
                var tmp = FileFor(car, track) + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(n, new JsonSerializerOptions(Database.Json) { WriteIndented = true }));
                File.Move(tmp, FileFor(car, track), true);
                var sheet = SheetFile(car, track);
                Directory.CreateDirectory(Path.GetDirectoryName(sheet)!);
                File.WriteAllText(sheet, SheetText(n, si));
            }
            catch { /* best effort */ }
        }
    }

    /// <summary>Preset values that differ from the car's current setup.</summary>
    public static List<(string key, string preset, string now)> PresetDiff(CarTrackNotes n, SessionInfo si)
    {
        var flat = new List<KeyValuePair<string, string>>();
        si.CarSetup?.Flatten("", flat);
        var cur = flat.ToDictionary(kv => kv.Key, kv => kv.Value);
        return n.Preset.Where(p => cur.TryGetValue(p.Key, out var v) && v.Trim() != p.Value.Trim())
                       .Select(p => (p.Key, p.Value, cur[p.Key])).ToList();
    }

    public static string Label(string key)
    {
        var p = key.Split('.');
        return string.Join(" ", p.TakeLast(2));
    }

    static string SheetText(CarTrackNotes n, SessionInfo? si)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Racing Helper setup sheet — {n.Car} @ {n.Track}");
        sb.AppendLine($"Updated {n.Updated:yyyy-MM-dd HH:mm}. This file is rewritten whenever the engineer learns something; it is never duplicated.");
        sb.AppendLine();
        sb.AppendLine("PRESET (set these in the garage, then save your setup):");
        if (n.Preset.Count == 0) sb.AppendLine("  (nothing yet: changes that prove better in a setup session are added here)");
        foreach (var (k, v) in n.Preset) sb.AppendLine($"  {Label(k),-40} {v}{(n.PresetWhy.TryGetValue(k, out var w) ? "   — " + w : "")}");
        sb.AppendLine();
        sb.AppendLine("TO TRY NEXT PRACTICE:");
        if (n.Todo.Count == 0) sb.AppendLine("  (nothing)");
        foreach (var t in n.Todo) sb.AppendLine($"  {t.Symptom} {t.Phase}: {t.Reason}");
        if (n.LastRace != null)
        {
            sb.AppendLine();
            sb.AppendLine($"LAST RACE ({n.LastRace.At:yyyy-MM-dd}): P{n.LastRace.Position}, {n.LastRace.Laps} laps, {n.LastRace.Incidents}x");
            foreach (var f in n.LastRace.Findings) sb.AppendLine("  - " + f);
        }
        if (n.Crashes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("RECENT INCIDENTS:");
            foreach (var c in n.Crashes.TakeLast(10)) sb.AppendLine($"  {c.At:MM-dd} {c.Session} lap {c.Lap} {c.Corner}: {c.Summary}");
        }
        return sb.ToString();
    }
}
