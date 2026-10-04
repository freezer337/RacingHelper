using RacingHelper.Recording;

namespace RacingHelper.Analysis;

public sealed class HandlingCell
{
    public string Phase { get; set; } = "";        // entry / mid / exit
    public string SpeedBand { get; set; } = "";    // slow / medium / fast
    public int Samples { get; set; }               // near-limit samples in this cell
    public float UndersteerRate { get; set; }      // % of near-limit time needing 25 %+ more steering than normal
    public float OversteerRate { get; set; }       // % of near-limit time rotating 20 %+ more than asked, or opposite lock
    public int Countersteer { get; set; }          // opposite-lock moments
    public float Balance { get; set; } = float.NaN; // % steering needed at the limit vs moderate cornering: + understeer, − oversteer
    public string Tendency { get; set; } = "neutral";
}

public sealed class HandlingReport
{
    public List<HandlingCell> Cells { get; set; } = new();
    public float SteerRatioK { get; set; }
    public int CountersteerEvents { get; set; }
    public List<HotSpot> HotSpots { get; set; } = new();
    public List<string> Findings { get; set; } = new();
    public bool Valid { get; set; }
}

public sealed class HotSpot
{
    public string Corner { get; set; } = "";
    public string Phase { get; set; } = "";
    public string Kind { get; set; } = "";   // understeer / oversteer
    public int Count { get; set; }
}

/// <summary>
/// Detects handling problems from how the car responds to the steering at the limit.
///  • "Steer ratio" = the yaw rate the steering asks for (v·δ/K, K calibrated from gentle cornering) ÷ the yaw rate the
///    car actually has. More than 1: it needs more lock than it rotates (understeer). Less than 1: it rotates more than
///    the steering asks, or you're catching it with opposite lock (oversteer).
///  • Every car's ratio drifts with speed even well below the limit (the linear understeer gradient goes with v²), and
///    every car rotates more under braking. So the ratio at the limit is compared with the same car at moderate
///    cornering, in the same speed range and phase: only what changes when the tyres saturate counts.
///  • "At the limit" = within 70 % of the grip the car shows at that speed (95th percentile of lateral g per speed
///    band), so downforce cars get their slow corners analysed too and one kerb spike doesn't hide everything.
/// </summary>
public static class HandlingAnalyzer
{
    static readonly string[] Phases = { "entry", "mid", "exit" };
    static readonly string[] Bands = { "slow", "medium", "fast" };
    static readonly float[] FineEdges = { 80, 110, 140, 170, 200, 240 };   // km/h, for the moderate-cornering baseline

    const float LimitShare = 0.7f;           // near the limit: ≥ 70 % of the grip at this speed
    const float ModLo = 0.3f, ModHi = 0.55f; // moderate cornering (the baseline)
    const float UsSample = 1.25f, OsSample = 0.8f;
    public const float TendencyPct = 10;     // balance beyond ±10 % → understeer / oversteer

