using System.Windows;
using System.Windows.Media;
using RacingHelper.Live;

namespace RacingHelper.App.Overlays;

/// <summary>Pedal bars, gear, speed, RPM / shift light and a steering indicator.</summary>
public sealed class InputsOverlay : OverlayView
{
    public override string Id => "inputs";
    public override double BaseWidth => 300;
    public override double BaseHeight => 120;

    protected override void Draw(DrawingContext dc, LiveState s)
    {
        // RPM strip with shift light colours
        var rpmRect = new Rect(10, 8, BaseWidth - 20, 6);
        double redline = s.RedLine > 0 ? s.RedLine : 8000;
        double frac = s.Rpm / redline;
        Brush rpmColor = s.ShiftRpm > 0 && s.Rpm >= s.ShiftRpm ? (s.Rpm >= s.ShiftRpm * 1.06 ? Theme.Blue : Theme.Red) : frac > 0.85 ? Theme.Yellow : Theme.Accent;
        Bar(dc, rpmRect, frac, rpmColor);

        // pedals
        double top = 22, h = BaseHeight - top - 10;
        void Pedal(double x, double v, Brush b, string label)
        {
            Bar(dc, new Rect(x, top, 16, h - 14), v, b, vertical: true);
            DrawText(dc, label, x + 8, top + h - 12, 9, Theme.Muted, Theme.UiBold, 1);
        }
        Pedal(12, 1 - s.Clutch, Theme.Blue, "C"); // iRacing reports clutch engagement (1 = pedal up)
        Pedal(34, s.Brake, s.Abs ? Theme.Orange : Theme.Red, "B");
        Pedal(56, s.Throttle, Theme.Green, "T");
        Num(dc, (s.Brake * 100).ToString("0"), 42, top - 1, 9, Theme.Muted, 1);

        // gear + speed
        string gear = s.Gear switch { -1 => "R", 0 => "N", _ => s.Gear.ToString() };
        Num(dc, gear, 128, 20, 52, Theme.Text, 1);
        Num(dc, Speed(s.Speed), 128, 80, 18, Theme.Text, 1);
        DrawText(dc, SpeedUnit, 128, 100, 8.5, Theme.Muted, Theme.UiBold, 1);

        // steering wheel
        double cx = 230, cy = 62, r = 34;
        double maxAngle = float.IsFinite(s.SteerMax) && s.SteerMax > 0 ? s.SteerMax / 2 : 4.7;
        double ang = -s.Steer; // iRacing: + = left
        dc.DrawEllipse(null, Theme.P(Theme.Line, 5), new Point(cx, cy), r, r);
        dc.PushTransform(new RotateTransform(ang * 180 / Math.PI, cx, cy));
        dc.DrawEllipse(null, Theme.P(Theme.Muted, 3), new Point(cx, cy), r, r);
        dc.DrawLine(Theme.P(Theme.Muted, 3), new Point(cx - r, cy), new Point(cx + r, cy));
        dc.DrawLine(Theme.P(Theme.Muted, 3), new Point(cx, cy), new Point(cx, cy + r));
        dc.DrawRectangle(Theme.Accent, null, new Rect(cx - 3, cy - r - 3, 6, 8));
        dc.Pop();
        Num(dc, $"{Math.Abs(s.Steer) * 180 / Math.PI:0}°", cx, cy - 8, 11, Theme.Text, 1);
        if (s.Abs) DrawText(dc, "ABS", 78, top, 9, Theme.Orange, Theme.UiBold);
        if (float.IsFinite(s.BrakeBias)) Num(dc, $"BB {s.BrakeBias:0.0}", 82, BaseHeight - 22, 9.5, Theme.Muted);
        _ = maxAngle;
    }
}

/// <summary>Scrolling trace of throttle / brake / steering.</summary>
public sealed class InputTelemetryOverlay : OverlayView
{
    public override string Id => "input-telemetry";
    public override double BaseWidth => 420;
    public override double BaseHeight => 130;
    static readonly Pen Thr = Theme.P(Theme.Green, 2), Brk = Theme.P(Theme.Red, 2), Str = Theme.P(Theme.B("#F3F6FA", 0.55), 1.2), Grid = Theme.P(Theme.Line, 1);

