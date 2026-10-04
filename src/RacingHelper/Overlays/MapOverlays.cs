using System.Windows;
using System.Windows.Media;
using RacingHelper.Analysis;
using RacingHelper.Live;

namespace RacingHelper.App.Overlays;

/// <summary>Whole-track map with every car, corner numbers and the start/finish line.</summary>
public sealed class TrackMapOverlay : OverlayView
{
    public override string Id => "track-map";
    public override double BaseWidth => 300;
    public override double BaseHeight => 300;

    TrackModel? _model;
    StreamGeometry? _path;
    double _k, _ox, _oy, _minX, _maxY;

    protected override void Draw(DrawingContext dc, LiveState s)
    {
        var m = Ctx.Hub.Model;
        if (m == null || m.X.Length < 10)
        {
            Header(dc, "Track map");
            DrawText(dc, "The map is built from your first clean lap.", 10, 30, 11, Theme.Muted, Theme.Ui, 0, BaseWidth - 20);
            return;
        }
        if (!ReferenceEquals(m, _model)) Build(m);
        dc.DrawGeometry(null, Theme.P(Theme.Track, 8), _path);
        dc.DrawGeometry(null, Theme.P(Theme.B("#5B6577"), 2), _path);

        // start / finish
        var p0 = Map(m.X[0], m.Y[0]); var p1 = Map(m.X[2], m.Y[2]);
        var dir = p1 - p0; if (dir.Length > 0) dir.Normalize();
        var nrm = new Vector(-dir.Y, dir.X) * 8;
        dc.DrawLine(Theme.P(Theme.Text, 3), p0 - nrm, p0 + nrm);

        foreach (var c in m.Corners)
        {
            var (x, y) = m.PosAt(c.Apex);
            var pt = Map(x, y);
            DrawText(dc, c.Name.TrimStart('T'), pt.X + 7, pt.Y - 7, 9, Theme.Muted, Theme.UiBold);
        }
        foreach (var car in s.MapCars.OrderBy(c => c.IsPlayer))
        {
            var (x, y) = m.PosAt(car.Pct * m.Length);
            var pt = Map(x, y);
            if (car.IsPlayer)
            {
                dc.DrawEllipse(Theme.Accent, Theme.P(Theme.Text, 2), pt, 7, 7);
            }
            else
            {
                dc.DrawEllipse(car.InPit ? Theme.Dim : Theme.B(car.ClassColor, 0.95), Theme.P(Theme.B("#0E1116"), 1), pt, 5, 5);
            }
        }
    }

    void Build(TrackModel m)
    {
        _model = m;
        double minX = m.X.Min(), maxX = m.X.Max(), minY = m.Y.Min(), maxY = m.Y.Max();
        double pad = 18;
        _k = Math.Min((BaseWidth - 2 * pad) / Math.Max(1, maxX - minX), (BaseHeight - 2 * pad) / Math.Max(1, maxY - minY));
        _ox = (BaseWidth - (maxX - minX) * _k) / 2;
        _oy = (BaseHeight - (maxY - minY) * _k) / 2;
        _minX = minX; _maxY = maxY;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(Map(m.X[0], m.Y[0]), false, true);
            for (int i = 1; i < m.X.Length; i++) c.LineTo(Map(m.X[i], m.Y[i]), true, true);
        }
        g.Freeze();
        _path = g;
    }

    Point Map(double x, double y) => new(_ox + (x - _minX) * _k, _oy + (_maxY - y) * _k);
}

/// <summary>Shared heading-up projection used by the mini map and line comparison.</summary>
public abstract class HeadingUpMap : OverlayView
{
    protected double Cx, Cy, K, Px, Py, Cos, Sin;

    protected void Setup(LiveState s, double radiusMetres)
    {
        Cx = BaseWidth / 2; Cy = BaseHeight / 2 + 20;
        K = (BaseWidth / 2 - 8) / radiusMetres;
        Px = s.PlayerX; Py = s.PlayerY;
        double phi = Math.PI / 2 - (double.IsFinite(s.PlayerHeading) ? s.PlayerHeading : 0);
        Cos = Math.Cos(phi); Sin = Math.Sin(phi);
    }

    protected Point P(double x, double y)
    {
        double dx = x - Px, dy = y - Py;
        double rx = dx * Cos - dy * Sin, ry = dx * Sin + dy * Cos;
        return new Point(Cx + rx * K, Cy - ry * K);
    }

    protected StreamGeometry? Polyline(Func<int, (float x, float y)> pt, int from, int count, int stride = 1)
    {
        if (count < 2) return null;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            bool started = false;
            for (int i = 0; i < count; i += stride)
            {
                var (x, y) = pt(from + i);
                if (!float.IsFinite(x) || !float.IsFinite(y)) { started = false; continue; }
                var p = P(x, y);
                if (!started) { c.BeginFigure(p, false, false); started = true; }
                else c.LineTo(p, true, true);
            }
        }
        g.Freeze();
        return g;
    }

    protected void DrawPlayer(DrawingContext dc)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(Cx, Cy - 9), true, true);
            c.LineTo(new Point(Cx + 6, Cy + 7), true, false);
            c.LineTo(new Point(Cx, Cy + 3), true, false);
            c.LineTo(new Point(Cx - 6, Cy + 7), true, false);
        }
        g.Freeze();
        dc.DrawGeometry(Theme.Accent, Theme.P(Theme.B("#0E1116"), 1), g);
    }
}

