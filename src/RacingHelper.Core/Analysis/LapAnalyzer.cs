namespace RacingHelper.Analysis;

public sealed class CornerMetrics
{
    public int Corner { get; set; }
    public string Name { get; set; } = "";
    public float SegTime { get; set; } = float.NaN;
    public float BrakePoint { get; set; } = float.NaN;    // m from line
    public float LiftPoint { get; set; } = float.NaN;
    public float EntrySpeed { get; set; } = float.NaN;    // m/s
    public float MinSpeed { get; set; } = float.NaN;
    public float ApexDist { get; set; } = float.NaN;
    public float BrakeRelease { get; set; } = float.NaN;
    public float ThrottlePoint { get; set; } = float.NaN;
    public float FullThrottlePoint { get; set; } = float.NaN;
    public float ExitSpeed { get; set; } = float.NaN;
    public float MaxBrake { get; set; }
    public float TrailBrakeLen { get; set; }
    public float CoastLen { get; set; }
    public int ApexGear { get; set; }
    public bool LockupFront { get; set; }
    public bool LockupRear { get; set; }
    public bool Wheelspin { get; set; }
    public bool AbsUsed { get; set; }
}

public sealed class LapStyle
{
    public float FullThrottlePct { get; set; }
    public float BrakingPct { get; set; }
    public float CoastingPct { get; set; }
    public float OverlapPct { get; set; }
    public float TrailBrakePct { get; set; }      // share of braking distance done while turning
    public float SteeringRate { get; set; }       // mean |d steer / dt| (rad/s) — smoothness
    public float ThrottleRate { get; set; }       // mean |d thr / dt| while partially open
    public float UpshiftRpm { get; set; }
    public int Upshifts { get; set; }
    public int Lockups { get; set; }
    public int Wheelspins { get; set; }
    public float AbsPct { get; set; }
    public float AvgPeakBrake { get; set; }
    public float MaxLatG { get; set; }
    public float TopSpeed { get; set; }
}

public sealed class LapEvent
{
    public string Type { get; set; } = "";   // lockup-front, lockup-rear, wheelspin, offtrack
    public float Dist { get; set; }
    public string Corner { get; set; } = "";
}

public sealed class LapAnalysis
{
    public List<CornerMetrics> Corners { get; set; } = new();
    public LapStyle Style { get; set; } = new();
    public List<LapEvent> Events { get; set; } = new();
}

public static class LapAnalyzer
{
    public static LapAnalysis Analyze(DistLap lap, TrackModel model, float shiftRpm = 0)
    {
        var res = new LapAnalysis();
        foreach (var c in model.Corners) res.Corners.Add(AnalyzeCorner(lap, c, res.Events));
        res.Style = Style(lap, shiftRpm, res.Corners);
        return res;
    }