    public static HandlingReport Analyze(IEnumerable<LapData> laps, TrackModel? model, float slowKmh = 110, float fastKmh = 170)
    {
        var rep = new HandlingReport();
        var lapList = laps.Where(l => l.Count > 100).ToList();
        if (lapList.Count == 0) return rep;

        float K = Calibrate(lapList);
        if (float.IsNaN(K)) return rep;
        rep.SteerRatioK = K;

        // grip envelope: 95th percentile of |lateral g| per 10 km/h (window 30 km/h), spikes from kerbs don't count
        var env = Envelope(lapList);
        if (env == null) return rep;

        // pass 1: moderate cornering baseline per (phase, fine speed bin) and per fine speed bin
        int nf = FineEdges.Length + 1;
        var basePhase = new List<float>[3, nf]; var baseAll = new List<float>[nf];
        for (int k = 0; k < nf; k++) { baseAll[k] = new(); for (int p = 0; p < 3; p++) basePhase[p, k] = new(); }
        foreach (var l in lapList)
        {
            var v = l[Ch.Speed]; var st = l[Ch.Steer]; var r = l[Ch.YawRate]; var ay = l[Ch.LatG]; var thr = l[Ch.Throttle]; var brk = l[Ch.Brake];
            for (int i = 0; i < l.Count; i++)
            {
                if (!(v[i] > 15)) continue;
                float a = Math.Abs(ay[i]), e = Env(env, v[i] * 3.6f);
                if (a < ModLo * e || a >= ModHi * e) continue;
                float ratio = Ratio(v[i], st[i], r[i], K);
                if (!float.IsFinite(ratio)) continue;
                int k = Fine(v[i] * 3.6f);
                baseAll[k].Add(ratio); basePhase[PhaseOf(brk[i], thr[i]), k].Add(ratio);
            }
        }
        var bAll = new float[nf];
        for (int k = 0; k < nf; k++) bAll[k] = baseAll[k].Count >= 40 ? Median(baseAll[k]) : float.NaN;
        FillGaps(bAll);
        var bPh = new float[3, nf];
        for (int p = 0; p < 3; p++)
            for (int k = 0; k < nf; k++) bPh[p, k] = basePhase[p, k].Count >= 40 ? Median(basePhase[p, k]) : bAll[k];

        // pass 2: at the limit
        var rel = new List<float>[3, 3];
        for (int p = 0; p < 3; p++) for (int b = 0; b < 3; b++) rel[p, b] = new();
        var us = new int[3, 3]; var os = new int[3, 3]; var cs = new int[3, 3];
        var spots = new Dictionary<(string corner, string phase, string kind), int>();

        foreach (var l in lapList)
        {
            var v = l[Ch.Speed]; var st = l[Ch.Steer]; var r = l[Ch.YawRate]; var ay = l[Ch.LatG];
            var thr = l[Ch.Throttle]; var brk = l[Ch.Brake]; var d = l[Ch.D]; var t = l[Ch.T];
            int minRun = Math.Max(3, (int)Math.Round(0.25 / SampleDt(t)));   // ≈ 0.25 s whatever the sample rate
            bool inCs = false; int usRun = 0, osRun = 0;
            var seen = new HashSet<(string, string, string)>();               // one hot-spot count per corner per lap
            for (int i = 0; i < l.Count; i++)
            {
                if (!(v[i] > 15) || Math.Abs(ay[i]) < LimitShare * Env(env, v[i] * 3.6f)) { usRun = osRun = 0; inCs = false; continue; }
                int phase = PhaseOf(brk[i], thr[i]);
                float kmh = v[i] * 3.6f;
                int band = kmh < slowKmh ? 0 : kmh < fastKmh ? 1 : 2;

                float turn = Math.Sign(r[i]);
                bool counter = v[i] * st[i] / K * turn < -0.02f && Math.Abs(r[i]) > 0.2f;   // opposite lock while rotating
                float ratio = Ratio(v[i], st[i], r[i], K);
                float x = float.IsFinite(ratio) ? ratio / bPh[phase, Fine(kmh)] : float.NaN;
                if (float.IsFinite(x)) rel[phase, band].Add(counter ? Math.Min(x, 0) : x);
                else if (counter) rel[phase, band].Add(0);

                bool isUs = !counter && x > UsSample;
                bool isOs = counter || x < OsSample;
                if (isUs) us[phase, band]++;
                if (isOs) os[phase, band]++;
                usRun = isUs ? usRun + 1 : 0;
                osRun = isOs ? osRun + 1 : 0;
                bool newCounter = counter && !inCs;
                if (newCounter) { cs[phase, band]++; rep.CountersteerEvents++; }
                inCs = counter;

                if (usRun == minRun || osRun == minRun || newCounter)
                {
                    string corner = model?.CornerAt(d[i])?.Name ?? $"{d[i]:0} m";
                    var key = (corner, Phases[phase], usRun == minRun ? "understeer" : "oversteer");
                    if (seen.Add(key)) spots[key] = spots.GetValueOrDefault(key) + 1;
                }
            }
        }

        int total = 0;
        foreach (var x in rel) total += x.Count;
        if (total < 300) return rep;
        rep.Valid = true;

        for (int p = 0; p < 3; p++)
            for (int b = 0; b < 3; b++)
            {
                int n = rel[p, b].Count;
                var cell = new HandlingCell { Phase = Phases[p], SpeedBand = Bands[b], Samples = n, Countersteer = cs[p, b] };
                if (n >= 60)
                {
                    cell.UndersteerRate = 100f * us[p, b] / n;
                    cell.OversteerRate = 100f * os[p, b] / n;
                    cell.Balance = (Median(rel[p, b]) - 1) * 100;
                    if (cell.Balance >= TendencyPct && cell.UndersteerRate > cell.OversteerRate) cell.Tendency = "understeer";
                    else if (cell.Balance <= -TendencyPct && cell.OversteerRate > cell.UndersteerRate) cell.Tendency = "oversteer";
                }
                else cell.Balance = float.NaN;
                if (cell.Countersteer >= Math.Max(3, lapList.Count / 2)) cell.Tendency = "oversteer";
                rep.Cells.Add(cell);
            }

        // a hot spot is a corner where it happened on at least two laps (or every lap of a short run)
        int need = Math.Min(2, lapList.Count);
        rep.HotSpots = spots.Where(kv => kv.Value >= need).OrderByDescending(kv => kv.Value).Take(6)
            .Select(kv => new HotSpot { Corner = kv.Key.corner, Phase = kv.Key.phase, Kind = kv.Key.kind, Count = kv.Value }).ToList();

        foreach (var c in rep.Cells.Where(c => c.Tendency != "neutral").OrderByDescending(c => float.IsFinite(c.Balance) ? Math.Abs(c.Balance) : 0))
        {
            string where = string.Join(", ", rep.HotSpots.Where(h => h.Kind == c.Tendency && h.Phase == c.Phase).Select(h => h.Corner).Distinct().Take(3));
            string how = float.IsFinite(c.Balance)
                ? (c.Tendency == "understeer" ? $"needs {c.Balance:0}% more steering at the limit than normal" : $"rotates {-c.Balance:0}% more than the steering asks at the limit")
                : "";
            string cnt = c.Countersteer > 0 ? $"{(how.Length > 0 ? ", " : "")}{c.Countersteer} opposite-lock moments" : "";
            rep.Findings.Add($"{Cap(c.Tendency)} on {c.Phase} in {c.SpeedBand} corners ({how}{cnt}){(where.Length > 0 ? $" — mostly {where}" : "")}.");
        }
        if (rep.Findings.Count == 0) rep.Findings.Add("Balance looks neutral — no consistent understeer or oversteer pattern at the limit.");
        return rep;
    }

