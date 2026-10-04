using System.Text.Json;
using RacingHelper.Storage;

namespace RacingHelper.Analysis;

public sealed class Corner
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public int Dir { get; set; }              // +1 left, -1 right
    public float Start { get; set; }          // apex region start (m)
    public float Apex { get; set; }           // min-speed point (m)
    public float End { get; set; }            // apex region end (m)
    public float SegStart { get; set; }       // analysis segment start (top speed before corner)
    public float SegEnd { get; set; }         // analysis segment end (top speed after corner)
    public float RefMinSpeed { get; set; }    // m/s on the model's source lap
    public bool Braking { get; set; }         // source lap braked for this corner
}

/// <summary>Map + corner layout of a track, built from a clean lap and reused for every lap at that track.</summary>
public sealed class TrackModel
{
    public string TrackKey { get; set; } = "";
    public float Length { get; set; }
    public float Step { get; set; } = 2f;
    public float[] X { get; set; } = Array.Empty<float>();
    public float[] Y { get; set; } = Array.Empty<float>();
    public List<Corner> Corners { get; set; } = new();
    public float[] SectorStarts { get; set; } = { 0f };
    public bool FromGps { get; set; }
    public long SourceLapId { get; set; }
    public double SourceLapTime { get; set; }
    public int Version { get; set; } = 1;

    public string ToJson() => JsonSerializer.Serialize(this, Database.Json);
    public static TrackModel? FromJson(string? json) => string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<TrackModel>(json, Database.Json);

    public (float x, float y) PosAt(float dist)
    {
        if (X.Length == 0) return (0, 0);
        dist = ((dist % Length) + Length) % Length;
        float f = dist / Step;
        int i = (int)f;
        if (i >= X.Length - 1) return (X[^1], Y[^1]);
        float a = f - i;
        return (X[i] + a * (X[i + 1] - X[i]), Y[i] + a * (Y[i + 1] - Y[i]));
    }

    public Corner? CornerAt(float dist) => Corners.FirstOrDefault(c => dist >= c.SegStart && dist < c.SegEnd);

    /// <summary>First corner whose apex lies ahead of <paramref name="dist"/> (wrapping to the next lap).</summary>
    public Corner? NextCorner(float dist)
    {
        foreach (var c in Corners) if (c.Apex > dist) return c;
        return Corners.FirstOrDefault();
    }

    public int SectorAt(float dist)
    {
        float pct = dist / Length;
        int s = 0;
        for (int i = 0; i < SectorStarts.Length; i++) if (pct >= SectorStarts[i]) s = i;
        return s;
    }

    /// <summary>Builds a model from a complete, clean lap.</summary>
    public static TrackModel Build(string trackKey, DistLap lap, float[] sectorStarts, bool fromGps, long sourceLapId)
    {
        var m = new TrackModel
        {
            TrackKey = trackKey,
            Length = lap.Length,
            SectorStarts = sectorStarts,
            FromGps = fromGps,
            SourceLapId = sourceLapId,
            SourceLapTime = lap.LapTime,
        };

        // --- path, decimated to Step, loop-closed when it came from dead-reckoning ---
        int stride = Math.Max(1, (int)Math.Round(m.Step / lap.Step));
        var xs = new List<float>(); var ys = new List<float>();
        for (int i = 0; i < lap.N; i += stride) { xs.Add(lap.X[i]); ys.Add(lap.Y[i]); }
        FillNaN(xs); FillNaN(ys);
        if (!fromGps && xs.Count > 2)
        {
            float ex = xs[^1] - xs[0], ey = ys[^1] - ys[0];
            for (int i = 0; i < xs.Count; i++)
            {
                float a = (float)i / (xs.Count - 1);
                xs[i] -= ex * a; ys[i] -= ey * a;
            }
        }
        m.X = xs.ToArray(); m.Y = ys.ToArray();
        m.Corners = DetectCorners(lap);
        return m;
    }

    static void FillNaN(List<float> v)
    {
        float last = v.FirstOrDefault(float.IsFinite);
        for (int i = 0; i < v.Count; i++) { if (float.IsFinite(v[i])) last = v[i]; else v[i] = last; }
    }

