using System.Text.Json;
using RacingHelper.Analysis;
using RacingHelper.Storage;

namespace RacingHelper;

public sealed class OverlayConfig
{
    public string Id { get; set; } = "";
    public bool Enabled { get; set; }
    public double X { get; set; } = double.NaN;
    public double Y { get; set; } = double.NaN;
    public double Scale { get; set; } = 1;
    public double Opacity { get; set; } = 0.92;
}

/// <summary>A keyboard shortcut for an action (a question, or an app action like "toggle overlays").</summary>
public sealed class KeyBinding
{
    public string Action { get; set; } = "";
    public string Keys { get; set; } = "";          // e.g. "Ctrl+Shift+F5", "F13", "Alt+NumPad7"
}

/// <summary>A wheel / button-box button that asks the engineer something (see TelemetryHub.Questions).</summary>
public sealed class ButtonBinding
{
    public string Action { get; set; } = "";
    public string Device { get; set; } = "";        // stable id, e.g. VID_346E&PID_0006
    public string DeviceName { get; set; } = "";
    public int Button { get; set; }
}

public sealed class AppSettings
{
    static string Docs => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    public string DataFolder { get; set; } = Path.Combine(Docs, "RacingHelper");
    public string TelemetryFolder { get; set; } = Path.Combine(Docs, "iRacing", "telemetry");
    public string IRacingSetupsFolder { get; set; } = Path.Combine(Docs, "iRacing", "setups");
    public string SetupLibraryFolder { get; set; } = Path.Combine(Docs, "RacingHelper", "SetupLibrary");

    public int WebPort { get; set; } = 5199;
    public bool AllowLan { get; set; }

    public string SpeedUnit { get; set; } = "kmh";      // kmh | mph
    public string PressureUnit { get; set; } = "kPa";   // kPa | psi | bar
    public string TempUnit { get; set; } = "C";         // C | F

    // comparison target
    public string ReferenceMode { get; set; } = "pb";   // pb | session | lap
    public long ReferenceLapId { get; set; }
    public string DeltaSectors { get; set; } = "iracing"; // "iracing" or a number of equal mini-sectors

    // race engineer
    public bool VoiceEnabled { get; set; } = true;
    // radio mode: auto picks by session — practice: lots of info, qualifying: half silent (tyre warm-up / push + where
    // you lose time), race: the normal radio. Or fix it to one of them (or minimal: flags, fuel, damage, PBs).
    public string RadioMode { get; set; } = "auto";         // auto | practice | quali | race | minimal
    public int VoiceRate { get; set; } = 1;                 // -10..10
    public int VoiceVolume { get; set; } = 100;
    public string VoiceName { get; set; } = "";
    public bool CornerCallouts { get; set; }                // speak after each corner where time was lost
    public bool QuietInCorners { get; set; } = true;        // hold messages until a straight with room to finish them
    public string VoiceOutput { get; set; } = "auto";       // auto (CrewChief when connected, else Windows) | windows | crewchief

    // CrewChief V4: we host a local MQTT broker; CrewChief connects to it and speaks our messages
    public bool CrewChiefEnabled { get; set; } = true;
    public int CrewChiefPort { get; set; } = 1883;
    public bool CrewChiefSkipDuplicates { get; set; } = true; // don't send what CrewChief already says itself (flags, fuel, lap times…)

    // car management while driving
    public bool TyreManager { get; set; } = true;           // cold / overheating / push calls from live sliding energy
    public string CoachingMode { get; set; } = "practice";  // practice | always | off — corner tips before the corner
    public bool HotspotWarnings { get; set; } = true;       // "careful at turn 6, you've been off there twice"
    public bool LiveSetupAdvice { get; set; } = true;       // practice: start a guided setup session automatically
    public int SetupRunLaps { get; set; } = 5;              // clean laps per setup run
    public List<ButtonBinding> ButtonBindings { get; set; } = new();
    public List<KeyBinding>? KeyBindings { get; set; }       // null = the defaults below

    public static readonly KeyBinding[] DefaultKeys =
    {
        new() { Action = "overlays-edit", Keys = "Ctrl+Shift+F9" }, new() { Action = "overlays-toggle", Keys = "Ctrl+Shift+F10" },
        new() { Action = "reference", Keys = "Ctrl+Shift+F11" }, new() { Action = "dashboard", Keys = "Ctrl+Shift+F12" },
        new() { Action = "tyres", Keys = "Ctrl+Shift+F5" }, new() { Action = "fuel", Keys = "Ctrl+Shift+F6" },
        new() { Action = "gaps", Keys = "Ctrl+Shift+F7" }, new() { Action = "repeat", Keys = "Ctrl+Shift+F8" },
        new() { Action = "radio-mode", Keys = "Ctrl+Shift+F4" }, new() { Action = "radio", Keys = "Ctrl+Shift+F3" },
    };

    public List<KeyBinding> EffectiveKeys() => KeyBindings ?? DefaultKeys.Select(k => new KeyBinding { Action = k.Action, Keys = k.Keys }).ToList();
    public bool InCarAdvice { get; set; } = true;            // "car adjustments: increase TC by 1, and move brake bias back 0.5"

    // automatic pit service: set the pit menu as you enter pit road
    public string AutoPit { get; set; } = "race";           // race | always | off
    public bool AutoPitFuel { get; set; } = true;            // fuel to the finish (+ safety margin)
    public string AutoPitTyres { get; set; } = "auto";       // auto (when enough laps left) | always | never | manual (don't touch)
    public int AutoPitTyreMinLaps { get; set; } = 6;
    public bool AutoPitPressures { get; set; } = true;       // cold pressures your last run here says you need
    public bool AutoPitFastRepair { get; set; } = true;
    public bool AutoPitWindscreen { get; set; } = true;

    // automation
    public bool AutoImportIbt { get; set; } = true;
    public bool AutoStartDiskTelemetry { get; set; } = true;
    public bool AutoInstallSetups { get; set; } = true;
    public string SetupTypeFilter { get; set; } = "all";    // all | race | quali | wet
    public bool SetupLatestOnly { get; set; } = true;
    public bool SetupMatchTrack { get; set; }

    public int MyUserId { get; set; }
    public double FuelMarginLaps { get; set; } = 1;
    public Dictionary<string, TyreTarget> TyreTargets { get; set; } = new();  // by car path

    // overlays
    public int OverlayFps { get; set; } = 30;
    public bool OverlaysOnlyInCar { get; set; } = true;
    public List<OverlayConfig> Overlays { get; set; } = new();

    public TyreTarget TyreTargetFor(string carPath, string category)
        => TyreTargets.TryGetValue(carPath, out var t) ? t : TyreAnalyzer.DefaultTarget(category);
}

public sealed class SettingsStore
{
    readonly string _path;
    readonly object _lock = new();
    public AppSettings Current { get; private set; }
    public event Action<AppSettings>? Changed;

    public SettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RacingHelper", "settings.json");
        Current = Load();
    }

    public string FilePath => _path;

    AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Database.Json) ?? new AppSettings();
        }
        catch { /* corrupt file → defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, new JsonSerializerOptions(Database.Json) { WriteIndented = true }));
            File.Move(tmp, _path, true);
        }
        Changed?.Invoke(Current);
    }

    public void Update(Action<AppSettings> change)
    {
        lock (_lock) change(Current);
        Save();
    }

    public void Replace(AppSettings s)
    {
        lock (_lock) Current = s;
        Save();
    }
}
