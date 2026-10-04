using System.Windows;
using System.Windows.Media;
using RacingHelper.Live;

namespace RacingHelper.App.Overlays;

/// <summary>Fuel usage, laps in tank, fuel to finish / to add.</summary>
public sealed class FuelOverlay : OverlayView
{
    public override string Id => "fuel";
    public override double BaseWidth => 300;
    public override double BaseHeight => 190;

    protected override void Draw(DrawingContext dc, LiveState s)
    {
        var f = s.Fuel;
        string laps = s.SessionLapsTotal > 0 ? $"Lap {s.Lap}/{s.SessionLapsTotal}" : $"Lap {s.Lap}";
        Header(dc, "Fuel  ·  " + laps);
        double level = f?.FuelLevel ?? double.NaN;
        Num(dc, double.IsFinite(level) ? $"{level:0.0} L" : "–", BaseWidth - 10, 4, 12, Theme.Text, 2);
        if (s.TankCapacity > 0 && double.IsFinite(level)) Bar(dc, new Rect(10, 22, BaseWidth - 20, 4), level / s.TankCapacity, level / s.TankCapacity < 0.15 ? Theme.Red : Theme.Accent);

        DrawText(dc, "PREDICTED / LAP", 10, 32, 9, Theme.Muted, Theme.UiBold);
        Num(dc, f != null && double.IsFinite(f.PerLapPredicted) ? $"{f.PerLapPredicted:0.00} L" : "–", 10, 43, 22, Theme.Text);
        DrawText(dc, "LAPS IN TANK", BaseWidth - 10, 32, 9, Theme.Muted, Theme.UiBold, 2);
        Num(dc, f != null && double.IsFinite(f.LapsInTank) ? $"{f.LapsInTank:0.0}" : "–", BaseWidth - 10, 43, 22, f != null && f.LapsInTank < 2 ? Theme.Red : Theme.Text, 2);

        double y = 78;
        DrawText(dc, "USAGE/LAP", 90, y, 8.5, Theme.Muted, Theme.UiBold);
        DrawText(dc, "ENDS LAP", 190, y, 8.5, Theme.Muted, Theme.UiBold);
        y += 13;
        void Row(string label, double usage)
        {
            DrawText(dc, label, 10, y + 1, 10, Theme.Muted, Theme.UiBold);
            Num(dc, double.IsFinite(usage) ? usage.ToString("0.00") : "–", 90, y, 12, Theme.Text);
            Num(dc, double.IsFinite(usage) && usage > 0 && double.IsFinite(level) ? (s.Lap + level / usage).ToString("0.0") : "–", 190, y, 12, Theme.Text);
            y += 17;
        }
        Row("LAST", f?.PerLapLast ?? double.NaN);
        Row("5 LAP", f?.PerLapAvg ?? double.NaN);
        Row("MAX", f?.PerLapMax ?? double.NaN);

        dc.DrawLine(Theme.P(Theme.Line, 1), new Point(10, y + 2), new Point(BaseWidth - 10, y + 2));
        y += 8;
        if (f != null && double.IsFinite(f.FuelToFinish))
        {
            DrawText(dc, "TO FINISH", 10, y + 2, 10, Theme.Muted, Theme.UiBold);
            Num(dc, $"{f.FuelToFinish:0.0} L", 90, y, 14, Theme.Text);
            if (f.FuelToAdd > 0.1) Num(dc, $"ADD {Math.Ceiling(f.FuelToAdd):0} L", BaseWidth - 10, y, 14, Theme.Yellow, 2);
            else Num(dc, $"+{f.Spare:0.0} L spare", BaseWidth - 10, y, 13, Theme.Green, 2);
        }
        else DrawText(dc, "Fuel to finish appears in timed / lap races.", 10, y + 2, 10, Theme.Dim);
    }
}

/// <summary>Four tyres: inner / middle / outer temps, pressure and wear.</summary>
public sealed class TyresOverlay : OverlayView
{
    public override string Id => "tyres";
    public override double BaseWidth => 300;
    public override double BaseHeight => 200;