    static int PhaseOf(float brake, float throttle) => brake > 0.05f ? 0 : throttle >= 0.3f ? 2 : 1;

    static float Ratio(float v, float steer, float yaw, float K)
    {
        if (Math.Abs(yaw) < 0.05f) return float.NaN;
        return v * steer / K * Math.Sign(yaw) / Math.Abs(yaw);
    }

    static int Fine(float kmh)
    {
        int k = 0;
        while (k < FineEdges.Length && kmh >= FineEdges[k]) k++;
        return k;
    }

    static double SampleDt(float[] t)
    {
        if (t.Length < 10) return 1 / 60.0;
        var dts = new List<float>();
        for (int i = 1; i < Math.Min(t.Length, 400); i++) if (t[i] > t[i - 1]) dts.Add(t[i] - t[i - 1]);
        return dts.Count > 0 ? Math.Clamp(Median(dts), 1 / 360f, 0.5f) : 1 / 60.0;
    }

    /// <summary>Grip at each speed: p95 of |lateral accel| per 10 km/h step, over a ±10–20 km/h window.</summary>
    static float[]? Envelope(List<LapData> laps)
    {
        var bins = new List<float>[40];
        for (int k = 0; k < bins.Length; k++) bins[k] = new();
        foreach (var l in laps)
        {
            var v = l[Ch.Speed]; var ay = l[Ch.LatG];
            for (int i = 0; i < l.Count; i++)
            {
                if (!(v[i] > 15) || !float.IsFinite(ay[i])) continue;
                int c = (int)(v[i] * 3.6f / 10);
                for (int k = c - 1; k <= c + 1; k++) if (k >= 0 && k < bins.Length) bins[k].Add(Math.Abs(ay[i]));
            }
        }
        var env = new float[bins.Length];
        int good = 0;
        for (int k = 0; k < bins.Length; k++)
        {
            if (bins[k].Count < 100) { env[k] = float.NaN; continue; }
            bins[k].Sort();
            env[k] = bins[k][(int)(bins[k].Count * 0.95)];
            good++;
        }
        if (good == 0) return null;
        FillGaps(env);
        return env;
    }

    static float Env(float[] env, float kmh)
    {
        float x = Math.Clamp(kmh / 10 - 0.5f, 0, env.Length - 1);
        int a = (int)x, b = Math.Min(a + 1, env.Length - 1);
        return env[a] + (env[b] - env[a]) * (x - a);
    }

    /// <summary>Missing entries take the nearest known value (linear in between).</summary>
    static void FillGaps(float[] a)
    {
        int first = Array.FindIndex(a, float.IsFinite);
        if (first < 0) { Array.Fill(a, 1f); return; }
        for (int i = 0; i < first; i++) a[i] = a[first];
        int last = first;
        for (int i = first + 1; i < a.Length; i++)
        {
            if (!float.IsFinite(a[i])) continue;
            for (int j = last + 1; j < i; j++) a[j] = a[last] + (a[i] - a[last]) * (j - last) / (i - last);
            last = i;
        }
        for (int i = last + 1; i < a.Length; i++) a[i] = a[last];
    }

    static float Median(List<float> xs)
    {
        var s = xs.Where(float.IsFinite).OrderBy(x => x).ToList();
        return s.Count == 0 ? float.NaN : s[s.Count / 2];
    }

    /// <summary>Steering→yaw constant from steady, gentle cornering (v·δ/r ≈ wheelbase × steering ratio).</summary>
    static float Calibrate(List<LapData> laps)
    {
        var ks = new List<float>();
        foreach (var l in laps)
        {
            var v = l[Ch.Speed]; var st = l[Ch.Steer]; var r = l[Ch.YawRate]; var ay = l[Ch.LatG];
            for (int i = 0; i < l.Count; i += 2)
            {
                if (v[i] < 15 || Math.Abs(ay[i]) > 4 || Math.Abs(r[i]) < 0.04f || Math.Abs(st[i]) < 0.015f) continue;
                ks.Add(v[i] * st[i] / r[i]);
            }
        }
        if (ks.Count < 200) return float.NaN;
        ks.Sort();
        float k = ks[ks.Count / 2];
        return Math.Abs(k) < 1e-3 ? float.NaN : k;
    }

    static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