    /// <summary>
    /// Corners = regions of significant curvature (yaw rate / speed), apex = min speed in region,
    /// analysis segments split at the top-speed point between consecutive corners so they tile the lap.
    /// </summary>
    public static List<Corner> DetectCorners(DistLap lap)
    {
        int n = lap.N;
        var k = new float[n];
        for (int i = 0; i < n; i++)
        {
            float v = lap.Speed[i];
            k[i] = float.IsFinite(v) && v > 5 && float.IsFinite(lap.YawRate[i]) ? lap.YawRate[i] / v : 0;
        }
        var ks = Smooth(k, (int)(20 / lap.Step));
        var spd = Smooth(lap.Speed.Select(x => float.IsFinite(x) ? x : 0).ToArray(), (int)(10 / lap.Step));

        const float threshold = 0.0045f;  // ≈ 220 m radius
        var regions = new List<(int s, int e, int dir)>();
        int start = -1, dir = 0;
        for (int i = 0; i < n; i++)
        {
            float a = Math.Abs(ks[i]);
            int sd = Math.Sign(ks[i]);
            if (a > threshold && (start < 0 || sd == dir)) { if (start < 0) { start = i; dir = sd; } }
            else if (start >= 0) { regions.Add((start, i - 1, dir)); start = a > threshold ? i : -1; dir = sd; }
        }
        if (start >= 0) regions.Add((start, n - 1, dir));

        // merge same-direction regions separated by short gaps; drop tiny ones
        var merged = new List<(int s, int e, int dir)>();
        foreach (var r in regions)
        {
            if (merged.Count > 0 && merged[^1].dir == r.dir && (r.s - merged[^1].e) * lap.Step < 35)
                merged[^1] = (merged[^1].s, r.e, r.dir);
            else merged.Add(r);
        }
        // drop gentle full-speed kinks and short blips (e.g. a correction on a hairpin exit):
        // a corner needs real length, and either strong curvature or a real speed drop since the previous corner
        var corners = new List<Corner>();
        int prevEnd = 0;
        foreach (var r in merged)
        {
            float maxK = 0; int apex = r.s; float minV = float.MaxValue;
            for (int i = r.s; i <= r.e; i++)
            {
                maxK = Math.Max(maxK, Math.Abs(ks[i]));
                if (spd[i] < minV) { minV = spd[i]; apex = i; }
            }
            float lenM = (r.e - r.s) * lap.Step;
            if (lenM < 20 && !(lenM >= 10 && maxK > 0.02f)) continue;
            float before = 0;
            for (int i = Math.Max(prevEnd, r.s - (int)(400 / lap.Step)); i <= apex; i++) before = Math.Max(before, spd[i]);
            bool speedDrop = before - minV > 15 / 3.6f;
            if (maxK < 0.008f && !speedDrop) continue;
            prevEnd = r.e;
            // apex at a region edge means the car was still slowing → use curvature peak instead
            if (apex == r.s || apex == r.e)
            {
                float best = 0;
                for (int i = r.s; i <= r.e; i++) if (Math.Abs(ks[i]) > best) { best = Math.Abs(ks[i]); apex = i; }
            }
            bool braking = false;
            for (int i = Math.Max(0, r.s - (int)(300 / lap.Step)); i <= apex; i++) if (lap.Brake[i] > 0.1f) { braking = true; break; }
            corners.Add(new Corner
            {
                Dir = r.dir, Start = r.s * lap.Step, End = r.e * lap.Step, Apex = apex * lap.Step,
                RefMinSpeed = minV, Braking = braking,
            });
        }

        // tile the lap into segments at the top-speed point between corners
        for (int c = 0; c < corners.Count; c++)
        {
            corners[c].Index = c + 1;
            corners[c].Name = $"T{c + 1}";
            corners[c].SegStart = c == 0 ? 0 : corners[c - 1].SegEnd;
            if (c == corners.Count - 1) { corners[c].SegEnd = lap.Length; break; }
            int a0 = lap.Index(corners[c].End), a1 = lap.Index(corners[c + 1].Start);
            if (a1 <= a0) { corners[c].SegEnd = (corners[c].End + corners[c + 1].Start) / 2; continue; }
            int best = a0; float bv = -1;
            for (int i = a0; i <= a1; i++) if (spd[i] > bv) { bv = spd[i]; best = i; }
            corners[c].SegEnd = best * lap.Step;
        }
        return corners;
    }

    public static float[] Smooth(float[] v, int window)
    {
        if (window <= 1) return (float[])v.Clone();
        int n = v.Length, h = window / 2;
        var res = new float[n];
        double sum = 0; int cnt = 0;
        int lo = 0, hi = -1;
        for (int i = 0; i < n; i++)
        {
            int want = Math.Min(n - 1, i + h);
            while (hi < want) { hi++; if (float.IsFinite(v[hi])) { sum += v[hi]; cnt++; } }
            int wantLo = Math.Max(0, i - h);
            while (lo < wantLo) { if (float.IsFinite(v[lo])) { sum -= v[lo]; cnt--; } lo++; }
            res[i] = cnt > 0 ? (float)(sum / cnt) : 0;
        }
        return res;
    }
}