    protected override void Draw(DrawingContext dc, LiveState s)
    {
        Header(dc, "Input telemetry");
        var r = new Rect(10, 22, BaseWidth - 50, BaseHeight - 32);
        for (int i = 0; i <= 4; i++) dc.DrawLine(Grid, new Point(r.X, r.Y + r.Height * i / 4), new Point(r.Right, r.Y + r.Height * i / 4));
        Trace(dc, s.HistSteer.Select(v => (v + 1) / 2).ToArray(), r, Str);
        Trace(dc, s.HistBrake, r, Brk);
        Trace(dc, s.HistThrottle, r, Thr);
        Bar(dc, new Rect(BaseWidth - 32, 22, 8, r.Height), s.Brake, Theme.Red, true);
        Bar(dc, new Rect(BaseWidth - 20, 22, 8, r.Height), s.Throttle, Theme.Green, true);
    }
}

/// <summary>Your pedal traces vs the reference lap at the same track position.</summary>
public sealed class InputCompareOverlay : OverlayView
{
    public override string Id => "input-compare";
    public override double BaseWidth => 420;
    public override double BaseHeight => 140;
    static readonly Pen Thr = Theme.P(Theme.Green, 2), Brk = Theme.P(Theme.Red, 2);
    static readonly Pen RThr = Theme.P(Theme.B("#22D37E", 0.55), 1.5, true), RBrk = Theme.P(Theme.B("#FF4D5E", 0.6), 1.5, true), Grid = Theme.P(Theme.Line, 1);

    protected override void Draw(DrawingContext dc, LiveState s)
    {
        Header(dc, "Input comparison");
        DrawText(dc, "you ——   ref - - -", BaseWidth - 10, 6, 9, Theme.Muted, Theme.Ui, 2);
        var r = new Rect(10, 24, BaseWidth - 20, BaseHeight - 50);
        dc.DrawLine(Grid, new Point(r.X, r.Bottom), new Point(r.Right, r.Bottom));
        dc.DrawLine(Grid, new Point(r.X, r.Y), new Point(r.Right, r.Y));
        Trace(dc, s.HistRefBrake, r, RBrk);
        Trace(dc, s.HistRefThrottle, r, RThr);
        Trace(dc, s.HistBrake, r, Brk);
        Trace(dc, s.HistThrottle, r, Thr);
        double y = BaseHeight - 22;
        DrawText(dc, "THR", 10, y, 9, Theme.Muted, Theme.UiBold);
        Num(dc, $"{s.Throttle * 100:0}", 36, y - 2, 12, Theme.Green);
        Num(dc, float.IsFinite(s.RefThrottle) ? $"{s.RefThrottle * 100:0}" : "–", 66, y - 2, 12, Theme.Muted);
        DrawText(dc, "BRK", 110, y, 9, Theme.Muted, Theme.UiBold);
        Num(dc, $"{s.Brake * 100:0}", 136, y - 2, 12, Theme.Red);
        Num(dc, float.IsFinite(s.RefBrake) ? $"{s.RefBrake * 100:0}" : "–", 166, y - 2, 12, Theme.Muted);
        if (s.RefGear > 0) { DrawText(dc, "REF GEAR", 220, y, 9, Theme.Muted, Theme.UiBold); Num(dc, s.RefGear.ToString(), 276, y - 2, 12, s.RefGear == s.Gear ? Theme.Text : Theme.Yellow); }
    }
}

/// <summary>Your speed vs the reference lap's speed at the same position.</summary>
public sealed class SpeedCompareOverlay : OverlayView
{
    public override string Id => "speed-compare";
    public override double BaseWidth => 420;
    public override double BaseHeight => 140;
    static readonly Pen You = Theme.P(Theme.Text, 2), Ref = Theme.P(Theme.Purple, 1.6, true), Grid = Theme.P(Theme.Line, 1);