/// <summary>Heading-up close-up of the track around you with nearby cars.</summary>
public sealed class MiniMapOverlay : HeadingUpMap
{
    public override string Id => "mini-map";
    public override double BaseWidth => 220;
    public override double BaseHeight => 220;

    protected override void Draw(DrawingContext dc, LiveState s)
    {
        var m = Ctx.Hub.Model;
        if (m == null || !float.IsFinite(s.PlayerX)) { Header(dc, "Mini map"); DrawText(dc, "Waiting for track map…", 10, 30, 11, Theme.Muted); return; }
        Setup(s, 260);
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, BaseWidth, BaseHeight), 8, 8));
        var g = Polyline(i => (m.X[i % m.X.Length], m.Y[i % m.Y.Length]), 0, m.X.Length + 1);
        if (g != null)
        {
            dc.DrawGeometry(null, Theme.P(Theme.Track, 11), g);
            dc.DrawGeometry(null, Theme.P(Theme.B("#5B6577"), 1.5), g);
        }
        foreach (var c in m.Corners)
        {
            var (x, y) = m.PosAt(c.Apex);
            var p = P(x, y);
            if (p.X > 0 && p.X < BaseWidth && p.Y > 0 && p.Y < BaseHeight) DrawText(dc, c.Name, p.X + 8, p.Y - 6, 9, Theme.Muted, Theme.UiBold);
        }
        foreach (var car in s.MapCars.Where(c => !c.IsPlayer))
        {
            var (x, y) = m.PosAt(car.Pct * m.Length);
            var p = P(x, y);
            if (p.X < -10 || p.X > BaseWidth + 10 || p.Y < -10 || p.Y > BaseHeight + 10) continue;
            dc.DrawEllipse(Theme.B(car.ClassColor, 0.95), Theme.P(Theme.B("#0E1116"), 1), p, 5, 5);
        }
        DrawPlayer(dc);
        dc.Pop();
    }
}

/// <summary>Close-up of your line (green) vs the reference line (purple) through the current part of the track.</summary>
public sealed class LineCompareOverlay : HeadingUpMap
{
    public override string Id => "line-compare";
    public override double BaseWidth => 260;
    public override double BaseHeight => 260;

    protected override void Draw(DrawingContext dc, LiveState s)
    {
        Header(dc, "Line vs reference");
        var m = Ctx.Hub.Model;
        var r = Ctx.Hub.Reference;
        if (m == null || !float.IsFinite(s.PlayerX)) { DrawText(dc, "Waiting for track map…", 10, 30, 11, Theme.Muted); return; }
        Setup(s, 90);
        dc.PushClip(new RectangleGeometry(new Rect(0, 18, BaseWidth, BaseHeight - 18), 6, 6));
        // track band (approximate width) along the model line near us
        int mi = (int)(s.LapDist / m.Step);
        int span = (int)(160 / m.Step);
        var track = Polyline(i => { int k = ((i % m.X.Length) + m.X.Length) % m.X.Length; return (m.X[k], m.Y[k]); }, mi - span, span * 2);
        if (track != null) dc.DrawGeometry(null, Theme.P(Theme.B("#2A303A"), 12 * K), track);

        if (r?.AlignedX != null && r.AlignedY != null)
        {
            var ax = r.AlignedX; var ay = r.AlignedY;
            int ri = (int)(s.LapDist / r.Dist.Step), rs = (int)(160 / r.Dist.Step);
            var refLine = Polyline(i => { int k = ((i % ax.Length) + ax.Length) % ax.Length; return (ax[k], ay[k]); }, ri - rs, rs * 2, 2);
            if (refLine != null) dc.DrawGeometry(null, Theme.P(Theme.Purple, 2.5), refLine);
        }
        if (s.TrailX.Length > 1)
        {
            var tx = s.TrailX; var ty = s.TrailY;
            var mine = Polyline(i => (tx[i], ty[i]), 0, tx.Length);
            if (mine != null) dc.DrawGeometry(null, Theme.P(Theme.Green, 2.5), mine);
        }
        DrawPlayer(dc);
        dc.Pop();
        DrawText(dc, "you", 10, BaseHeight - 18, 9.5, Theme.Green, Theme.UiBold);
        DrawText(dc, "ref", 38, BaseHeight - 18, 9.5, Theme.Purple, Theme.UiBold);
        var corner = m.CornerAt(s.LapDist);
        if (corner != null) DrawText(dc, corner.Name, BaseWidth - 10, BaseHeight - 18, 10, Theme.Text, Theme.UiBold, 2);
    }
}