    public static CornerMetrics AnalyzeCorner(DistLap lap, Corner c, List<LapEvent> events)
    {
        var m = new CornerMetrics { Corner = c.Index, Name = c.Name, SegTime = lap.TimeBetween(c.SegStart, Math.Min(c.SegEnd, lap.Length)) };
        int s0 = lap.Index(c.SegStart), s1 = Math.Min(lap.Index(c.SegEnd), lap.N - 1);
        int apexRegion0 = lap.Index(c.Start), apexRegion1 = lap.Index(c.End);
        if (!float.IsFinite(lap.Speed[Math.Min(apexRegion0, lap.N - 1)])) return m;

        // braking point: first sustained brake application before the apex region ends
        int brake = -1;
        for (int i = s0; i <= apexRegion1 && i < lap.N; i++)
        {
            if (lap.Brake[i] > 0.1f && Sustained(lap.Brake, i, 5, x => x > 0.05f)) { brake = i; break; }
        }
        int entry = brake;
        if (brake >= 0) m.BrakePoint = brake * lap.Step;
        else
        {
            for (int i = s0; i <= apexRegion1 && i < lap.N; i++)
                if (lap.Throttle[i] < 0.85f && i > s0 && lap.Throttle[i - 1] >= 0.85f) { entry = i; m.LiftPoint = i * lap.Step; break; }
        }
        if (entry < 0) entry = apexRegion0;
        m.EntrySpeed = lap.Speed[entry];

        int minI = entry; float minV = float.MaxValue;
        int searchEnd = Math.Min(lap.N - 1, apexRegion1 + (int)(30 / lap.Step));
        for (int i = entry; i <= searchEnd; i++) if (lap.Speed[i] < minV) { minV = lap.Speed[i]; minI = i; }
        m.MinSpeed = minV;
        m.ApexDist = minI * lap.Step;
        m.ApexGear = (int)lap.Gear[minI];

        int release = brake;
        if (brake >= 0)
        {
            float peak = 0; int steerOn = 0;
            float apexSteer = Math.Abs(lap.Steer[minI]);
            int offFor = (int)(8 / lap.Step);
            for (int i = brake; i <= searchEnd; i++)
            {
                if (lap.Brake[i] > 0.05f)
                {
                    release = i;
                    peak = Math.Max(peak, lap.Brake[i]);
                    if (Math.Abs(lap.Steer[i]) > Math.Max(0.05f, apexSteer * 0.25f)) steerOn++;
                }
                else if (i - release > offFor) break; // brakes off long enough → zone finished
            }
            m.MaxBrake = peak;
            m.TrailBrakeLen = steerOn * lap.Step;
            m.BrakeRelease = release * lap.Step;
        }

        int thrSearch = Math.Max(release, minI - (int)(40 / lap.Step));
        for (int i = Math.Max(thrSearch, entry); i <= s1; i++)
            if (lap.Throttle[i] > 0.25f && Sustained(lap.Throttle, i, 10, x => x > 0.2f)) { m.ThrottlePoint = i * lap.Step; thrSearch = i; break; }
        if (float.IsFinite(m.ThrottlePoint))
            for (int i = thrSearch; i <= s1; i++)
                if (lap.Throttle[i] > 0.95f) { m.FullThrottlePoint = i * lap.Step; break; }

        m.ExitSpeed = lap.Speed[Math.Min(s1, lap.Index(c.End + 60))];

        float coast = 0;
        for (int i = s0; i <= s1; i++)
            if (lap.Brake[i] < 0.03f && lap.Throttle[i] < 0.05f && lap.Speed[i] > 10) coast++;
        m.CoastLen = coast * lap.Step;

        // lockups / wheelspin / ABS from wheel speeds where available. Under hard braking the fronts normally run 5–15 %
        // slow (that's where peak braking grip is) and the inside front on a hairpin more, so only a wheel turning 30 %+
        // slower than the car for 6 m counts as locked. Wheelspin uses the average of the rears (the outside rear runs
        // faster in a corner anyway).
        int lf = 0, lr = 0, spin = 0;
        int lockRun = Math.Max(1, (int)Math.Ceiling(6 / lap.Step)), spinRun = Math.Max(1, (int)Math.Ceiling(6 / lap.Step));
        for (int i = s0; i <= s1; i++)
        {
            float v = lap.Speed[i];
            if (!(v > 12)) continue;
            if (lap.Abs[i] > 0.5f) m.AbsUsed = true;
            if (lap.Brake[i] > 0.15f)
            {
                lf = Min2(lap.WheelSpeed[0], lap.WheelSpeed[1], i) < v * 0.7f ? lf + 1 : 0;
                lr = Min2(lap.WheelSpeed[2], lap.WheelSpeed[3], i) < v * 0.7f ? lr + 1 : 0;
                if (lf >= lockRun && !m.LockupFront) { m.LockupFront = true; events.Add(new LapEvent { Type = "lockup-front", Dist = i * lap.Step, Corner = c.Name }); }
                if (lr >= lockRun && !m.LockupRear) { m.LockupRear = true; events.Add(new LapEvent { Type = "lockup-rear", Dist = i * lap.Step, Corner = c.Name }); }
            }
            else { lf = 0; lr = 0; }
            if (lap.Throttle[i] > 0.3f && lap.WheelSpeed[2] != null)
            {
                float rear = (lap.WheelSpeed[2]![i] + lap.WheelSpeed[3]![i]) / 2;
                spin = rear > v * 1.1f + 0.5f ? spin + 1 : 0;
                if (spin >= spinRun && !m.Wheelspin) { m.Wheelspin = true; events.Add(new LapEvent { Type = "wheelspin", Dist = i * lap.Step, Corner = c.Name }); }
            }
            else spin = 0;
        }
        return m;
    }

