using System.Windows;
using System.Windows.Media;
using RacingHelper.Live;
using RacingHelper.Sim;

namespace RacingHelper.App.Overlays;

/// <summary>Race order with gap / interval / last / best, iRating with projected change, grouped by class.</summary>
public sealed class StandingsOverlay : OverlayView
{
    public override string Id => "standings";
    public override double BaseWidth => 520;
    public override double BaseHeight => 360;

    protected override void Draw(DrawingContext dc, LiveState s)
    {
        string remain = s.SessionLapsRemain > 0 ? $"{s.SessionLapsRemain} laps left"
                      : double.IsFinite(s.SessionTimeRemain) && s.SessionTimeRemain > 0 && s.SessionTimeRemain < 86400 ? TimeSpan.FromSeconds(s.SessionTimeRemain).ToString(@"h\:mm\:ss") : "";
        Header(dc, $"{s.SessionType}  ·  Lap {s.Lap}{(remain.Length > 0 ? "  ·  " + remain : "")}");
        if (s.Standings.Count == 0) { DrawText(dc, "Standings appear in a live session.", 10, 30, 12, Theme.Muted); return; }

        double y = 24, rowH = 19;
        int maxRows = (int)((BaseHeight - y - 6) / rowH);
        // columns
        double cPos = 10, cNum = 34, cName = 72, cIr = 250, cGap = 330, cInt = 385, cLast = 455, cTyre = 505;
        foreach (var cls in s.Standings.GroupBy(r => r.ClassName))
        {
            var rows = cls.ToList();
            if (y + rowH * 2 > BaseHeight) break;
            int sof = s.ClassSof.TryGetValue(cls.Key, out var v) ? v : 0;
            var color = Theme.B(rows[0].ClassColor);
            dc.DrawRectangle(color, null, new Rect(6, y + 3, 3, 12));
            DrawText(dc, $"{cls.Key}  ·  SoF {sof / 1000.0:0.0}k  ·  {rows.Count} cars", 14, y + 2, 9.5, Theme.Muted, Theme.UiBold);
            y += rowH;
            int room = Math.Max(3, maxRows - (int)((y - 24) / rowH) - (s.Standings.GroupBy(r => r.ClassName).Count() > 1 ? 2 : 0));
            var shown = PickRows(rows, room);
            foreach (var r in shown)
            {
                if (y + rowH > BaseHeight - 4) break;
                if (r == null) { DrawText(dc, "···", cName, y, 10, Theme.Dim); y += rowH * 0.7; continue; }
                if (r.IsPlayer) dc.DrawRoundedRectangle(Theme.PlayerRow, null, new Rect(6, y - 1, BaseWidth - 12, rowH - 1), 3, 3);
                var dim = r.InPit ? Theme.Muted : Theme.Text;
                Num(dc, r.ClassPosition.ToString(), cPos + 14, y + 1, 12, dim, 2);
                dc.DrawRoundedRectangle(Theme.B(r.ClassColor, 0.85), null, new Rect(cNum, y + 1, 30, 15), 3, 3);
                Num(dc, r.Number, cNum + 15, y + 1, 11, Theme.B("#0E1116"), 1);
                DrawText(dc, r.Name, cName, y + 1, 11.5, dim, r.IsPlayer ? Theme.UiBold : Theme.Ui, 0, cIr - cName - 8);
                if (r.IRating > 0) Num(dc, $"{r.IRating / 1000.0:0.0}k", cIr, y + 1, 11, Theme.Muted);
                if (double.IsFinite(r.IRatingChange)) Num(dc, (r.IRatingChange >= 0 ? "+" : "−") + Math.Abs(r.IRatingChange).ToString("0"), cIr + 34, y + 1, 11, r.IRatingChange >= 0 ? Theme.Green : Theme.Red);
                Num(dc, r.ClassPosition == 1 ? "LEADER" : Gap(r.Gap), cGap + 40, y + 1, 11, Theme.Muted, 2);
                Num(dc, r.ClassPosition == 1 ? "" : Gap(r.Interval), cInt + 40, y + 1, 11, Theme.Text, 2);
                Num(dc, LapTime(r.LastLap), cLast + 40, y + 1, 11, double.IsFinite(r.LastLap) && Math.Abs(r.LastLap - r.BestLap) < 0.001 ? Theme.Purple : Theme.Text, 2);
                if (r.InPit) DrawText(dc, "PIT", cTyre, y + 2, 9, Theme.Yellow, Theme.UiBold);
                else DrawText(dc, r.Tyre, cTyre, y + 2, 9.5, Theme.Muted, Theme.UiBold);
                y += rowH;
            }
            y += 4;
        }
    }

