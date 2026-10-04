namespace RacingHelper.Analysis;

public sealed class FuelInput
{
    public double RaceMinutes { get; set; }      // 0 if lap-based
    public int RaceLaps { get; set; }            // 0 if time-based
    public double LapTimeSec { get; set; }
    public double FuelPerLap { get; set; }
    public double TankLitres { get; set; }
    public double StartFuel { get; set; } = -1;  // -1 = full tank
    public double MarginLaps { get; set; } = 1;
    public double PitLossSec { get; set; } = 0;  // used for time-based races (fewer laps if stopping)
}

public sealed class FuelPlan
{
    public double TotalLaps { get; set; }
    public double TotalFuel { get; set; }
    public int PitStops { get; set; }
    public double StartFuel { get; set; }
    public List<double> StintFuel { get; set; } = new();
    public double FinalStintFuel { get; set; }
    public double LapsPerTank { get; set; }
    public string Summary { get; set; } = "";
}

public sealed class LiveFuel
{
    public double FuelLevel { get; set; }
    public double PerLapLast { get; set; } = double.NaN;
    public double PerLapAvg { get; set; } = double.NaN;   // rolling average of recent green laps
    public double PerLapMax { get; set; } = double.NaN;
    public double PerLapPredicted { get; set; } = double.NaN;
    public double LapsInTank { get; set; } = double.NaN;
    public double LapsRemaining { get; set; } = double.NaN;  // laps to the finish
    public double FuelToFinish { get; set; } = double.NaN;   // incl. margin
    public double FuelToAdd { get; set; } = double.NaN;      // at the next stop (0 if enough)
    public int StopsRemaining { get; set; }
    public double Spare { get; set; } = double.NaN;          // litres left over at the finish (negative = short)
    public double EndsLap { get; set; } = double.NaN;        // on which lap fuel runs out
    public bool Estimated { get; set; }
}

public static class FuelCalculator
{
    public static FuelPlan Plan(FuelInput i)
    {
        var p = new FuelPlan();
        if (i.FuelPerLap <= 0 || i.TankLitres <= 0) { p.Summary = "Enter fuel per lap and tank size."; return p; }
        double laps = i.RaceLaps > 0 ? i.RaceLaps : i.LapTimeSec > 0 ? Math.Ceiling(i.RaceMinutes * 60 / i.LapTimeSec) + 1 : 0;
        // timed races: the leader crosses after time expires → +1 lap above (approximation)
        if (laps <= 0) { p.Summary = "Enter race length and lap time."; return p; }
        double need = (laps + i.MarginLaps) * i.FuelPerLap;
        double start = i.StartFuel >= 0 ? Math.Min(i.StartFuel, i.TankLitres) : i.TankLitres;
        int stops = need <= start ? 0 : (int)Math.Ceiling((need - start) / i.TankLitres);

        if (i.RaceLaps <= 0 && stops > 0 && i.PitLossSec > 0 && i.LapTimeSec > 0)
        {
            // each stop costs time → fewer laps in a timed race
            double lapsLost = stops * i.PitLossSec / i.LapTimeSec;
            laps = Math.Max(1, Math.Ceiling(i.RaceMinutes * 60 / i.LapTimeSec - lapsLost) + 1);
            need = (laps + i.MarginLaps) * i.FuelPerLap;
            stops = need <= start ? 0 : (int)Math.Ceiling((need - start) / i.TankLitres);
        }

        p.TotalLaps = laps;
        p.TotalFuel = need;
        p.PitStops = stops;
        p.LapsPerTank = i.TankLitres / i.FuelPerLap;
        if (stops == 0)
        {
            p.StartFuel = Math.Min(start, Math.Ceiling(need * 10) / 10);
            p.StintFuel.Add(p.StartFuel);
            p.FinalStintFuel = p.StartFuel;
        }
        else
        {
            // fill up at the start, then split the remaining fuel evenly across stops (keeps stops short and balanced)
            p.StartFuel = start;
            double remaining = need - start;
            p.StintFuel.Add(start);
            double perStop = remaining / stops;
            for (int s = 0; s < stops; s++) p.StintFuel.Add(Math.Ceiling(perStop * 10) / 10);
            p.FinalStintFuel = p.StintFuel[^1];
        }
        p.Summary = stops == 0
            ? $"No stop needed: start with {p.StartFuel:0.0} L for {laps:0} laps (+{i.MarginLaps:0.#} lap margin)."
            : $"{stops} stop{(stops > 1 ? "s" : "")}: {laps:0} laps need {need:0.0} L. Start full ({start:0.0} L), add ~{p.StintFuel[1]:0.0} L per stop.";
        return p;
    }

    /// <summary>Live fuel state from recent per-lap consumption.</summary>
    public static LiveFuel Live(double fuelLevel, IReadOnlyList<double> recentPerLap, double lapsRemaining, double tankLitres, double marginLaps, double completedFraction)
    {
        var f = new LiveFuel { FuelLevel = fuelLevel };
        var valid = recentPerLap.Where(x => x > 0.01 && double.IsFinite(x)).ToList();
        if (valid.Count == 0) { f.Estimated = true; return f; }
        f.PerLapLast = valid[^1];
        var last5 = valid.TakeLast(5).ToList();
        f.PerLapAvg = last5.Average();
        f.PerLapMax = last5.Max();
        // weighted towards recent laps, slightly pessimistic
        f.PerLapPredicted = Math.Max(f.PerLapAvg, f.PerLapLast * 0.6 + f.PerLapAvg * 0.4);
        f.LapsInTank = fuelLevel / f.PerLapPredicted;
        f.EndsLap = f.LapsInTank;
        if (double.IsFinite(lapsRemaining) && lapsRemaining >= 0)
        {
            double lapsToGo = Math.Max(0, lapsRemaining - completedFraction);
            f.LapsRemaining = lapsToGo;
            f.FuelToFinish = (lapsToGo + marginLaps) * f.PerLapPredicted;
            f.Spare = fuelLevel - lapsToGo * f.PerLapPredicted;
            double deficit = f.FuelToFinish - fuelLevel;
            f.FuelToAdd = Math.Max(0, deficit);
            f.StopsRemaining = deficit <= 0 || tankLitres <= 0 ? 0 : (int)Math.Ceiling(deficit / tankLitres);
            if (f.StopsRemaining > 0 && tankLitres > 0) f.FuelToAdd = Math.Min(deficit, tankLitres - Math.Max(0, fuelLevel - f.PerLapPredicted));
        }
        return f;
    }
}
