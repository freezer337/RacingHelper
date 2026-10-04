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
    public string VoiceVerbosity { get; set; } = "normal";  // minimal | normal | detailed
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
    // in-car adjustments by key press (opt-in): keys you bound in iRacing, by id: bb+ bb- tc+ tc- abs+ abs- arbf+ arbf- arbr+ arbr-
    public bool InCarAutomation { get; set; }
    public Dictionary<string, string> InCarKeys { get; set; } = new();

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