    protected override void Draw(DrawingContext dc, LiveState s)
    {
        bool carcass = s.Tyres.Any(t => t.Carcass);
        Header(dc, carcass ? "Tyres · last pit reading" : "Tyres");
        if (s.Tyres.Count < 4) return;
        var target = S.TyreTargetFor(s.CarPath, RacingHelper.Sim.SessionInfo.CarCategoryFor(s.CarPath, s.CarClass, ""));
        double w = (BaseWidth - 30) / 2, h = (BaseHeight - 30) / 2;
        for (int i = 0; i < 4; i++)
        {
            var t = s.Tyres[i];
            double x = 10 + (i % 2) * (w + 10), y = 22 + (i / 2) * (h + 4);
            bool left = i % 2 == 0;
            // temperature strip: outer edge on the outside of the car
            float[] temps = left ? new[] { t.TempOut, t.TempMid, t.TempIn } : new[] { t.TempIn, t.TempMid, t.TempOut };
            double sw = (w - 8) / 3;
            for (int k = 0; k < 3; k++)
            {
                var r = new Rect(x + k * (sw + 4), y, sw, 30);
                dc.DrawRoundedRectangle(Theme.TempBrush(temps[k], target.TempMin, target.TempMax), null, r, 3, 3);
                Num(dc, Temp(temps[k]), r.X + r.Width / 2, r.Y + 7, 11, Theme.B("#0E1116"), 1);
            }
            DrawText(dc, t.Name, x, y + 36, 10, Theme.Muted, Theme.UiBold);
            string press = float.IsFinite(t.Pressure) && t.Pressure > 0 ? Pressure(t.Pressure) : Pressure(t.ColdPressure) + (float.IsFinite(t.ColdPressure) ? " c" : "");
            Brush pb = t.PressStatus switch { "low" => Theme.Blue, "high" => Theme.Red, "ok" => Theme.Green, _ => Theme.Text };
            Num(dc, press, x + 26, y + 34, 14, pb);
            DrawText(dc, S.PressureUnit, x + 26 + Fmt(press, 14, pb, Theme.Num).Width + 3, y + 38, 8.5, Theme.Muted);
            if (float.IsFinite(t.Wear)) Num(dc, $"{t.Wear * 100:0}%", x + w, y + 34, 12, t.Wear < 0.5 ? Theme.Yellow : Theme.Muted, 2);
        }
    }
}

/// <summary>Current weather conditions.</summary>
public sealed class WeatherOverlay : OverlayView
{
    public override string Id => "weather";
    public override double BaseWidth => 260;
    public override double BaseHeight => 120;