    /// <summary>Top of the class plus the rows around the player (null = gap marker).</summary>
    static List<CarRow?> PickRows(List<CarRow> rows, int room)
    {
        if (rows.Count <= room) return rows.Cast<CarRow?>().ToList();
        int me = rows.FindIndex(r => r.IsPlayer);
        if (me < 0 || me < room - 1) return rows.Take(room).Cast<CarRow?>().ToList();
        int top = Math.Max(1, room / 3);
        int around = room - top - 1;
        int from = Math.Clamp(me - around / 2, top, rows.Count - around);
        var res = rows.Take(top).Cast<CarRow?>().ToList();
        res.Add(null);
        res.AddRange(rows.Skip(from).Take(around));
        return res;
    }

    static string Gap(double g) => !double.IsFinite(g) ? "" : g >= 60 ? $"{(int)(g / 60)}:{g % 60:00.0}" : g.ToString("0.0");
}

/// <summary>Cars around you on track, with relative time and lap status colouring.</summary>
public sealed class RelativeOverlay : OverlayView
{
    public override string Id => "relative";
    public override double BaseWidth => 400;
    public override double BaseHeight => 260;

    protected override void Draw(DrawingContext dc, LiveState s)
    {
        Header(dc, "Relative");
        if (s.Position > 0) DrawText(dc, $"P{s.ClassPosition} / {s.CarsInClass}", BaseWidth - 10, 6, 9.5, Theme.Muted, Theme.UiBold, 2);
        if (s.Relative.Count == 0) { DrawText(dc, "Relatives appear in a live session.", 10, 30, 12, Theme.Muted); return; }
        double y = 24, rowH = 25;
        foreach (var r in s.Relative)
        {
            if (y + rowH > BaseHeight) break;
            if (r.IsPlayer) dc.DrawRoundedRectangle(Theme.PlayerRow, null, new Rect(6, y, BaseWidth - 12, rowH - 3), 4, 4);
            // lapping you = red, lapped = blue, same lap = white
            Brush nameBrush = r.IsPlayer ? Theme.Text : r.LapDiff > 0 ? Theme.B("#FF7A85") : r.LapDiff < 0 ? Theme.B("#6FB6FF") : Theme.Text;
            if (r.InPit) nameBrush = Theme.Muted;
            Num(dc, r.ClassPosition > 0 ? r.ClassPosition.ToString() : "", 30, y + 4, 13, Theme.Muted, 2);
            dc.DrawRoundedRectangle(Theme.B(r.ClassColor, 0.85), null, new Rect(38, y + 3, 34, 17), 3, 3);
            Num(dc, r.Number, 55, y + 4, 12, Theme.B("#0E1116"), 1);
            DrawText(dc, r.Name, 80, y + 4, 12.5, nameBrush, r.IsPlayer ? Theme.UiBold : Theme.Ui, 0, 170);
            if (!string.IsNullOrEmpty(r.License))
            {
                var lic = r.License.Split(' ')[0];
                dc.DrawRoundedRectangle(Theme.B(r.LicenseColor, 0.8), null, new Rect(258, y + 5, 40, 14), 3, 3);
                DrawText(dc, $"{lic} {r.IRating / 1000.0:0.0}k", 278, y + 5, 9, Theme.Text, Theme.UiBold, 1);
            }
            if (r.InPit) DrawText(dc, "PIT", 306, y + 6, 9, Theme.Yellow, Theme.UiBold);
            if (!r.IsPlayer && double.IsFinite(r.Relative))
                Num(dc, (r.Relative >= 0 ? "+" : "−") + Math.Abs(r.Relative).ToString("0.0"), BaseWidth - 12, y + 4, 13, nameBrush, 2);
            y += rowH;
        }
    }
}