    protected override void Draw(DrawingContext dc, LiveState s)
    {
        Header(dc, "Speed comparison");
        var r = new Rect(10, 24, BaseWidth - 130, BaseHeight - 34);
        var all = s.HistSpeed.Concat(s.HistRefSpeed).Where(float.IsFinite).ToList();
        double max = all.Count > 0 ? all.Max() * 1.05 + 1 : 80, min = all.Count > 0 ? Math.Max(0, all.Min() * 0.9) : 0;
        dc.DrawLine(Grid, new Point(r.X, r.Bottom), new Point(r.Right, r.Bottom));
        Trace(dc, s.HistRefSpeed, r, Ref, min, max);
        Trace(dc, s.HistSpeed, r, You, min, max);
        double x = BaseWidth - 110;
        DrawText(dc, "YOU", x, 24, 9, Theme.Muted, Theme.UiBold);
        Num(dc, Speed(s.Speed), x, 34, 22, Theme.Text);
        DrawText(dc, "REF", x, 66, 9, Theme.Muted, Theme.UiBold);
        Num(dc, Speed(s.RefSpeed), x, 76, 16, Theme.Purple);
        if (float.IsFinite(s.RefSpeed))
        {
            double diff = SpeedConv((s.Speed - s.RefSpeed) * 3.6);
            Num(dc, (diff >= 0 ? "+" : "−") + Math.Abs(diff).ToString("0"), x + 52, 76, 16, diff >= 0 ? Theme.Green : Theme.Red);
        }
        DrawText(dc, SpeedUnit, x, 102, 8.5, Theme.Muted, Theme.UiBold);
    }
}

/// <summary>Countdown boards to the reference braking point of the next corner.</summary>
public sealed class BrakeIndicatorOverlay : OverlayView
{
    public override string Id => "brake-indicator";
    public override double BaseWidth => 110;
    public override double BaseHeight => 260;
    static readonly int[] Boards = { 120, 90, 60, 30 };

    protected override void Draw(DrawingContext dc, LiveState s)
    {
        float dist = s.NextBrakeDist;
        Header(dc, string.IsNullOrEmpty(s.NextCorner) ? "Brake" : s.NextCorner, 10, 6);
        if (float.IsFinite(s.NextCornerRefMinSpeed))
            DrawText(dc, $"apex {SpeedConv(s.NextCornerRefMinSpeed):0}", BaseWidth - 8, 6, 9, Theme.Muted, Theme.Ui, 2);
        double y = 26, h = 34, gap = 8;
        for (int i = 0; i < Boards.Length; i++)
        {
            bool lit = float.IsFinite(dist) && dist <= Boards[i] && dist > 0;
            var r = new Rect(14, y, BaseWidth - 28, h);
            dc.DrawRoundedRectangle(lit ? Theme.B("#F3F6FA", 0.95) : Theme.PanelRow, lit ? null : Theme.P(Theme.Line, 1), r, 4, 4);
            Num(dc, Boards[i] + "m", r.X + r.Width / 2, r.Y + 6, 17, lit ? Theme.B("#0E1116") : Theme.Dim, 1);
            y += h + gap;
        }
        bool brake = float.IsFinite(dist) && dist <= 0.5f && dist > -5;
        var br = new Rect(8, y + 4, BaseWidth - 16, 42);
        bool flash = brake && (DateTime.Now.Millisecond / 125) % 2 == 0;
        dc.DrawRoundedRectangle(brake ? (flash ? Theme.Red : Theme.B("#FF4D5E", 0.6)) : Theme.PanelRow, null, br, 6, 6);
        DrawText(dc, "BRAKE", br.X + br.Width / 2, br.Y + 10, 17, brake ? Theme.Text : Theme.Dim, Theme.UiBold, 1);
        if (float.IsFinite(dist) && dist > 0 && dist < 400) Num(dc, $"{dist:0} m", BaseWidth / 2, BaseHeight - 18, 11, Theme.Muted, 1);
    }
}
