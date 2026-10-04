using System.Windows;
using System.Windows.Media;
using RacingHelper.Live;

namespace RacingHelper.App.Overlays;

/// <summary>Sector times + deltas vs reference, plus best / last / reference lap rows.</summary>
public sealed class DeltaSectorsOverlay : OverlayView
{
    public override string Id => "delta-sectors";
    public override double BaseWidth => 300;
    public override double BaseHeight => 170;

    protected override void Draw(DrawingContext dc, LiveState s)
    {
        Header(dc, "Delta vs " + (string.IsNullOrEmpty(s.ReferenceLabel) ? "reference" : s.ReferenceLabel));
        double y = 24;
        // show at most 5 sectors: around the current one when there are many mini-sectors
        var sectors = s.Sectors;
        int cur = Math.Max(0, sectors.FindIndex(x => x.State == "current"));
        int start = sectors.Count <= 5 ? 0 : Math.Clamp(cur - 2, 0, sectors.Count - 5);
        foreach (var sec in sectors.Skip(start).Take(5))
        {
            bool current = sec.State == "current";
            if (current) dc.DrawRoundedRectangle(Theme.PanelRow, null, new Rect(6, y - 1, BaseWidth - 12, 19), 3, 3);
            DrawText(dc, "S" + sec.Index, 12, y + 1, 11, current ? Theme.Text : Theme.Muted, Theme.UiBold);
            Num(dc, sec.State == "pending" ? "–" : SectorTime(sec.Time), 52, y, 13, Theme.Text);
            Brush b = sec.Color switch { "purple" => Theme.Purple, "green" => Theme.Green, "yellow" => Theme.Yellow, _ => DeltaBrush(sec.Delta) };
            if (sec.State != "pending") Num(dc, Delta(sec.Delta), BaseWidth - 12, y, 13, b, 2);
            y += 20;
        }
        dc.DrawLine(Theme.P(Theme.Line, 1), new Point(10, y + 2), new Point(BaseWidth - 10, y + 2));
        y += 8;
        Row(dc, ref y, "BEST", s.SessionBest, s.SessionBest - s.ReferenceLapTime);
        Row(dc, ref y, "LAST", s.LastLapTime, s.LastLapDelta);
        DrawText(dc, "REF", 12, y + 1, 11, Theme.Muted, Theme.UiBold);
        Num(dc, LapTime(s.ReferenceLapTime), 52, y, 13, Theme.Purple);
    }

    void Row(DrawingContext dc, ref double y, string label, double time, double delta)
    {
        DrawText(dc, label, 12, y + 1, 11, Theme.Muted, Theme.UiBold);
        Num(dc, LapTime(time), 52, y, 13, Theme.Text);
        if (double.IsFinite(delta)) Num(dc, Delta(delta), BaseWidth - 12, y, 13, DeltaBrush(delta), 2);
        y += 19;
    }

    static string SectorTime(double t) => !double.IsFinite(t) ? "–" : t >= 60 ? LapTime(t) : "0:" + t.ToString("00.000", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Big live delta with a gain / loss bar and predicted lap.</summary>
public sealed class DeltaBarOverlay : OverlayView
{
    public override string Id => "delta-bar";
    public override double BaseWidth => 380;
    public override double BaseHeight => 80;

    protected override void Draw(DrawingContext dc, LiveState s)
    {
        double d = s.Delta;
        var bar = new Rect(10, 10, BaseWidth - 20, 10);
        dc.DrawRoundedRectangle(Theme.PanelRow, null, bar, 3, 3);
        double mid = bar.X + bar.Width / 2;
        if (double.IsFinite(d))
        {
            double frac = Math.Clamp(Math.Abs(d) / 1.0, 0, 1) * bar.Width / 2;
            var r = d <= 0 ? new Rect(mid, bar.Y, frac, bar.Height) : new Rect(mid - frac, bar.Y, frac, bar.Height);
            if (r.Width > 0) dc.DrawRoundedRectangle(DeltaBrush(d), null, r, 3, 3);
        }
        dc.DrawRectangle(Theme.Text, null, new Rect(mid - 1, bar.Y - 3, 2, bar.Height + 6));

        string txt = s.OutLap ? "OUT LAP" : double.IsFinite(d) ? Delta(d, "0.00") : "–.––";
        Num(dc, txt, BaseWidth / 2, 24, 30, s.OutLap ? Theme.Muted : DeltaBrush(d), 1);
        // trend arrow: are we gaining right now?
        if (double.IsFinite(d) && Math.Abs(s.DeltaRate) > 0.005)
        {
            bool gaining = s.DeltaRate < 0;
            double ax = BaseWidth / 2 + 72, ay = 42;
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                if (gaining) { c.BeginFigure(new Point(ax, ay + 6), true, true); c.LineTo(new Point(ax + 12, ay + 6), true, false); c.LineTo(new Point(ax + 6, ay - 4), true, false); }
                else { c.BeginFigure(new Point(ax, ay - 4), true, true); c.LineTo(new Point(ax + 12, ay - 4), true, false); c.LineTo(new Point(ax + 6, ay + 6), true, false); }
            }
            g.Freeze();
            dc.DrawGeometry(gaining ? Theme.Green : Theme.Red, null, g);
        }
        DrawText(dc, "PRED", 12, 30, 9, Theme.Muted, Theme.UiBold);
        Num(dc, LapTime(s.PredictedLap), 12, 42, 14, Theme.Text);
        DrawText(dc, "REF", BaseWidth - 12, 30, 9, Theme.Muted, Theme.UiBold, 2);
        Num(dc, LapTime(s.ReferenceLapTime), BaseWidth - 12, 42, 14, Theme.Purple, 2);
    }
}

/// <summary>What you're comparing against, plus key lap times.</summary>
public sealed class ComparisonTargetOverlay : OverlayView
{
    public override string Id => "comparison-target";
    public override double BaseWidth => 300;
    public override double BaseHeight => 130;