/// <summary>Proximity radar: cars alongside (from the spotter) and close ahead / behind.</summary>
public sealed class RadarOverlay : OverlayView
{
    public override string Id => "radar";
    public override double BaseWidth => 200;
    public override double BaseHeight => 240;
    protected override bool Background => Ctx.EditMode;

    protected override void Draw(DrawingContext dc, LiveState s)
    {
        double cx = BaseWidth / 2, cy = BaseHeight / 2;
        double scale = (BaseHeight / 2 - 10) / 28.0; // px per metre (cars further than ~28 m fall off the edge)
        double carW = 1.9 * scale * 1.6, carL = 4.6 * scale;
        int lr = s.CarLeftRight;
        bool left = lr is Irsdk.LeftRight.CarLeft or Irsdk.LeftRight.CarsBothSides or Irsdk.LeftRight.TwoCarsLeft;
        bool right = lr is Irsdk.LeftRight.CarRight or Irsdk.LeftRight.CarsBothSides or Irsdk.LeftRight.TwoCarsRight;
        bool any = left || right || s.Radar.Any(r => Math.Abs(r.Dist) < 20);
        if (!any && !Ctx.EditMode) return;

        dc.DrawEllipse(Theme.B("#0E1116", 0.55 * Ctx.Opacity), Theme.P(Theme.Line, 1), new Point(cx, cy), BaseWidth / 2 - 4, BaseHeight / 2 - 4);
        dc.DrawLine(Theme.P(Theme.Line, 1), new Point(cx, 10), new Point(cx, BaseHeight - 10));
        dc.PushClip(new EllipseGeometry(new Point(cx, cy), BaseWidth / 2 - 4, BaseHeight / 2 - 4));
        foreach (var r in s.Radar)
        {
            double y = cy - r.Dist * scale;
            bool close = Math.Abs(r.Dist) < 6;
            var b = close ? Theme.Red : Math.Abs(r.Dist) < 15 ? Theme.Yellow : Theme.B(r.ClassColor, 0.8);
            // cars beside us are drawn in the side lanes by the left/right indicator below
            if (close && (left || right)) continue;
            dc.DrawRoundedRectangle(b, null, new Rect(cx - carW / 2, y - carL / 2, carW, carL), 3, 3);
        }
        void Side(double x, int count)
        {
            for (int i = 0; i < count; i++)
                dc.DrawRoundedRectangle(Theme.Red, null, new Rect(x - carW / 2 + i * (carW + 3) * Math.Sign(x - cx), cy - carL / 2, carW, carL), 3, 3);
        }
        if (left) Side(cx - carW - 6, lr == Irsdk.LeftRight.TwoCarsLeft ? 2 : 1);
        if (right) Side(cx + carW + 6, lr == Irsdk.LeftRight.TwoCarsRight ? 2 : 1);
        dc.DrawRoundedRectangle(Theme.Accent, null, new Rect(cx - carW / 2, cy - carL / 2, carW, carL), 3, 3);
        dc.Pop();
        if (left) DrawText(dc, "◀ CAR LEFT", 8, BaseHeight - 18, 9, Theme.Red, Theme.UiBold);
        if (right) DrawText(dc, "CAR RIGHT ▶", BaseWidth - 8, BaseHeight - 18, 9, Theme.Red, Theme.UiBold, 2);
    }
}
