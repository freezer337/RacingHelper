namespace RacingHelper.Analysis;

public static class LineAlign
{
    /// <summary>
    /// Brings a lap's X/Y into the track model's frame. GPS laps are already in that frame; dead-reckoned laps get a
    /// slowly varying offset (low-passed over <paramref name="windowM"/>) that removes integration drift while keeping
    /// the corner-scale line differences we actually want to see.
    /// </summary>
    public static (float[] x, float[] y) ToModel(DistLap lap, TrackModel model, bool lapHasGps, float windowM = 300)
    {
        int n = lap.N;
        var x = new float[n]; var y = new float[n];
        if (lapHasGps && model.FromGps)
        {
            Array.Copy(lap.X, x, n); Array.Copy(lap.Y, y, n);
            return (x, y);
        }
        var ox = new float[n]; var oy = new float[n];
        for (int i = 0; i < n; i++)
        {
            var (mx, my) = model.PosAt(i * lap.Step);
            ox[i] = float.IsFinite(lap.X[i]) ? mx - lap.X[i] : float.NaN;
            oy[i] = float.IsFinite(lap.Y[i]) ? my - lap.Y[i] : float.NaN;
        }
        int w = Math.Max(3, (int)(windowM / lap.Step));
        var sx = TrackModel.Smooth(ox, w); var sy = TrackModel.Smooth(oy, w);
        for (int i = 0; i < n; i++)
        {
            x[i] = float.IsFinite(lap.X[i]) ? lap.X[i] + sx[i] : float.NaN;
            y[i] = float.IsFinite(lap.Y[i]) ? lap.Y[i] + sy[i] : float.NaN;
        }
        return (x, y);
    }
}
