namespace RacingHelper.Analysis;

/// <summary>
/// iRacing exposes current conditions but not a forecast, so we project the recent trend
/// (linear fit of the last ~20 minutes) forward. Clearly a projection, not an official forecast.
/// </summary>
public sealed class WeatherTrend
{
    readonly List<(double t, float track, float air, float wet, float precip)> _samples = new();

    public void Add(double sessionTime, float trackTemp, float airTemp, int wetness, float precip)
    {
        if (!float.IsFinite(trackTemp)) return;
        if (_samples.Count > 0 && sessionTime - _samples[^1].t < 15) return;
        if (_samples.Count > 0 && sessionTime < _samples[^1].t) _samples.Clear();
        _samples.Add((sessionTime, trackTemp, airTemp, wetness, float.IsFinite(precip) ? precip : 0));
        while (_samples.Count > 0 && sessionTime - _samples[0].t > 1200) _samples.RemoveAt(0);
    }

    public void Clear() => _samples.Clear();

    public sealed record Projection(int Minutes, float TrackTemp, float AirTemp, float Wetness, float Precip);

    public List<Projection> Project(params int[] minutes)
    {
        var res = new List<Projection>();
        if (_samples.Count < 4) return res;
        var (ta, tb) = Fit(s => s.track);
        var (aa, ab) = Fit(s => s.air);
        var (wa, wb) = Fit(s => s.wet);
        var (pa, pb) = Fit(s => s.precip);
        double now = _samples[^1].t;
        foreach (var m in minutes)
        {
            double t = now + m * 60;
            res.Add(new Projection(m, (float)(ta + tb * t), (float)(aa + ab * t), (float)Math.Clamp(wa + wb * t, 1, 7), (float)Math.Clamp(pa + pb * t, 0, 1)));
        }
        return res;
    }

    public float TrackTempRatePerHour
    {
        get
        {
            if (_samples.Count < 4) return 0;
            var (_, b) = Fit(s => s.track);
            return (float)(b * 3600);
        }
    }

    (double a, double b) Fit(Func<(double t, float track, float air, float wet, float precip), float> sel)
    {
        int n = _samples.Count;
        double mx = _samples.Average(s => s.t), my = _samples.Average(s => (double)sel(s));
        double sxx = 0, sxy = 0;
        foreach (var s in _samples) { double dx = s.t - mx; sxx += dx * dx; sxy += dx * (sel(s) - my); }
        double b = sxx > 0 ? sxy / sxx : 0;
        return (my - b * mx, b);
    }
}

/// <summary>Community-derived approximation of iRating change for a race finish (Elo-style, per class).</summary>
public static class IRatingEstimator
{
    /// <param name="ratings">iRatings of all starters in the class, in finishing order (index = position-1).</param>
    public static double[] Estimate(IReadOnlyList<int> ratings, int starters = -1)
    {
        int n = ratings.Count;
        var res = new double[n];
        if (n < 2) return res;
        if (starters <= 0) starters = n;
        double br1 = 1600 / Math.Log(2);
        double Chance(double a, double b)
        {
            double ea = Math.Exp(-a / br1), eb = Math.Exp(-b / br1);
            return (1 - ea) * eb / ((1 - eb) * ea + (1 - ea) * eb);
        }
        // expected score = sum of win chances against every other driver
        var expected = new double[n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) if (i != j) expected[i] += Chance(ratings[i], ratings[j]);
        for (int i = 0; i < n; i++)
        {
            double fudge = (n / 2.0 - (i + 1)) / 100.0;
            double actual = n - (i + 1);
            res[i] = (actual - expected[i] - fudge) * 200.0 / Math.Max(1, starters);
        }
        return res;
    }
}
