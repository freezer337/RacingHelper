using RacingHelper.Recording;
using RacingHelper.Storage;

namespace RacingHelper.Analysis;

public sealed class Insight
{
    public string Category { get; set; } = "";   // pace, consistency, corner, technique, tyres, handling, fuel, safety
    public int Severity { get; set; }            // 3 = high impact, 2 = medium, 1 = info, 0 = positive
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string? Corner { get; set; }
}

public sealed class StintSummary
{
    public int Stint { get; set; }
    public int Laps { get; set; }
    public int ValidLaps { get; set; }
    public double Best { get; set; }
    public double Average { get; set; }
    public double StdDev { get; set; }
    public double TrendPerLap { get; set; }   // s/lap, + = getting slower
    public double FuelPerLap { get; set; } = double.NaN;
    public List<double> LapTimes { get; set; } = new();
    public List<bool> LapValid { get; set; } = new();
}

public sealed class CornerInsight
{
    public int Corner { get; set; }
    public string Name { get; set; } = "";
    public float BestTime { get; set; }
    public float AvgTime { get; set; }
    public float AvgLossToBest { get; set; }
    public float MinSpeedBest { get; set; }
    public float MinSpeedAvg { get; set; }
    public float BrakePointSpread { get; set; }   // std dev of braking point (m) — consistency
    public CornerComparison? VsReference { get; set; }
    public int Lockups { get; set; }
}

public sealed class SessionReport
{
    public int Laps { get; set; }
    public int ValidLaps { get; set; }
    public double BestLap { get; set; }
    public int BestLapNumber { get; set; }
    public long BestLapId { get; set; }
    public double AverageLap { get; set; }
    public double StdDev { get; set; }
    public double TheoreticalBest { get; set; }
    public double OptimalSectors { get; set; }
    public List<StintSummary> Stints { get; set; } = new();
    public List<CornerInsight> Corners { get; set; } = new();
    public LapStyle? Style { get; set; }
    public List<Insight> Insights { get; set; } = new();
    public HandlingReport? Handling { get; set; }
    public List<TyreAdvice> Tyres { get; set; } = new();
    public double FuelPerLapAvg { get; set; } = double.NaN;
    public double FuelPerLapMax { get; set; } = double.NaN;
    public string ReferenceLabel { get; set; } = "";
    public double ReferenceLapTime { get; set; }
}

public sealed class AnalyzedLap
{
    public required LapRow Row { get; init; }
    public LapData? Data { get; init; }
    public DistLap? Dist { get; set; }
    public LapAnalysis? Analysis { get; set; }
}

public sealed class ReferenceLap
{
    public required string Label { get; init; }
    public required DistLap Dist { get; init; }
    public required LapAnalysis Analysis { get; init; }
    public long LapId { get; init; }
    public bool HasGps { get; init; }
    // line in the track model's frame (filled lazily by the live engine)
    public float[]? AlignedX { get; set; }
    public float[]? AlignedY { get; set; }
}