    protected override void Draw(DrawingContext dc, LiveState s)
    {
        var w = s.Weather;
        Header(dc, "Weather");
        DrawText(dc, $"{w.Skies}{(w.DeclaredWet ? " · WET declared" : "")}", BaseWidth - 10, 6, 9.5, w.DeclaredWet ? Theme.Blue : Theme.Muted, Theme.UiBold, 2);
        DrawText(dc, "AIR", 10, 26, 9, Theme.Muted, Theme.UiBold);
        Num(dc, Temp(w.AirTemp), 10, 37, 20, Theme.Text);
        DrawText(dc, "TRACK", 80, 26, 9, Theme.Muted, Theme.UiBold);
        Num(dc, Temp(w.TrackTemp), 80, 37, 20, Theme.Text);
        if (Math.Abs(w.TrackTempTrend) > 0.5f) Num(dc, $"{(w.TrackTempTrend > 0 ? "↑" : "↓")}{Math.Abs(w.TrackTempTrend):0}/h", 128, 44, 10, w.TrackTempTrend > 0 ? Theme.Orange : Theme.Blue);
        DrawText(dc, "TRACK STATE", 170, 26, 9, Theme.Muted, Theme.UiBold);
        DrawText(dc, string.IsNullOrEmpty(w.Wetness) || w.Wetness == "—" ? "–" : w.Wetness, 170, 40, 11, w.WetnessLevel >= 3 ? Theme.Blue : Theme.Text, Theme.UiBold);
        double y = 72;
        DrawText(dc, "RAIN", 10, y, 9, Theme.Muted, Theme.UiBold);
        Num(dc, $"{w.Precipitation * 100:0}%", 10, y + 12, 13, w.Precipitation > 0.05 ? Theme.Blue : Theme.Text);
        DrawText(dc, "HUMIDITY", 80, y, 9, Theme.Muted, Theme.UiBold);
        Num(dc, $"{w.Humidity * 100:0}%", 80, y + 12, 13, Theme.Text);
        DrawText(dc, "WIND", 170, y, 9, Theme.Muted, Theme.UiBold);
        double kmh = w.WindSpeed * 3.6;
        Num(dc, $"{SpeedConv(kmh):0} {(S.SpeedUnit == "mph" ? "mph" : "km/h")}", 170, y + 12, 13, Theme.Text);
        // wind direction arrow (direction the wind blows from)
        double ax = BaseWidth - 22, ay = y + 18;
        dc.PushTransform(new RotateTransform(w.WindDir * 180 / Math.PI, ax, ay));
        dc.DrawLine(Theme.P(Theme.Muted, 2), new Point(ax, ay - 9), new Point(ax, ay + 9));
        dc.DrawLine(Theme.P(Theme.Muted, 2), new Point(ax, ay + 9), new Point(ax - 4, ay + 4));
        dc.DrawLine(Theme.P(Theme.Muted, 2), new Point(ax, ay + 9), new Point(ax + 4, ay + 4));
        dc.Pop();
    }
}

/// <summary>Projected (trend-based) conditions for the next hour.</summary>
public sealed class WeatherForecastOverlay : OverlayView
{
    public override string Id => "weather-forecast";
    public override double BaseWidth => 300;
    public override double BaseHeight => 140;

    protected override void Draw(DrawingContext dc, LiveState s)
    {
        Header(dc, "Forecast · projected from trend");
        var w = s.Weather;
        if (w.Forecast.Count == 0) { DrawText(dc, "Collecting a few minutes of data…", 10, 30, 11, Theme.Muted); return; }
        double colW = (BaseWidth - 20) / (w.Forecast.Count + 1);
        void Col(int i, string when, float track, float wet, float rain)
        {
            double x = 10 + i * colW + colW / 2;
            DrawText(dc, when, x, 26, 9.5, Theme.Muted, Theme.UiBold, 1);
            Num(dc, Temp(track), x, 42, 15, Theme.Text, 1);
            string state = wet >= 3 ? "WET" : wet >= 2 ? "DAMP" : "DRY";
            DrawText(dc, state, x, 66, 10, wet >= 3 ? Theme.Blue : Theme.Text, Theme.UiBold, 1);
            Bar(dc, new Rect(x - colW / 2 + 8, 88, colW - 16, 30), rain, Theme.Blue, true);
            DrawText(dc, $"{rain * 100:0}%", x, 120, 9, Theme.Muted, Theme.Ui, 1);
        }
        Col(0, "NOW", w.TrackTemp, w.WetnessLevel, w.Precipitation);
        for (int i = 0; i < w.Forecast.Count; i++)
        {
            var p = w.Forecast[i];
            Col(i + 1, $"+{p.Minutes}m", p.TrackTemp, p.Wetness, p.Precip);
        }
    }
}

/// <summary>Repair times (iRacing's damage indicator), warnings, incidents and pace loss after contact.</summary>
public sealed class DamageOverlay : OverlayView
{
    public override string Id => "damage";
    public override double BaseWidth => 280;
    public override double BaseHeight => 150;

