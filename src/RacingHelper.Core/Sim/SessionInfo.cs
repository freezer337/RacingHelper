using System.Globalization;

namespace RacingHelper.Sim;

public sealed class DriverEntry
{
    public int CarIdx;
    public string Name = "", AbbrevName = "", Initials = "", CarNumber = "", LicString = "", LicColor = "#888888";
    public string CarPath = "", CarScreenName = "", CarClassShort = "", ClassColor = "#ffffff", TeamName = "";
    public int UserId, IRating, CarClassId, LicLevel, CarClassRelSpeed;
    public bool IsPaceCar, IsSpectator, IsAI;
    public float ClassEstLapTime;
}

public sealed class ResultPosition
{
    public int Position, ClassPosition, CarIdx, Lap, LapsComplete, Incidents, FastestLap;
    public double Time, FastestTime, LastTime;
    public string ReasonOut = "";
}

public sealed class SessionDef
{
    public int Num;
    public string Type = "", Name = "";
    public int? Laps;              // null = unlimited
    public double? TimeSec;        // null = unlimited
    public List<ResultPosition> Results = new();

    public bool IsRace => Type.Contains("Race", StringComparison.OrdinalIgnoreCase);
    public bool IsQualify => Type.Contains("Qual", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Strongly-typed view over the iRacing session-info YAML.</summary>
public sealed class SessionInfo
{
    public YNode Root { get; }
    public string RawYaml { get; }

    // track
    public string TrackName = "", TrackDisplayName = "", TrackShortName = "", TrackConfig = "", TrackType = "", TrackCountry = "";
    public int TrackId, TrackNumTurns;
    public float TrackLengthM;
    public double TrackLat, TrackLon;
    public float PitSpeedLimitKph;

    // event
    public long SubSessionId, SessionId;
    public int SeriesId, SeasonId, LeagueId;
    public bool Official, TeamRacing;
    public string EventType = "", Category = "";
    public int IncidentLimit;

    // player & car
    public int PlayerCarIdx, PlayerUserId;
    public List<DriverEntry> Drivers = new();
    public DriverEntry? Player;
    public string CarPath = "", CarName = "", CarClass = "";
    public int CarId;
    public float FuelMaxL, MaxFuelPct = 1, FuelKgPerL = 0.75f;
    public float ShiftRpm, RedLine, IdleRpm, ShiftLightFirstRpm, ShiftLightBlinkRpm;
    public int Gears;
    public float EstLapTime;
    public string SetupName = "";
    public bool SetupModified;
    public bool IsFixedSetup;
    public List<string> TireCompounds = new();

    // sessions
    public int CurrentSessionNum;
    public List<SessionDef> Sessions = new();

    // sectors / setup
    public float[] SectorStarts = { 0f };
    public YNode? CarSetup;
    public int SetupUpdateCount;

    public SessionInfo(string yaml)
    {
        RawYaml = yaml;
        Root = YNode.Parse(yaml);
        var w = Root["WeekendInfo"] ?? YNode.Empty;
        TrackName = w.Str("TrackName");
        TrackDisplayName = w.Str("TrackDisplayName", TrackName);
        TrackShortName = w.Str("TrackDisplayShortName", TrackDisplayName);
        TrackConfig = w.Str("TrackConfigName");
        TrackType = w.Str("TrackType");
        TrackCountry = w.Str("TrackCountry");
        TrackId = w.Int("TrackID");
        TrackNumTurns = w.Int("TrackNumTurns");
        TrackLengthM = (float)(w.Num("TrackLength", 0) * 1000.0);
        TrackLat = w.Num("TrackLatitude", 0);
        TrackLon = w.Num("TrackLongitude", 0);
        PitSpeedLimitKph = (float)w.Num("TrackPitSpeedLimit", 0);
        SubSessionId = (long)w.Num("SubSessionID", 0);
        SessionId = (long)w.Num("SessionID", 0);
        SeriesId = w.Int("SeriesID");
        SeasonId = w.Int("SeasonID");
        LeagueId = w.Int("LeagueID");
        Official = w.Int("Official") == 1;
        TeamRacing = w.Int("TeamRacing") == 1;
        EventType = w.Str("EventType");
        Category = w.Str("Category");
        IncidentLimit = w.Int("WeekendOptions.IncidentLimit");
        IsFixedSetup = w.Int("WeekendOptions.IsFixedSetup") == 1;

        var d = Root["DriverInfo"] ?? YNode.Empty;
        PlayerCarIdx = d.Int("DriverCarIdx");
        PlayerUserId = d.Int("DriverUserID");
        FuelMaxL = (float)d.Num("DriverCarFuelMaxLtr", 0);
        MaxFuelPct = (float)d.Num("DriverCarMaxFuelPct", 1);
        FuelKgPerL = (float)d.Num("DriverCarFuelKgPerLtr", 0.75);
        ShiftRpm = (float)d.Num("DriverCarSLShiftRPM", 0);
        ShiftLightFirstRpm = (float)d.Num("DriverCarSLFirstRPM", 0);
        ShiftLightBlinkRpm = (float)d.Num("DriverCarSLBlinkRPM", 0);
        RedLine = (float)d.Num("DriverCarRedLine", 0);
        IdleRpm = (float)d.Num("DriverCarIdleRPM", 0);
        Gears = d.Int("DriverCarGearNumForward");
        EstLapTime = (float)d.Num("DriverCarEstLapTime", 0);
        SetupName = d.Str("DriverSetupName");
        SetupModified = d.Int("DriverSetupIsModified") == 1;
        foreach (var t in (d["DriverTires"]?.Items ?? Array.Empty<YNode>()))
            TireCompounds.Add(t.Str("TireCompoundType"));

        foreach (var n in (d["Drivers"]?.Items ?? Array.Empty<YNode>()))
        {
            var e = new DriverEntry
            {
                CarIdx = n.Int("CarIdx"),
                Name = n.Str("UserName"),
                AbbrevName = n.Str("AbbrevName"),
                Initials = n.Str("Initials"),
                UserId = n.Int("UserID"),
                TeamName = n.Str("TeamName"),
                CarNumber = n.Str("CarNumber"),
                CarPath = n.Str("CarPath"),
                CarScreenName = n.Str("CarScreenName"),
                CarClassShort = n.Str("CarClassShortName"),
                CarClassId = n.Int("CarClassID"),
                CarClassRelSpeed = n.Int("CarClassRelSpeed"),
                ClassColor = HexColor(n.Str("CarClassColor"), "#ffffff"),
                IRating = n.Int("IRating"),
                LicLevel = n.Int("LicLevel"),
                LicString = n.Str("LicString"),
                LicColor = HexColor(n.Str("LicColor"), "#888888"),
                IsPaceCar = n.Int("CarIsPaceCar") == 1,
                IsSpectator = n.Int("IsSpectator") == 1,
                IsAI = n.Int("CarIsAI") == 1,
                ClassEstLapTime = (float)n.Num("CarClassEstLapTime", 0),
            };
            if (string.IsNullOrEmpty(e.CarClassShort)) e.CarClassShort = e.CarScreenName;
            Drivers.Add(e);
            if (e.CarIdx == PlayerCarIdx) Player = e;
        }
        if (Player != null)
        {
            CarPath = Player.CarPath;
            CarName = Player.CarScreenName;
            CarClass = Player.CarClassShort;
        }
        CarId = Player != null ? d.Path("Drivers")?.Items.FirstOrDefault(x => x.Int("CarIdx") == PlayerCarIdx)?.Int("CarID") ?? 0 : 0;

        var s = Root["SessionInfo"] ?? YNode.Empty;
        CurrentSessionNum = s.Int("CurrentSessionNum");
        foreach (var n in (s["Sessions"]?.Items ?? Array.Empty<YNode>()))
        {
            var def = new SessionDef
            {
                Num = n.Int("SessionNum"),
                Type = n.Str("SessionType"),
                Name = n.Str("SessionName"),
            };
            var laps = n.Str("SessionLaps");
            if (!laps.Contains("unlimited", StringComparison.OrdinalIgnoreCase) && int.TryParse(laps, out var l)) def.Laps = l;
            var time = n.Str("SessionTime");
            if (!time.Contains("unlimited", StringComparison.OrdinalIgnoreCase)) { var t = YNode.ParseNum(time); if (!double.IsNaN(t)) def.TimeSec = t; }
            foreach (var r in (n["ResultsPositions"]?.Items ?? Array.Empty<YNode>()))
            {
                def.Results.Add(new ResultPosition
                {
                    Position = r.Int("Position"), ClassPosition = r.Int("ClassPosition"), CarIdx = r.Int("CarIdx"),
                    Lap = r.Int("Lap"), LapsComplete = r.Int("LapsComplete"), Incidents = r.Int("Incidents"),
                    FastestLap = r.Int("FastestLap"), FastestTime = r.Num("FastestTime", -1), LastTime = r.Num("LastTime", -1),
                    Time = r.Num("Time", -1), ReasonOut = r.Str("ReasonOutStr"),
                });
            }
            Sessions.Add(def);
        }

        var sectors = Root.Path("SplitTimeInfo.Sectors")?.Items.Select(x => (float)x.Num("SectorStartPct", 0)).OrderBy(x => x).ToArray();
        if (sectors is { Length: > 0 }) SectorStarts = sectors;

        CarSetup = Root["CarSetup"];
        SetupUpdateCount = CarSetup?.Int("UpdateCount") ?? 0;
    }

    public SessionDef? Session(int num) => Sessions.FirstOrDefault(x => x.Num == num);
    public DriverEntry? Driver(int carIdx) => Drivers.FirstOrDefault(x => x.CarIdx == carIdx);

    public string TrackKey => $"{TrackId}:{TrackName}";
    public string TrackLabel => string.IsNullOrEmpty(TrackConfig) ? TrackDisplayName : $"{TrackDisplayName} – {TrackConfig}";

    public float TankCapacityL => FuelMaxL * (MaxFuelPct > 0 ? MaxFuelPct : 1);

    /// <summary>Stable identity of a session so live recording and .ibt import land in the same place.</summary>
    public string SessionKey(int sessionNum, DateTime localStart)
    {
        if (SubSessionId > 0) return $"ss:{SubSessionId}:{sessionNum}";
        return $"off:{CarPath}:{TrackId}:{localStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
    }

    /// <summary>Car category used by the setup optimiser and tyre targets.</summary>
    public string CarCategory => CarCategoryFor(CarPath, CarClass, Category);

    public static string CarCategoryFor(string carPath, string carClass, string category)
    {
        string p = (carPath + " " + carClass).ToLowerInvariant();
        if (category.Contains("Oval", StringComparison.OrdinalIgnoreCase)) return "oval";
        if (category.Contains("Formula", StringComparison.OrdinalIgnoreCase)) return "formula";
        string[] proto = { "gtp", "lmdh", "lmp", "dpi", "hypercar" };
        string[] gt = { "gt3", "gte", "gt4", "gt2", "gt1", "992", "cup", "gtd" };
        string[] formula = { "formula", "superformula", "lights", "f1", "f3", "f4", "fr2", "fr3", "indy", "dallara", "skip", "vee", "williams", "mercedesw", "lotus" };
        if (proto.Any(p.Contains)) return "prototype";
        if (gt.Any(p.Contains)) return "gt";
        if (formula.Any(p.Contains)) return "formula";
        return "touring";
    }

    static string HexColor(string v, string def)
    {
        v = v.Trim();
        if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && v.Length > 2)
        {
            var hex = v[2..].PadLeft(6, '0');
            if (hex.Length > 6) hex = hex[^6..];
            return "#" + hex.ToLowerInvariant();
        }
        return def;
    }
}
