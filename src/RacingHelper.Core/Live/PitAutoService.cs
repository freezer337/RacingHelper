using RacingHelper.Analysis;
using RacingHelper.Sim;

namespace RacingHelper.Live;

/// <summary>iRacing's pit-service commands (the same ones the # chat macros use).</summary>
public interface IPitCommands
{
    bool Fuel(double litres);
    bool ClearFuel();
    bool Tyre(int tyre, double kpa);   // 0 LF 1 RF 2 LR 3 RR; kpa 0 = keep the current pressure
    bool ClearTyres();
    bool FastRepair();
    bool Windscreen();
}

public sealed class IRacingPitCommands : IPitCommands
{
    public bool Fuel(double litres) => IRacingBroadcast.SetFuel(litres);
    public bool ClearFuel() => IRacingBroadcast.Pit(IRacingBroadcast.PitCmd.ClearFuel);
    public bool Tyre(int tyre, double kpa) => IRacingBroadcast.SetTyrePressure(tyre, kpa);
    public bool ClearTyres() => IRacingBroadcast.Pit(IRacingBroadcast.PitCmd.ClearTires);
    public bool FastRepair() => IRacingBroadcast.Pit(IRacingBroadcast.PitCmd.FR);
    public bool Windscreen() => IRacingBroadcast.Pit(IRacingBroadcast.PitCmd.WS);
}

public sealed class PitPlan
{
    public double FuelLitres = double.NaN;    // NaN = leave fuel as it is, 0 = no fuel
    public bool Tyres;
    public float[] Pressures = { 0, 0, 0, 0 }; // kPa, 0 = current
    public bool PressuresFromHistory;
    public bool FastRepair, Windscreen;
    public List<string> Words = new();
}

/// <summary>
/// Sets up the pit stop for you as you enter pit road: fuel to the finish, tyres (with the cold pressures your
/// last run here says you need), fast repair when there's damage, and a tear-off. Uses iRacing's own pit-service
/// commands — the garage setup (wing, springs …) can't be changed at a stop.
/// </summary>
public sealed class PitAutoService
{
    readonly RaceEngineer _eng;
    readonly Func<AppSettings> _settings;
    string _kind = "practice";
    bool _prevPitRoad = true;
    float[]? _historyPressures;    // suggested cold pressures from your last run with this car here
    string _historyNote = "";

    public IPitCommands? Commands { get; set; }
    public PitPlan? LastPlan { get; private set; }

    public PitAutoService(RaceEngineer engineer, Func<AppSettings> settings)
    {
        _eng = engineer;
        _settings = settings;
    }

    public void Reset(string sessionType)
    {
        _kind = TyreManager.Kind(sessionType);
        _prevPitRoad = true;   // starting in the pits doesn't count as coming in
        LastPlan = null;
    }

    public void SetHistoryPressures(float[]? kpa, string note) { _historyPressures = kpa; _historyNote = note; }

    bool Active => _settings().AutoPit switch { "always" => true, "race" => _kind == "race", _ => false };

    /// <summary>Per frame: entering pit road → set the pit menu.</summary>
    public void Update(Frame f, LiveFuel? fuel, bool isLive)
    {
        bool pit = f.OnPitRoad;
        bool entered = pit && !_prevPitRoad && f.IsOnTrack && !f.PlayerCarInPitStall;
        _prevPitRoad = pit;
        if (!entered || !Active) return;
        var plan = Plan(f, fuel);
        LastPlan = plan;
        bool sent = isLive && Commands != null && Send(plan);
        string text = plan.Words.Count == 0 ? "Pit service unchanged." : "Pit service set: " + string.Join(", ", plan.Words) + ".";
        _eng.Say(isLive ? (sent ? text : "Couldn't set the pit menu. Check it yourself.") : text + " (replay, nothing sent)", "strategy", 2, immediate: true);
    }

    public PitPlan Plan(Frame f, LiveFuel? fuel)
    {
        var s = _settings();
        var p = new PitPlan();
        double lapsLeft = fuel?.LapsRemaining ?? double.NaN;

        if (s.AutoPitFuel && fuel != null && double.IsFinite(fuel.FuelToAdd))
        {
            p.FuelLitres = Math.Max(0, Math.Ceiling(fuel.FuelToAdd));
            p.Words.Add(p.FuelLitres > 0 ? $"{p.FuelLitres:0} litres" : "no fuel");
        }

        p.Tyres = s.AutoPitTyres switch
        {
            "always" => true,
            "never" => false,
            _ => !double.IsFinite(lapsLeft) || lapsLeft >= s.AutoPitTyreMinLaps,
        };
        if (p.Tyres)
        {
            if (s.AutoPitPressures && _historyPressures is { Length: 4 } h && h.All(x => x > 50))
            {
                p.Pressures = (float[])h.Clone();
                p.PressuresFromHistory = true;
                p.Words.Add($"four tyres at {string.Join(", ", h.Select(x => x.ToString("0")))} kPa");
            }
            else p.Words.Add("four tyres");
        }
        else if (s.AutoPitTyres != "manual") p.Words.Add(double.IsFinite(lapsLeft) ? $"no tyres, only {lapsLeft:0} laps left" : "no tyres");

        float repair = (float.IsFinite(f.PitRepairLeft) ? f.PitRepairLeft : 0) + (float.IsFinite(f.PitOptRepairLeft) ? f.PitOptRepairLeft : 0);
        if (s.AutoPitFastRepair && repair > 10) { p.FastRepair = true; p.Words.Add("fast repair"); }
        if (s.AutoPitWindscreen) { p.Windscreen = true; p.Words.Add("tear-off"); }
        return p;
    }

    bool Send(PitPlan p)
    {
        var c = Commands!;
        bool ok = true;
        var s = _settings();
        if (double.IsFinite(p.FuelLitres)) ok &= p.FuelLitres > 0 ? c.Fuel(p.FuelLitres) : c.ClearFuel();
        if (p.Tyres) for (int i = 0; i < 4; i++) ok &= c.Tyre(i, p.Pressures[i]);
        else if (s.AutoPitTyres != "manual") ok &= c.ClearTyres();
        if (p.FastRepair) ok &= c.FastRepair();
        if (p.Windscreen) ok &= c.Windscreen();
        return ok;
    }

    public string Describe(Frame f, LiveFuel? fuel)
    {
        if (!Active) return _settings().AutoPit == "off" ? "Automatic pit service is off." : "Automatic pit service only works in races. You can switch it to always in Settings.";
        var p = Plan(f, fuel);
        string hist = p.PressuresFromHistory ? $" Pressures from {_historyNote}." : "";
        return (p.Words.Count == 0 ? "Nothing to change at the stop." : "When you box I'll set: " + string.Join(", ", p.Words) + ".") + hist;
    }
}