    protected override void Draw(DrawingContext dc, LiveState s)
    {
        var h = s.Health;
        Header(dc, "Car health");
        string inc = h.IncidentLimit > 0 ? $"{h.Incidents}x / {h.IncidentLimit}" : $"{h.Incidents}x";
        Num(dc, inc, BaseWidth - 10, 4, 12, h.IncidentLimit > 0 && h.Incidents >= h.IncidentLimit * 0.75 ? Theme.Red : Theme.Text, 2);
        bool damaged = h.RepairLeft > 0.5f || h.OptRepairLeft > 0.5f || h.RepairFlag;
        var box = new Rect(10, 24, BaseWidth - 20, 40);
        dc.DrawRoundedRectangle(damaged ? Theme.B("#FF4D5E", 0.25) : Theme.PanelRow, null, box, 4, 4);
        if (damaged)
        {
            DrawText(dc, h.RepairFlag ? "REPAIR REQUIRED (meatball)" : "DAMAGE", 18, 28, 10, Theme.Red, Theme.UiBold);
            DrawText(dc, $"Mandatory {h.RepairLeft:0}s   ·   Optional {h.OptRepairLeft:0}s", 18, 44, 11, Theme.Text);
        }
        else DrawText(dc, h.PossibleDamage ? $"Pace down {h.PaceLossPct:0.0}% since contact — possible damage" : "No damage reported", 18, 36, 11, h.PossibleDamage ? Theme.Yellow : Theme.Green, Theme.UiBold, 0, box.Width - 16);
        double y = 74;
        void Stat(double x, string label, string value, Brush b)
        {
            DrawText(dc, label, x, y, 9, Theme.Muted, Theme.UiBold);
            Num(dc, value, x, y + 11, 13, b);
        }
        Stat(10, "WATER", Temp(h.WaterTemp), h.Warnings.Any(x => x.Contains("water")) ? Theme.Red : Theme.Text);
        Stat(80, "OIL", Temp(h.OilTemp), h.Warnings.Any(x => x.Contains("oil temp")) ? Theme.Red : Theme.Text);
        Stat(150, "OIL P", float.IsFinite(h.OilPress) ? $"{h.OilPress:0.0}" : "–", h.Warnings.Any(x => x.Contains("oil pressure")) ? Theme.Red : Theme.Text);
        Stat(215, "VOLT", float.IsFinite(h.Voltage) ? $"{h.Voltage:0.0}" : "–", Theme.Text);
        if (h.Warnings.Count > 0) DrawText(dc, "⚠ " + string.Join(", ", h.Warnings), 10, BaseHeight - 20, 10, Theme.Red, Theme.UiBold, 0, BaseWidth - 20);
    }
}

/// <summary>The race engineer's latest messages.</summary>
public sealed class EngineerOverlay : OverlayView
{
    public override string Id => "engineer";
    public override double BaseWidth => 380;
    public override double BaseHeight => 150;

    protected override void Draw(DrawingContext dc, LiveState s)
    {
        Header(dc, "Race engineer");
        double y = 22;
        var msgs = s.Messages.Where(m => m.Category != "info" || m.Priority > 0 || m.Speak).TakeLast(5).Reverse().ToList();
        if (msgs.Count == 0) msgs = s.Messages.TakeLast(5).Reverse().ToList();
        foreach (var m in msgs)
        {
            double age = (DateTime.Now - m.At).TotalSeconds;
            double fade = age < 20 ? 1 : age < 60 ? 0.7 : 0.45;
            Brush b = m.Priority >= 3 ? Theme.Red : m.Category switch
            {
                "pb" => Theme.Purple, "fuel" => Theme.Yellow, "flag" => Theme.Yellow, "warning" => Theme.Orange, "corner" => Theme.Blue, _ => Theme.Text,
            };
            dc.PushOpacity(fade);
            dc.DrawRectangle(b, null, new Rect(10, y + 3, 3, 13));
            DrawText(dc, m.Text, 18, y + 1, 11.5, Theme.Text, Theme.Ui, 0, BaseWidth - 70);
            DrawText(dc, age < 60 ? $"{age:0}s" : $"{age / 60:0}m", BaseWidth - 10, y + 2, 9.5, Theme.Muted, Theme.Ui, 2);
            dc.Pop();
            y += 24;
            if (y > BaseHeight - 20) break;
        }
    }
}
