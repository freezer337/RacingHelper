using RacingHelper.Analysis;

namespace RacingHelper.Live;

public sealed class SectorLive
{
    public int Index { get; set; }
    public float StartPct { get; set; }
    public double Time { get; set; } = double.NaN;       // this lap (or running time for the current sector)
    public double RefTime { get; set; } = double.NaN;
    public double BestTime { get; set; } = double.NaN;   // session best for this sector
    public double Delta { get; set; } = double.NaN;      // vs reference
    public string State { get; set; } = "pending";       // pending / current / done
    public string Color { get; set; } = "";              // purple (session best) / green (better than ref) / yellow (slower)
}

public sealed class CornerLive
{
    public string Name { get; set; } = "";
    public float TimeDelta { get; set; }
    public float MinSpeedDiff { get; set; } = float.NaN;
    public float BrakeDiff { get; set; } = float.NaN;
    public float ExitSpeedDiff { get; set; } = float.NaN;
    public string Verdict { get; set; } = "";
    public string Advice { get; set; } = "";
    public float MinSpeed { get; set; }
    public int Lap { get; set; }
}

public sealed class TyreLive
{
    public string Name { get; set; } = "";
    public float Pressure { get; set; } = float.NaN;       // hot (kPa) if available
    public float ColdPressure { get; set; } = float.NaN;
    public float TempIn { get; set; } = float.NaN;
    public float TempMid { get; set; } = float.NaN;
    public float TempOut { get; set; } = float.NaN;
    public bool Carcass { get; set; }                      // temps are pit-measured carcass values (live iRacing)
    public float Wear { get; set; } = float.NaN;
    public string PressStatus { get; set; } = "";
    public string TempStatus { get; set; } = "";
}

public sealed class CarRow
{
    public int CarIdx { get; set; }
    public int Position { get; set; }
    public int ClassPosition { get; set; }
    public string Number { get; set; } = "";
    public string Name { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string ClassColor { get; set; } = "#ffffff";
    public int IRating { get; set; }
    public double IRatingChange { get; set; } = double.NaN;
    public string License { get; set; } = "";
    public string LicenseColor { get; set; } = "#888";
    public double Gap { get; set; } = double.NaN;          // to class leader (s)
    public double Interval { get; set; } = double.NaN;     // to car ahead in class (s)
    public double Relative { get; set; } = double.NaN;     // relative time to player (+ ahead)
    public double LastLap { get; set; } = double.NaN;
    public double BestLap { get; set; } = double.NaN;
    public int Lap { get; set; }
    public int LapDiff { get; set; }                       // laps ahead (+) / behind (−) of the player
    public bool InPit { get; set; }
    public bool IsPlayer { get; set; }
    public string Tyre { get; set; } = "";
    public float Pct { get; set; }
}

public sealed class RadarCar
{
    public int CarIdx { get; set; }
    public float Dist { get; set; }       // m, + ahead
    public string ClassColor { get; set; } = "#fff";
}

public sealed class MapCar
{
    public int CarIdx { get; set; }
    public float Pct { get; set; }
    public bool IsPlayer { get; set; }
    public string ClassColor { get; set; } = "#fff";
    public int Position { get; set; }
    public string Number { get; set; } = "";
    public bool InPit { get; set; }
}

public sealed class WeatherLive
{
    public float AirTemp { get; set; } = float.NaN;
    public float TrackTemp { get; set; } = float.NaN;
    public string Skies { get; set; } = "";
    public string Wetness { get; set; } = "";
    public int WetnessLevel { get; set; }
    public float Precipitation { get; set; }
    public float WindSpeed { get; set; }
    public float WindDir { get; set; }
    public float Humidity { get; set; }
    public bool DeclaredWet { get; set; }
    public float TrackTempTrend { get; set; }          // °C / hour
    public List<WeatherTrend.Projection> Forecast { get; set; } = new();
}

public sealed class HealthLive
{
    public float WaterTemp { get; set; } = float.NaN;
    public float OilTemp { get; set; } = float.NaN;
    public float OilPress { get; set; } = float.NaN;
    public float Voltage { get; set; } = float.NaN;
    public List<string> Warnings { get; set; } = new();
    public float RepairLeft { get; set; }               // mandatory repair seconds
    public float OptRepairLeft { get; set; }            // optional repair seconds
    public bool RepairFlag { get; set; }                // meatball
    public bool PossibleDamage { get; set; }
    public float PaceLossPct { get; set; }
    public int Incidents { get; set; }
    public int IncidentLimit { get; set; }
}

public sealed class EngineerMessage
{
    public DateTime At { get; set; } = DateTime.Now;
    public string Text { get; set; } = "";
    public string Category { get; set; } = "info";      // info, lap, corner, fuel, flag, warning, pb, setup
    public int Priority { get; set; } = 1;              // 0 low .. 3 critical
    public bool Speak { get; set; } = true;
}

/// <summary>Immutable-by-convention snapshot of everything live, published ~20× per second.</summary>
public sealed class LiveState
{
    public DateTime At { get; set; } = DateTime.Now;
    public string Status { get; set; } = "waiting";       // waiting (no sim) / connected / driving / replay
    public string Source { get; set; } = "";
    public bool InCar { get; set; }
    public bool OnPitRoad { get; set; }

