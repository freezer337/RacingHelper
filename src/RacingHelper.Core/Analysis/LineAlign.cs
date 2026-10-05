namespace RacingHelper.Analysis;

public static class LineAlign
{
    // Dead reckoning is accurate (~0.1 % drift) but needs pinning to the map. The lap distance says exactly where along
    // the track you are, so the along-track error is corrected quickly; across the track only the slow drift is removed,
    // which keeps where you really were across the width (inside / outside, kerb to kerb).
    // Checked against GPS on two tracks: lateral error 2.0 m → 0.37 m (Phillip Island), 0.69 m → 0.36 m (Bathurst).
    public const float AlongM = 50, AcrossM = 3000, MaxOffTrackM = 8;

    /// <summary>
    /// Brings a lap's X/Y into the track model's frame. GPS laps are already in that frame; dead-reckoned laps go through
    /// the along/across filter above, forwards and backwards over the lap and averaged.
    /// </summary>
    public static (float[] x, float[] y) ToModel(DistLap lap, TrackModel model, bool lapHasGps)
    {
        int n = lap.N;
        var x = new float[n]; var y = new float[n];
        if (lapHasGps && model.FromGps)
        {
            Array.Copy(lap.X, x, n); Array.Copy(lap.Y, y, n);
            return (x, y);
        }
        var mx = new float[n]; var my = new float[n]; var tx = new float[n]; var ty = new float[n];
        for (int i = 0; i < n; i++)
        {
            float d = i * lap.Step;
            (mx[i], my[i]) = model.PosAt(d);
            var (ax, ay) = model.PosAt(d - 4); var (bx, by) = model.PosAt(d + 4);
            float len = MathF.Max(1e-3f, MathF.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay)));
            tx[i] = (bx - ax) / len; ty[i] = (by - ay) / len;
        }
        var (fx, fy) = Filter(lap, mx, my, tx, ty, forward: true);
        var (rx, ry) = Filter(lap, mx, my, tx, ty, forward: false);
        for (int i = 0; i < n; i++)
        {
            if (!float.IsFinite(lap.X[i]) || !float.IsFinite(fx[i]) || !float.IsFinite(rx[i])) { x[i] = y[i] = float.NaN; continue; }
            float px = lap.X[i] + (fx[i] + rx[i]) / 2, py = lap.Y[i] + (fy[i] + ry[i]) / 2;
            // never further off the line than a car can be
            float lat = (px - mx[i]) * -ty[i] + (py - my[i]) * tx[i];
            float clamp = Math.Clamp(lat, -MaxOffTrackM, MaxOffTrackM) - lat;
            x[i] = px + clamp * -ty[i]; y[i] = py + clamp * tx[i];
        }
        return (x, y);
    }

    /// <summary>The offset (map − dead reckoning) after the along/across filter, two passes round the lap.</summary>
    static (float[] ox, float[] oy) Filter(DistLap lap, float[] mx, float[] my, float[] tx, float[] ty, bool forward)
    {
        int n = lap.N;
        var ox = new float[n]; var oy = new float[n];
        Array.Fill(ox, float.NaN); Array.Fill(oy, float.NaN);
        float ka = Math.Min(lap.Step / AlongM, 1), kl = Math.Min(lap.Step / AcrossM, 1);
        float cx = float.NaN, cy = float.NaN;
        for (int pass = 0; pass < 2; pass++)
            for (int k = 0; k < n; k++)
            {
                int i = forward ? k : n - 1 - k;
                if (!float.IsFinite(lap.X[i]) || !float.IsFinite(lap.Y[i])) continue;
                float gx = mx[i] - lap.X[i], gy = my[i] - lap.Y[i];
                if (float.IsNaN(cx)) { cx = gx; cy = gy; }
                else
                {
                    float ex = gx - cx, ey = gy - cy;
                    float ea = ex * tx[i] + ey * ty[i], el = ex * -ty[i] + ey * tx[i];
                    cx += ea * ka * tx[i] + el * kl * -ty[i];
                    cy += ea * ka * ty[i] + el * kl * tx[i];
                }
                ox[i] = cx; oy[i] = cy;
            }
        return (ox, oy);
    }
}
