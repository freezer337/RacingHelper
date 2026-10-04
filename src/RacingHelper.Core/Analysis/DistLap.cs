using RacingHelper.Recording;

namespace RacingHelper.Analysis;

/// <summary>
/// A lap resampled onto a fixed distance grid (index i ↔ i*Step metres) so laps can be compared point by point.
/// Points not covered by the lap (e.g. before an out-lap started) are NaN.
/// </summary>
public sealed class DistLap
{
    public float Step { get; }
    public float Length { get; }
    public int N { get; }
    public double LapTime { get; }
    public float[] Time, Speed, Throttle, Brake, Steer, Gear, Rpm, LatG, LonG, YawRate, X, Y, Abs;
    public float[]?[] WheelSpeed = new float[]?[4];
    public bool Complete { get; }

    public DistLap(LapData data, float trackLength, double lapTime, bool complete, float step = 1f)
    {
        Step = step;
        Length = trackLength;
        LapTime = lapTime;
        Complete = complete;
        N = (int)Math.Ceiling(trackLength / step) + 1;

        var d = (float[])data[Ch.D].Clone();
        for (int i = 1; i < d.Length; i++) if (d[i] < d[i - 1]) d[i] = d[i - 1];

        Time = Resample(d, data[Ch.T], complete ? 0f : float.NaN, complete ? (float)lapTime : float.NaN);
        Speed = Resample(d, data[Ch.Speed]);
        Throttle = Resample(d, data[Ch.Throttle]);
        Brake = Resample(d, data[Ch.Brake]);
        Steer = Resample(d, data[Ch.Steer]);
        Gear = Resample(d, data[Ch.Gear], nearest: true);
        // sequential boxes report neutral for a frame mid-shift; treat that as the previous gear while moving
        for (int i = 1; i < N; i++)
            if (Gear[i] == 0 && Gear[i - 1] > 0 && Speed[i] > 5) Gear[i] = Gear[i - 1];
        Rpm = Resample(d, data[Ch.Rpm]);
        LatG = Resample(d, data[Ch.LatG]);
        LonG = Resample(d, data[Ch.LonG]);
        YawRate = Resample(d, data[Ch.YawRate]);
        X = Resample(d, data[Ch.X]);
        Y = Resample(d, data[Ch.Y]);
        Abs = data.Has(Ch.Abs) ? Resample(d, data[Ch.Abs], nearest: true) : new float[N];
        for (int w = 0; w < 4; w++)
            if (data.Get(Ch.WheelSpeed[w]) is { } ws && ws.Any(float.IsFinite)) WheelSpeed[w] = Resample(d, ws);
    }

    /// <summary>An empty (all-NaN) lap that the live engine fills in as the car drives.</summary>
    public DistLap(float trackLength, float step = 1f)
    {
        Step = step;
        Length = trackLength;
        N = (int)Math.Ceiling(trackLength / step) + 1;
        Time = Nan(); Speed = Nan(); Throttle = Nan(); Brake = Nan(); Steer = Nan(); Gear = Nan(); Rpm = Nan();
        LatG = Nan(); LonG = Nan(); YawRate = Nan(); X = Nan(); Y = Nan(); Abs = new float[N];
    }

    float[] Nan() { var a = new float[N]; Array.Fill(a, float.NaN); return a; }

    public static DistLap? TryCreate(LapData? data, float trackLength, double lapTime, bool complete, float step = 1f)
    {
        if (data == null || data.Count < 20 || trackLength <= 0) return null;
        return new DistLap(data, trackLength, lapTime, complete, step);
    }

    float[] Resample(float[] d, float[] v, float startValue = float.NaN, float endValue = float.NaN, bool nearest = false)
    {
        var res = new float[N];
        int j = 0, n = d.Length;
        for (int i = 0; i < N; i++)
        {
            float x = i * Step;
            if (x > Length) x = Length;
            while (j < n - 1 && d[j + 1] < x) j++;
            if (x < d[0])
            {
                // before the first sample: extrapolate to the start line for complete laps
                if (!float.IsNaN(startValue) && d[0] - x < 30) res[i] = startValue + (v[0] - startValue) * (d[0] > 0 ? x / d[0] : 1);
                else res[i] = d[0] - x < 3 ? v[0] : float.NaN;
                continue;
            }
            if (j >= n - 1 || x > d[n - 1])
            {
                if (!float.IsNaN(endValue) && x - d[n - 1] < 30)
                {
                    float span = Length - d[n - 1];
                    res[i] = span > 0 ? v[n - 1] + (endValue - v[n - 1]) * (x - d[n - 1]) / span : endValue;
                }
                else res[i] = x - d[n - 1] < 3 ? v[n - 1] : float.NaN;
                continue;
            }
            float d0 = d[j], d1 = d[j + 1];
            float a = d1 > d0 ? (x - d0) / (d1 - d0) : 0;
            res[i] = nearest ? (a < 0.5f ? v[j] : v[j + 1]) : v[j] + a * (v[j + 1] - v[j]);
        }
        return res;
    }

    public int Index(float dist) => Math.Clamp((int)Math.Round(dist / Step), 0, N - 1);

    public float At(float[] ch, float dist)
    {
        float x = Math.Clamp(dist / Step, 0, N - 1);
        int i = (int)x;
        if (i >= N - 1) return ch[N - 1];
        float a = x - i;
        return ch[i] + a * (ch[i + 1] - ch[i]);
    }

    /// <summary>Time spent between two distances (NaN if not covered).</summary>
    public float TimeBetween(float d0, float d1) => At(Time, d1) - At(Time, d0);
}