public static class SessionAnalyzer
{
    public static SessionReport Analyze(List<AnalyzedLap> laps, TrackModel? model, string category, float shiftRpm, float blinkRpm, ReferenceLap? reference, TyreTarget tyreTarget)
    {
        var rep = new SessionReport { Laps = laps.Count };
        AssignStints(laps.Select(l => l.Row).ToList());
        var valid = laps.Where(l => l.Row.Valid).ToList();
        rep.ValidLaps = valid.Count;

        // ---- lap analysis per lap ----
        if (model != null)
            foreach (var l in laps)
            {
                if (l.Data == null) continue;
                l.Dist ??= DistLap.TryCreate(l.Data, model.Length, l.Row.LapTime, !l.Row.OutLap);
                if (l.Dist != null && l.Row.Valid) l.Analysis = LapAnalyzer.Analyze(l.Dist, model, shiftRpm);
            }

        if (valid.Count > 0)
        {
            var best = valid.OrderBy(l => l.Row.LapTime).First();
            rep.BestLap = best.Row.LapTime;
            rep.BestLapNumber = best.Row.LapNumber;
            rep.BestLapId = best.Row.Id;
            var clean = valid.Where(l => l.Row.LapTime < rep.BestLap * 1.07).Select(l => l.Row.LapTime).ToList();
            rep.AverageLap = clean.Average();
            rep.StdDev = StdDev(clean);
            // optimal from iRacing sectors
            int ns = valid.Max(l => l.Row.SectorTimes.Length);
            if (ns > 1)
            {
                double opt = 0;
                for (int s = 0; s < ns; s++)
                    opt += valid.Where(l => l.Row.SectorTimes.Length == ns && l.Row.SectorTimes[s] > 0).Select(l => (double)l.Row.SectorTimes[s]).DefaultIfEmpty(double.NaN).Min();
                rep.OptimalSectors = opt;
            }
        }

        // ---- stints ----
        foreach (var g in laps.GroupBy(l => l.Row.Stint).OrderBy(g => g.Key))
        {
            var st = new StintSummary { Stint = g.Key, Laps = g.Count() };
            var v = g.Where(l => l.Row.Valid).ToList();
            st.ValidLaps = v.Count;
            st.LapTimes = g.Select(l => l.Row.LapTime).ToList();
            st.LapValid = g.Select(l => l.Row.Valid).ToList();
            if (v.Count > 0)
            {
                st.Best = v.Min(l => l.Row.LapTime);
                var clean = v.Where(l => l.Row.LapTime < st.Best * 1.07).Select(l => l.Row.LapTime).ToList();
                st.Average = clean.Average();
                st.StdDev = StdDev(clean);
                // trend ignoring the first two flying laps (tyre warm-up)
                var trendLaps = v.Where(l => l.Row.LapTime < st.Best * 1.05).Skip(v.Count > 5 ? 2 : 0).ToList();
                if (trendLaps.Count >= 4) st.TrendPerLap = Slope(trendLaps.Select((l, i) => ((double)i, l.Row.LapTime)).ToList());
            }
            var fuel = g.Where(l => !l.Row.OutLap && !l.Row.InLap && l.Row.FuelUsed is > 0.05f).Select(l => (double)l.Row.FuelUsed!.Value).ToList();
            if (fuel.Count > 0) st.FuelPerLap = fuel.Average();
            rep.Stints.Add(st);
        }
        var allFuel = laps.Where(l => !l.Row.OutLap && !l.Row.InLap && l.Row.FuelUsed is > 0.05f).Select(l => (double)l.Row.FuelUsed!.Value).ToList();
        if (allFuel.Count > 0) { rep.FuelPerLapAvg = allFuel.Average(); rep.FuelPerLapMax = allFuel.Max(); }

        // ---- corners ----
        var analysed = valid.Where(l => l.Analysis != null).ToList();
        if (model != null && analysed.Count > 0)
        {
            double theo = 0;
            foreach (var c in model.Corners)
            {
                var ms = analysed.Select(l => l.Analysis!.Corners.FirstOrDefault(x => x.Corner == c.Index)).Where(x => x != null && float.IsFinite(x.SegTime)).ToList();
                if (ms.Count == 0) continue;
                var ci = new CornerInsight
                {
                    Corner = c.Index, Name = c.Name,
                    BestTime = ms.Min(x => x!.SegTime),
                    AvgTime = ms.Average(x => x!.SegTime),
                    MinSpeedBest = ms.Max(x => x!.MinSpeed) * 3.6f,
                    MinSpeedAvg = ms.Average(x => x!.MinSpeed) * 3.6f,
                    Lockups = ms.Count(x => x!.LockupFront || x.LockupRear),
                };
                ci.AvgLossToBest = ci.AvgTime - ci.BestTime;
                var bps = ms.Where(x => float.IsFinite(x!.BrakePoint)).Select(x => (double)x!.BrakePoint).ToList();
                if (bps.Count >= 3) ci.BrakePointSpread = (float)StdDev(bps);
                theo += ci.BestTime;
                rep.Corners.Add(ci);
            }
            if (rep.Corners.Count == model.Corners.Count) rep.TheoreticalBest = theo;

            rep.Style = AverageStyle(analysed.Select(l => l.Analysis!.Style).ToList());

            if (reference != null)
            {
                var bestLap = analysed.OrderBy(l => l.Row.LapTime).First();
                var cmp = LapComparer.Compare(bestLap.Dist!, bestLap.Analysis!, reference.Dist, reference.Analysis);
                rep.ReferenceLabel = reference.Label;
                rep.ReferenceLapTime = reference.Dist.LapTime;
                foreach (var cc in cmp.Corners)
                {
                    var ci = rep.Corners.FirstOrDefault(x => x.Corner == cc.Corner);
                    if (ci != null) ci.VsReference = cc;
                }
            }

            rep.Handling = HandlingAnalyzer.Analyze(analysed.Where(l => l.Data != null).Select(l => l.Data!), model);
        }

        var tyreLaps = laps.Where(l => !l.Row.OutLap && l.Row.Tyres != null).Select(l => l.Row.Tyres!).ToList();
        if (tyreLaps.Count > 0) rep.Tyres = TyreAnalyzer.Analyze(tyreLaps, tyreTarget);

        BuildInsights(rep, laps, shiftRpm, blinkRpm);
        return rep;
    }

