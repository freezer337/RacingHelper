namespace RacingHelper.Analysis;

public sealed class CornerComparison
{
    public int Corner { get; set; }
    public string Name { get; set; } = "";
    public float TimeDelta { get; set; }          // + = slower than reference
    public float BrakeDiff { get; set; } = float.NaN;   // m, + = braked later than reference
    public float EntrySpeedDiff { get; set; } = float.NaN; // km/h
    public float MinSpeedDiff { get; set; } = float.NaN;   // km/h
    public float ExitSpeedDiff { get; set; } = float.NaN;  // km/h
    public float ThrottleDiff { get; set; } = float.NaN;   // m, + = later throttle than reference
    public float CoastDiff { get; set; }                    // m
    public float TrailDiff { get; set; }                    // m
    public string Verdict { get; set; } = "";               // short one-liner for overlays / voice
    public List<string> Advice { get; set; } = new();
}

public sealed class LapComparison
{
    public double LapDelta { get; set; }
    public float[] Delta { get; set; } = Array.Empty<float>();     // per DistLap index, lap − reference (s)
    public List<CornerComparison> Corners { get; set; } = new();
    public List<CornerComparison> TopLosses => Corners.Where(c => c.TimeDelta > 0.02f).OrderByDescending(c => c.TimeDelta).Take(3).ToList();
}

public static class LapComparer
{
    const float Kmh = 3.6f;

    public static float[] DeltaTrace(DistLap lap, DistLap reference)
    {
        var d = new float[lap.N];
        for (int i = 0; i < lap.N; i++)
        {
            float a = lap.Time[i], b = i < reference.N ? reference.Time[i] : float.NaN;
            d[i] = float.IsFinite(a) && float.IsFinite(b) ? a - b : float.NaN;
        }
        return d;
    }

    public static LapComparison Compare(DistLap lap, LapAnalysis lapA, DistLap reference, LapAnalysis refA)
    {
        var res = new LapComparison { LapDelta = lap.LapTime - reference.LapTime, Delta = DeltaTrace(lap, reference) };
        foreach (var a in lapA.Corners)
        {
            var b = refA.Corners.FirstOrDefault(x => x.Corner == a.Corner);
            if (b == null) continue;
            res.Corners.Add(CompareCorner(a, b));
        }
        return res;
    }

    public static CornerComparison CompareCorner(CornerMetrics a, CornerMetrics b)
    {
        var c = new CornerComparison
        {
            Corner = a.Corner,
            Name = a.Name,
            TimeDelta = a.SegTime - b.SegTime,
            BrakeDiff = a.BrakePoint - b.BrakePoint,
            EntrySpeedDiff = (a.EntrySpeed - b.EntrySpeed) * Kmh,
            MinSpeedDiff = (a.MinSpeed - b.MinSpeed) * Kmh,
            ExitSpeedDiff = (a.ExitSpeed - b.ExitSpeed) * Kmh,
            ThrottleDiff = a.ThrottlePoint - b.ThrottlePoint,
            CoastDiff = a.CoastLen - b.CoastLen,
            TrailDiff = a.TrailBrakeLen - b.TrailBrakeLen,
        };
        BuildAdvice(c, a, b);
        return c;
    }

    static void BuildAdvice(CornerComparison c, CornerMetrics a, CornerMetrics b)
    {
        var adv = c.Advice;
        string verdict = "";
        bool slower = c.TimeDelta > 0.02f;

        if (slower)
        {
            if (c.BrakeDiff < -8 && !(c.MinSpeedDiff < -3))
            { adv.Add($"Brake later — you braked {-c.BrakeDiff:0} m earlier than the reference."); verdict = $"braked {-c.BrakeDiff:0} m early"; }
            if (c.BrakeDiff > 5 && c.MinSpeedDiff < -3)
            { adv.Add($"You braked {c.BrakeDiff:0} m later but over-slowed (apex {c.MinSpeedDiff:0} km/h). Brake a touch earlier and release smoother to carry speed."); verdict = Pick(verdict, "overdriving entry"); }
            if (c.MinSpeedDiff < -3)
            {
                string why = c.TrailDiff < -10 ? " Trail the brake deeper into the corner to keep the front loaded." : "";
                adv.Add($"Carry more minimum speed: {-c.MinSpeedDiff:0} km/h slower at the apex.{why}");
                verdict = Pick(verdict, $"apex {c.MinSpeedDiff:0} km/h");
            }
            if (c.ThrottleDiff > 10)
            { adv.Add($"Get back to throttle earlier — {c.ThrottleDiff:0} m later than the reference."); verdict = Pick(verdict, $"throttle {c.ThrottleDiff:0} m late"); }
            if (c.ExitSpeedDiff < -3)
            { adv.Add($"Exit speed down {-c.ExitSpeedDiff:0} km/h — that costs time all the way down the next straight. Prioritise a straighter exit."); verdict = Pick(verdict, $"exit {c.ExitSpeedDiff:0} km/h"); }
            if (c.CoastDiff > 12)
            { adv.Add($"Coasting {a.CoastLen:0} m with no pedal — go more directly from brake to throttle."); verdict = Pick(verdict, "coasting"); }
            if (a.MaxBrake < b.MaxBrake - 0.12f && float.IsFinite(a.BrakePoint))
                adv.Add($"Hit the brake harder initially: peak {a.MaxBrake * 100:0}% vs {b.MaxBrake * 100:0}%.");
            if (float.IsFinite(a.LiftPoint) && float.IsNaN(b.LiftPoint) && float.IsNaN(b.BrakePoint))
            { adv.Add("The reference takes this flat — try lifting less or not at all."); verdict = Pick(verdict, "lifted"); }
            if (float.IsFinite(b.BrakePoint) && float.IsNaN(a.BrakePoint) && float.IsFinite(a.LiftPoint))
                adv.Add("The reference brakes here rather than just lifting — a short, firm stab may rotate the car better.");
            if (adv.Count == 0)
            { adv.Add($"Lost {c.TimeDelta:0.00}s with similar inputs — compare your line on the map for this corner."); verdict = "check your line"; }
            if (adv.Count > 3) adv.RemoveRange(3, adv.Count - 3); // most important first; keep it digestible
        }
        else if (c.TimeDelta < -0.03f)
        {
            if (c.MinSpeedDiff > 2) adv.Add($"Gained here with {c.MinSpeedDiff:0} km/h more apex speed.");
            if (c.BrakeDiff > 5) adv.Add($"Gained by braking {c.BrakeDiff:0} m later.");
            if (c.ExitSpeedDiff > 2) adv.Add($"Better exit: +{c.ExitSpeedDiff:0} km/h.");
            verdict = "gained";
        }
        if (a.LockupFront) adv.Add("Front lock-up under braking — ease peak pressure or move brake bias rearward.");
        if (a.LockupRear) adv.Add("Rear lock-up — move brake bias forward or downshift more gently.");
        if (a.Wheelspin) adv.Add("Wheelspin on exit — feed the throttle in more progressively (or raise TC).");
        c.Verdict = verdict;
    }

    static string Pick(string current, string next) => string.IsNullOrEmpty(current) ? next : current;
}