    protected override void Draw(DrawingContext dc, LiveState s)
    {
        Header(dc, "Comparison target");
        string mode = s.ReferenceMode switch { "pb" => "PERSONAL BEST", "session" => "SESSION BEST", "last" => "LAST LAP", "lap" => "CHOSEN LAP", _ => s.ReferenceMode.ToUpperInvariant() };
        var chip = Fmt(mode, 9, Theme.B("#0E1116"), Theme.UiBold);
        dc.DrawRoundedRectangle(Theme.Purple, null, new Rect(BaseWidth - chip.Width - 22, 5, chip.Width + 12, 15), 7, 7);
        dc.DrawText(chip, new Point(BaseWidth - chip.Width - 16, 6));
        DrawText(dc, string.IsNullOrEmpty(s.ReferenceLabel) ? "No reference yet — drive a clean lap" : s.ReferenceLabel, 10, 26, 13, Theme.Text, Theme.UiBold, 0, BaseWidth - 20);
        double y = 52;
        void Row(string l, double t, double d)
        {
            DrawText(dc, l, 10, y + 1, 10, Theme.Muted, Theme.UiBold);
            Num(dc, LapTime(t), 110, y, 13, Theme.Text);
            if (double.IsFinite(d)) Num(dc, Delta(d), BaseWidth - 10, y, 13, DeltaBrush(d), 2);
            y += 18;
        }
        Row("PREDICTED", s.PredictedLap, s.Delta);
        Row("SESSION BEST", s.SessionBest, s.SessionBest - s.ReferenceLapTime);
        Row("PB", s.PersonalBest, double.NaN);
        DrawText(dc, "Ctrl+Shift+F11 to switch", 10, BaseHeight - 16, 9, Theme.Dim);
    }
}

/// <summary>Result of the last corner vs reference: time, apex speed, braking and the main reason.</summary>
public sealed class CornerAnalysisOverlay : OverlayView
{
    public override string Id => "corner-analysis";
    public override double BaseWidth => 330;
    public override double BaseHeight => 120;

    protected override void Draw(DrawingContext dc, LiveState s)
    {
        Header(dc, "Corner analysis");
        var c = s.LastCorner;
        if (c == null)
        {
            DrawText(dc, string.IsNullOrEmpty(s.ReferenceLabel) ? "Waiting for a reference lap…" : "Complete a corner to see feedback", 10, 30, 12, Theme.Muted);
            return;
        }
        DrawText(dc, "Corner " + c.Name.TrimStart('T'), 10, 22, 17, Theme.Text, Theme.UiBold);
        Num(dc, (c.TimeDelta >= 0 ? "+" : "−") + Math.Abs(c.TimeDelta).ToString("0.00") + "s", BaseWidth - 10, 20, 22, c.TimeDelta <= 0 ? Theme.Green : Theme.Red, 2);
        double y = 52;
        void Stat(double x, string label, string value, Brush b)
        {
            DrawText(dc, label, x, y, 9, Theme.Muted, Theme.UiBold);
            Num(dc, value, x, y + 11, 13, b);
        }
        Stat(10, "APEX", float.IsFinite(c.MinSpeed) ? $"{SpeedConv(c.MinSpeed):0}" : "–", Theme.Text);
        Stat(70, "Δ APEX", float.IsFinite(c.MinSpeedDiff) ? $"{(c.MinSpeedDiff >= 0 ? "+" : "−")}{Math.Abs(SpeedConv(c.MinSpeedDiff)):0}" : "–", c.MinSpeedDiff >= 0 ? Theme.Green : Theme.Red);
        Stat(140, "BRAKE", float.IsFinite(c.BrakeDiff) ? $"{(c.BrakeDiff >= 0 ? "+" : "−")}{Math.Abs(c.BrakeDiff):0} m" : "–", Theme.Text);
        Stat(220, "Δ EXIT", float.IsFinite(c.ExitSpeedDiff) ? $"{(c.ExitSpeedDiff >= 0 ? "+" : "−")}{Math.Abs(SpeedConv(c.ExitSpeedDiff)):0}" : "–", c.ExitSpeedDiff >= 0 ? Theme.Green : Theme.Red);
        string advice = !string.IsNullOrEmpty(c.Advice) ? c.Advice : c.Verdict;
        DrawText(dc, advice, 10, 88, 10.5, Theme.Yellow, Theme.Ui, 0, BaseWidth - 20, maxLines: 2);
    }
}