    static void BuildInsights(SessionReport rep, List<AnalyzedLap> laps, float shiftRpm, float blinkRpm)
    {
        var ins = rep.Insights;
        string T(double s) => Fmt.LapTime(s);

        if (rep.ValidLaps == 0)
        {
            ins.Add(new Insight { Category = "pace", Severity = 1, Title = "No clean laps yet", Detail = "Complete a full lap without going off track to unlock lap analysis." });
            return;
        }
        ins.Add(new Insight { Category = "pace", Severity = 0, Title = $"Best lap {T(rep.BestLap)} (lap {rep.BestLapNumber})", Detail = $"{rep.ValidLaps} clean laps of {rep.Laps}. Average {T(rep.AverageLap)}." });

        if (rep.TheoreticalBest > 0 && rep.BestLap - rep.TheoreticalBest > 0.05)
        {
            var biggest = rep.Corners.OrderByDescending(c => c.AvgLossToBest).Take(3).Select(c => c.Name);
            ins.Add(new Insight
            {
                Category = "pace", Severity = 2,
                Title = $"{rep.BestLap - rep.TheoreticalBest:0.00}s available by combining your best corners",
                Detail = $"Theoretical best {T(rep.TheoreticalBest)}. Biggest lap-to-lap variation: {string.Join(", ", biggest)}.",
            });
        }

        if (rep.ValidLaps >= 4)
        {
            if (rep.StdDev > 0.6) ins.Add(new Insight { Category = "consistency", Severity = 3, Title = $"Inconsistent pace (±{rep.StdDev:0.00}s)", Detail = "Work on repeating the same braking points. Pick fixed visual markers for each braking zone." });
            else if (rep.StdDev < 0.25) ins.Add(new Insight { Category = "consistency", Severity = 0, Title = $"Very consistent (±{rep.StdDev:0.00}s)", Detail = "Great repeatability — you are ready to push for the next step in pace." });
            var spread = rep.Corners.Where(c => c.BrakePointSpread > 12).OrderByDescending(c => c.BrakePointSpread).Take(2).ToList();
            foreach (var c in spread)
                ins.Add(new Insight { Category = "consistency", Severity = 2, Corner = c.Name, Title = $"{c.Name}: braking point varies by ±{c.BrakePointSpread:0} m", Detail = "An unsteady braking point makes the whole corner inconsistent. Commit to one marker." });
        }

        // reference comparison
        foreach (var c in rep.Corners.Where(c => c.VsReference != null && c.VsReference.TimeDelta > 0.04f).OrderByDescending(c => c.VsReference!.TimeDelta).Take(4))
        {
            var cc = c.VsReference!;
            ins.Add(new Insight
            {
                Category = "corner", Severity = cc.TimeDelta > 0.15f ? 3 : 2, Corner = c.Name,
                Title = $"{c.Name}: −{cc.TimeDelta:0.00}s vs {rep.ReferenceLabel}",
                Detail = string.Join(" ", cc.Advice),
            });
        }

        // technique
        var s = rep.Style;
        if (s != null)
        {
            if (s.CoastingPct > 4) ins.Add(new Insight { Category = "technique", Severity = 2, Title = $"Coasting {s.CoastingPct:0.0}% of the lap", Detail = "Time with neither pedal is usually lost time. Overlap the end of braking with turn-in and get to the throttle as soon as the car is rotated." });
            if (s.TrailBrakePct < 15 && s.BrakingPct > 5) ins.Add(new Insight { Category = "technique", Severity = 2, Title = "Little trail braking", Detail = $"Only {s.TrailBrakePct:0}% of your braking happens while turning. Releasing the brake gradually into the corner keeps the front loaded and improves rotation." });
            if (s.OverlapPct > 2) ins.Add(new Insight { Category = "technique", Severity = 1, Title = $"Throttle and brake overlap {s.OverlapPct:0.0}% of the lap", Detail = "Unless deliberate (left-foot braking), this scrubs speed and heats brakes." });
            if (shiftRpm > 0 && s.Upshifts > 2)
            {
                if (s.UpshiftRpm < shiftRpm * 0.95) ins.Add(new Insight { Category = "technique", Severity = 2, Title = $"Upshifting early ({s.UpshiftRpm:0} rpm)", Detail = $"The shift light is at {shiftRpm:0} rpm. Holding gears longer keeps the engine in its power band." });
                else if (s.UpshiftRpm > shiftRpm * 1.06) ins.Add(new Insight { Category = "technique", Severity = 1, Title = $"Upshifting around {s.UpshiftRpm:0} rpm", Detail = $"That's past the shift light ({shiftRpm:0} rpm){(blinkRpm > 0 && s.UpshiftRpm > blinkRpm ? " and into the over-rev blink" : "")}. Unless this car pulls harder near the limiter, shifting at the light is usually quicker." });
            }
            if (s.AbsPct > 40) ins.Add(new Insight { Category = "technique", Severity = 1, Title = $"ABS active in {s.AbsPct:0}% of braking", Detail = "Leaning on ABS lengthens braking distances. Aim for peak pressure just below the ABS threshold." });
        }
        var lockCorners = rep.Corners.Where(c => c.Lockups > 0).OrderByDescending(c => c.Lockups).Take(3).ToList();
        if (lockCorners.Count > 0)
            ins.Add(new Insight { Category = "technique", Severity = 2, Title = "Lock-ups detected", Detail = string.Join(", ", lockCorners.Select(c => $"{c.Name} ×{c.Lockups}")) + ". Reduce peak pressure or adjust brake bias." });

        // pace evolution
        foreach (var st in rep.Stints.Where(st => st.ValidLaps >= 5))
        {
            if (st.TrendPerLap > 0.05) ins.Add(new Insight { Category = "tyres", Severity = 2, Title = $"Stint {st.Stint}: losing {st.TrendPerLap:0.00}s per lap", Detail = "Pace drops through the stint — tyre degradation or overheating. Check the Tyres page." });
            else if (st.TrendPerLap < -0.05) ins.Add(new Insight { Category = "pace", Severity = 0, Title = $"Stint {st.Stint}: improving {-st.TrendPerLap:0.00}s per lap", Detail = "Track evolution and learning are working in your favour." });
        }

        // tyres: merge identical findings across corners of the car
        if (rep.Tyres.Count == 4)
        {
            var press = rep.Tyres.Where(t => t.PressStatus is "low" or "high").GroupBy(t => t.PressStatus);
            foreach (var g in press)
            {
                var names = string.Join("/", g.Select(t => t.Tyre));
                var detail = string.Join(" ", g.Where(t => float.IsFinite(t.SuggestedCold)).Select(t => $"{t.Tyre} {t.PressCold:0}→{t.SuggestedCold:0} kPa"));
                ins.Add(new Insight { Category = "tyres", Severity = 2, Title = $"{names} hot pressures {g.Key} ({string.Join(", ", g.Select(t => $"{t.PressHot:0}"))} kPa)", Detail = $"Suggested cold pressures: {detail}. Target window is editable on the Tyres page." });
            }
            foreach (var g in rep.Tyres.Where(t => t.TempStatus is "cold" or "hot").GroupBy(t => t.TempStatus))
                ins.Add(new Insight { Category = "tyres", Severity = 1, Title = $"{string.Join("/", g.Select(t => t.Tyre))} {(g.Key == "cold" ? "below" : "above")} target temperature ({string.Join(", ", g.Select(t => $"{t.TempAvg:0}°C"))})", Detail = (g.Key == "cold" ? "Cold tyres lose grip. Raise pressures or push harder on the out-lap." : "Overheating tyres lose grip; reduce sliding on that axle.") + " The target window is a generic default per car type — adjust it on the Tyres page to match what's fast for this car." });
            foreach (var t in rep.Tyres)
                foreach (var n in t.Notes.Where(n => n.StartsWith("Camber") || n.Contains("inflated") || n.Contains("hotter than rears") || n.Contains("hotter than fronts")))
                    ins.Add(new Insight { Category = "tyres", Severity = 1, Title = $"{t.Tyre} tyre", Detail = n });
        }

        if (rep.Handling is { Valid: true })
            foreach (var f in rep.Handling.Findings.Take(3))
                ins.Add(new Insight { Category = "handling", Severity = f.StartsWith("Balance") ? 0 : 2, Title = "Handling", Detail = f + (f.StartsWith("Balance") ? "" : " See Setup → Optimiser for fixes.") });

        int inc = laps.Sum(l => l.Row.Incidents), off = laps.Sum(l => l.Row.OffTracks);
        if (off > 0) ins.Add(new Insight { Category = "safety", Severity = off > 3 ? 2 : 1, Title = $"{off} off-track excursion{(off > 1 ? "s" : "")}, {inc}x incidents", Detail = "Off-tracks invalidate laps — find the limit by creeping up, not by overshooting." });

        if (double.IsFinite(rep.FuelPerLapAvg))
            ins.Add(new Insight { Category = "fuel", Severity = 1, Title = $"Fuel {rep.FuelPerLapAvg:0.00} L/lap (max {rep.FuelPerLapMax:0.00})", Detail = "Used by the fuel calculator for race planning." });
    }