    static float Min2(float[]? a, float[]? b, int i)
    {
        if (a == null || b == null) return float.MaxValue;
        float x = a[i], y = b[i];
        return float.IsFinite(x) && float.IsFinite(y) ? Math.Min(x, y) : float.MaxValue;
    }

    static bool Sustained(float[] v, int i, int count, Func<float, bool> cond)
    {
        for (int k = i; k < Math.Min(v.Length, i + count); k++) if (!cond(v[k])) return false;
        return true;
    }

    static LapStyle Style(DistLap lap, float shiftRpm, List<CornerMetrics> corners)
    {
        var s = new LapStyle();
        double total = 0, full = 0, braking = 0, coast = 0, overlap = 0, absT = 0;
        double steerRate = 0, thrRate = 0; int thrRateN = 0;
        float rpmSum = 0; int ups = 0;
        for (int i = 1; i < lap.N; i++)
        {
            float dt = lap.Time[i] - lap.Time[i - 1];
            if (!(dt > 0) || dt > 1 || !float.IsFinite(lap.Speed[i])) continue;
            total += dt;
            float thr = lap.Throttle[i], brk = lap.Brake[i];
            if (thr > 0.98f) full += dt;
            if (brk > 0.05f) braking += dt;
            if (brk < 0.03f && thr < 0.05f && lap.Speed[i] > 10) coast += dt;
            if (brk > 0.1f && thr > 0.1f) overlap += dt;
            if (lap.Abs[i] > 0.5f) absT += dt;
            steerRate += Math.Abs(lap.Steer[i] - lap.Steer[i - 1]);
            if (thr > 0.05f && thr < 0.95f) { thrRate += Math.Abs(thr - lap.Throttle[i - 1]) / dt; thrRateN++; }
            if (lap.Gear[i] > lap.Gear[i - 1] && lap.Gear[i - 1] >= 1)
            {
                // RPM just before the shift
                int j = Math.Max(0, i - 3);
                float r = 0; for (int k = j; k < i; k++) r = Math.Max(r, lap.Rpm[k]);
                rpmSum += r; ups++;
            }
            s.MaxLatG = Math.Max(s.MaxLatG, Math.Abs(lap.LatG[i]) / 9.81f);
            s.TopSpeed = Math.Max(s.TopSpeed, lap.Speed[i]);
        }
        if (total <= 0) return s;
        s.FullThrottlePct = (float)(full / total * 100);
        s.BrakingPct = (float)(braking / total * 100);
        s.CoastingPct = (float)(coast / total * 100);
        s.OverlapPct = (float)(overlap / total * 100);
        s.AbsPct = (float)(absT / Math.Max(braking, 1e-6) * 100);
        s.SteeringRate = (float)(steerRate / total);
        s.ThrottleRate = thrRateN > 0 ? (float)(thrRate / thrRateN) : 0;
        s.Upshifts = ups;
        s.UpshiftRpm = ups > 0 ? rpmSum / ups : 0;
        var braked = corners.Where(c => float.IsFinite(c.BrakePoint)).ToList();
        float brakeDist = braked.Sum(c => Math.Max(0, c.BrakeRelease - c.BrakePoint));
        s.TrailBrakePct = brakeDist > 0 ? braked.Sum(c => c.TrailBrakeLen) / brakeDist * 100 : 0;
        s.AvgPeakBrake = braked.Count > 0 ? braked.Average(c => c.MaxBrake) * 100 : 0;
        s.Lockups = corners.Count(c => c.LockupFront || c.LockupRear);
        s.Wheelspins = corners.Count(c => c.Wheelspin);
        return s;
    }
}
