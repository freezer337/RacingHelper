using RacingHelper.Recording;

namespace RacingHelper.Analysis;

public sealed class HandlingCell
{
    public string Phase { get; set; } = "";        // entry / mid / exit
    public string SpeedBand { get; set; } = "";    // slow / medium / fast
    public int Samples { get; set; }               // near-limit samples in this cell
    public float UndersteerRate { get; set; }      // % of near-limit time with front saturation
    public float OversteerRate { get; set; }       // % of near-limit time with rear rotation the driver is catching
    public int Countersteer { get; set; }          // opposite-lock moments
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
/// Detects handling problems from driver/car behaviour at the limit rather than absolute ratios:
///  • understeer = the driver keeps adding lock but the yaw rate does not follow (front axle saturated)
///  • oversteer  = the car keeps rotating while the driver unwinds or applies opposite lock (rear sliding)
/// Steering is converted to "requested yaw rate" using a constant calibrated from gentle cornering,
/// which makes the thresholds independent of each car's steering ratio.
/// </summary>
public static class HandlingAnalyzer
{
    static readonly string[] Phases = { "entry", "mid", "exit" };
    static readonly string[] Bands = { "slow", "medium", "fast" };

    public static HandlingReport Analyze(IEnumerable<LapData> laps, TrackModel? model, float slowKmh = 110, float fastKmh = 170)
    {
        var rep = new HandlingReport();
        var lapList = laps.Where(l => l.Count > 100).ToList();
        if (lapList.Count == 0) return rep;

        float K = Calibrate(lapList);
        if (float.IsNaN(K)) return rep;
        rep.SteerRatioK = K;

        var n = new int[3, 3]; var us = new int[3, 3]; var os = new int[3, 3]; var cs = new int[3, 3];
        var spots = new Dictionary<(string corner, string phase, string kind), int>();

        foreach (var l in lapList)
        {
            var v = l[Ch.Speed]; var st = l[Ch.Steer]; var r = l[Ch.YawRate]; var ay = l[Ch.LatG];
            var thr = l[Ch.Throttle]; var brk = l[Ch.Brake]; var d = l[Ch.D]; var t = l[Ch.T];
            float maxAy = 0;
            for (int i = 0; i < l.Count; i++) if (v[i] > 15) maxAy = Math.Max(maxAy, Math.Abs(ay[i]));
            float limitAy = maxAy * 0.6f;
            int lag = 12; // ≈0.2 s at 60 Hz
            bool inCs = false; int usRun = 0, osRun = 0;
            for (int i = lag; i < l.Count; i++)
            {
                if (v[i] < 15 || Math.Abs(ay[i]) < limitAy) { usRun = osRun = 0; inCs = false; continue; }
                int phase = brk[i] > 0.05f ? 0 : thr[i] >= 0.3f ? 2 : 1;
                float kmh = v[i] * 3.6f;
                int band = kmh < slowKmh ? 0 : kmh < fastKmh ? 1 : 2;
                n[phase, band]++;

                float turn = Math.Sign(r[i]);                    // direction the car is rotating
                float reqYaw = v[i] * st[i] / K;                 // yaw rate the steering asks for
                float reqYawPrev = v[i - lag] * st[i - lag] / K;
                float dReq = (reqYaw - reqYawPrev) * turn;       // + = more lock in the turning direction
                float dYaw = (r[i] - r[i - lag]) * turn;         // + = rotating faster

                bool counter = reqYaw * turn < -0.02f && Math.Abs(r[i]) > 0.2f;
                bool isUs = !counter && dReq > 0.06f && dYaw < 0.01f && reqYaw * turn > Math.Abs(r[i]) * 1.1f;
                bool isOs = counter || (dYaw > 0.05f && dReq < -0.03f);

                usRun = isUs ? usRun + 1 : 0;
                osRun = isOs ? osRun + 1 : 0;
                if (usRun >= 4) us[phase, band]++;
                if (osRun >= 4) os[phase, band]++;
                bool newCounter = counter && !inCs;
                if (newCounter) { cs[phase, band]++; rep.CountersteerEvents++; }
                inCs = counter;

                if (usRun == 6 || (osRun == 6 && !counter) || newCounter)
                {
                    string corner = model?.CornerAt(d[i])?.Name ?? $"{d[i]:0} m";
                    var key = (corner, Phases[phase], usRun == 6 ? "understeer" : "oversteer");
                    spots[key] = spots.GetValueOrDefault(key) + 1;
                }
            }
        }

        int total = 0;
        foreach (var x in n) total += x;
        if (total < 300) return rep;
        rep.Valid = true;

        for (int p = 0; p < 3; p++)
            for (int b = 0; b < 3; b++)
            {
                var cell = new HandlingCell { Phase = Phases[p], SpeedBand = Bands[b], Samples = n[p, b], Countersteer = cs[p, b] };
                if (n[p, b] >= 60)
                {
                    cell.UndersteerRate = 100f * us[p, b] / n[p, b];
                    cell.OversteerRate = 100f * os[p, b] / n[p, b];
                    if (cell.UndersteerRate > 6 && cell.UndersteerRate > cell.OversteerRate * 1.5f) cell.Tendency = "understeer";
                    else if (cell.OversteerRate > 4 && cell.OversteerRate > cell.UndersteerRate * 1.5f) cell.Tendency = "oversteer";
                }
                if (cell.Countersteer >= Math.Max(3, lapList.Count / 2)) cell.Tendency = "oversteer";
                rep.Cells.Add(cell);
            }

        rep.HotSpots = spots.OrderByDescending(kv => kv.Value).Take(6)
            .Select(kv => new HotSpot { Corner = kv.Key.corner, Phase = kv.Key.phase, Kind = kv.Key.kind, Count = kv.Value }).ToList();

        foreach (var c in rep.Cells.Where(c => c.Tendency != "neutral").OrderByDescending(c => Math.Max(c.UndersteerRate, c.OversteerRate)))
        {
            float rate = c.Tendency == "understeer" ? c.UndersteerRate : c.OversteerRate;
            string where = string.Join(", ", rep.HotSpots.Where(h => h.Kind == c.Tendency && h.Phase == c.Phase).Select(h => h.Corner).Distinct().Take(3));
            rep.Findings.Add($"{Cap(c.Tendency)} on {c.Phase} in {c.SpeedBand} corners ({rate:0}% of time at the limit{(c.Countersteer > 0 ? $", {c.Countersteer} opposite-lock moments" : "")}){(where.Length > 0 ? $" — mostly {where}" : "")}.");
        }
        if (rep.Findings.Count == 0) rep.Findings.Add("Balance looks neutral — no consistent understeer or oversteer pattern at the limit.");
        return rep;
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