    /// <summary>
    /// Stints are derived from the lap sequence (an out-lap or a long gap starts a new one), so sessions
    /// stitched together from several files / live recordings still number stints 1..n.
    /// </summary>
    public static void AssignStints(List<LapRow> laps)
    {
        int stint = 0;
        DateTime prevEnd = DateTime.MinValue;
        foreach (var l in laps.OrderBy(l => l.StartedAt))
        {
            bool gap = prevEnd != DateTime.MinValue && (l.StartedAt - prevEnd).TotalSeconds > 90;
            if (stint == 0 || l.OutLap || gap) stint++;
            l.Stint = stint;
            prevEnd = l.StartedAt.AddSeconds(l.LapTime);
        }
    }

    public static LapStyle AverageStyle(List<LapStyle> styles)
    {
        if (styles.Count == 0) return new LapStyle();
        return new LapStyle
        {
            FullThrottlePct = styles.Average(s => s.FullThrottlePct),
            BrakingPct = styles.Average(s => s.BrakingPct),
            CoastingPct = styles.Average(s => s.CoastingPct),
            OverlapPct = styles.Average(s => s.OverlapPct),
            TrailBrakePct = styles.Average(s => s.TrailBrakePct),
            SteeringRate = styles.Average(s => s.SteeringRate),
            ThrottleRate = styles.Average(s => s.ThrottleRate),
            UpshiftRpm = styles.Where(s => s.Upshifts > 0).Select(s => s.UpshiftRpm).DefaultIfEmpty(0).Average(),
            Upshifts = (int)styles.Average(s => s.Upshifts),
            Lockups = styles.Sum(s => s.Lockups),
            Wheelspins = styles.Sum(s => s.Wheelspins),
            AbsPct = styles.Average(s => s.AbsPct),
            AvgPeakBrake = styles.Average(s => s.AvgPeakBrake),
            MaxLatG = styles.Max(s => s.MaxLatG),
            TopSpeed = styles.Max(s => s.TopSpeed),
        };
    }

    public static double StdDev(IReadOnlyList<double> v)
    {
        if (v.Count < 2) return 0;
        double m = v.Average();
        return Math.Sqrt(v.Sum(x => (x - m) * (x - m)) / (v.Count - 1));
    }

    static double Slope(List<(double x, double y)> pts)
    {
        double mx = pts.Average(p => p.x), my = pts.Average(p => p.y), sxx = 0, sxy = 0;
        foreach (var (x, y) in pts) { sxx += (x - mx) * (x - mx); sxy += (x - mx) * (y - my); }
        return sxx > 0 ? sxy / sxx : 0;
    }
}

public static class Fmt
{
    public static string LapTime(double s)
    {
        if (!double.IsFinite(s) || s <= 0) return "—";
        int m = (int)(s / 60);
        double r = s - m * 60;
        return m > 0 ? $"{m}:{r:00.000}" : $"{r:0.000}";
    }
}