    public string Car { get; set; } = "";
    public string CarPath { get; set; } = "";
    public string CarClass { get; set; } = "";
    public string Track { get; set; } = "";
    public string TrackKey { get; set; } = "";
    public float TrackLength { get; set; }
    public string SessionType { get; set; } = "";
    public double SessionTimeRemain { get; set; } = double.NaN;
    public int SessionLapsRemain { get; set; } = -1;
    public int SessionLapsTotal { get; set; } = -1;
    public List<string> Flags { get; set; } = new();
    public long SessionDbId { get; set; }
    public int Position { get; set; }
    public int ClassPosition { get; set; }
    public int CarsInClass { get; set; }

    // timing
    public int Lap { get; set; }
    public float LapPct { get; set; }
    public float LapDist { get; set; }
    public double CurrentLapTime { get; set; } = double.NaN;
    public double LastLapTime { get; set; } = double.NaN;
    public double SessionBest { get; set; } = double.NaN;
    public double PersonalBest { get; set; } = double.NaN;
    public double Delta { get; set; } = double.NaN;
    public double DeltaRate { get; set; }
    public double PredictedLap { get; set; } = double.NaN;
    public string ReferenceLabel { get; set; } = "";
    public double ReferenceLapTime { get; set; } = double.NaN;
    public string ReferenceMode { get; set; } = "pb";
    public bool OutLap { get; set; }
    public List<SectorLive> Sectors { get; set; } = new();
    public double LastLapDelta { get; set; } = double.NaN;

    // inputs (now) + reference at the same distance
    public float Throttle { get; set; }
    public float Brake { get; set; }
    public float Clutch { get; set; }
    public float Steer { get; set; }
    public float SteerMax { get; set; } = float.NaN;
    public float Speed { get; set; }
    public float Rpm { get; set; }
    public float ShiftRpm { get; set; }
    public float RedLine { get; set; }
    public int Gear { get; set; }
    public bool Abs { get; set; }
    public float BrakeBias { get; set; } = float.NaN;
    public float RefThrottle { get; set; } = float.NaN;
    public float RefBrake { get; set; } = float.NaN;
    public float RefSpeed { get; set; } = float.NaN;
    public int RefGear { get; set; }
    // last ~6 s of inputs (oldest → newest), sampled at ~30 Hz
    public float[] HistThrottle { get; set; } = Array.Empty<float>();
    public float[] HistBrake { get; set; } = Array.Empty<float>();
    public float[] HistSteer { get; set; } = Array.Empty<float>();
    public float[] HistSpeed { get; set; } = Array.Empty<float>();
    public float[] HistRefThrottle { get; set; } = Array.Empty<float>();
    public float[] HistRefBrake { get; set; } = Array.Empty<float>();
    public float[] HistRefSpeed { get; set; } = Array.Empty<float>();

    // brake boards / corner analysis
    public float NextBrakeDist { get; set; } = float.NaN;
    public string NextCorner { get; set; } = "";
    public float NextCornerRefMinSpeed { get; set; } = float.NaN;
    public CornerLive? LastCorner { get; set; }
    public List<CornerLive> LapCorners { get; set; } = new();

    public LiveFuel? Fuel { get; set; }
    public float TankCapacity { get; set; }
    public List<TyreLive> Tyres { get; set; } = new();
    public WeatherLive Weather { get; set; } = new();
    public HealthLive Health { get; set; } = new();

    public List<CarRow> Standings { get; set; } = new();
    public List<CarRow> Relative { get; set; } = new();
    public Dictionary<string, int> ClassSof { get; set; } = new();
    public int CarLeftRight { get; set; }
    public List<RadarCar> Radar { get; set; } = new();
    public List<MapCar> MapCars { get; set; } = new();
    public float PlayerX { get; set; } = float.NaN;
    public float PlayerY { get; set; } = float.NaN;
    public float PlayerHeading { get; set; } = float.NaN;   // radians, direction of travel in map frame
    public float[] TrailX { get; set; } = Array.Empty<float>(); // your last ~300 m in the map frame (oldest → newest)
    public float[] TrailY { get; set; } = Array.Empty<float>();
    public int TrackModelVersion { get; set; }

    public List<EngineerMessage> Messages { get; set; } = new();
}
