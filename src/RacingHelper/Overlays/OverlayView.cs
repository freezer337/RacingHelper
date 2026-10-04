using System.Globalization;
using System.Windows;
using System.Windows.Media;
using RacingHelper.Live;

namespace RacingHelper.App.Overlays;

/// <summary>Colours / fonts shared by every overlay.</summary>
public static class Theme
{
    // must be initialised before the brushes below (static fields initialise in declaration order)
    static readonly Dictionary<string, Brush> _cache = new();

    public static readonly Brush Text = B("#F3F6FA");
    public static readonly Brush Muted = B("#8A94A6");
    public static readonly Brush Dim = B("#4A5262");
    public static readonly Brush Line = B("#2A303A");
    public static readonly Brush Accent = B("#22D37E");
    public static readonly Brush Green = B("#22D37E");
    public static readonly Brush Red = B("#FF4D5E");
    public static readonly Brush Yellow = B("#F5C542");
    public static readonly Brush Purple = B("#B26BFF");
    public static readonly Brush Blue = B("#4DA3FF");
    public static readonly Brush Orange = B("#FF9640");
    public static readonly Brush Track = B("#3A4250");
    public static readonly Brush PanelRow = B("#FFFFFF", 0.04);
    public static readonly Brush PlayerRow = B("#22D37E", 0.16);

    public static readonly Typeface Ui = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    public static readonly Typeface UiBold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    public static readonly Typeface Num = new(new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    public static readonly Typeface NumLight = new(new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public static Brush B(string hex, double alpha = 1)
    {
        var key = hex + alpha.ToString("0.00", CultureInfo.InvariantCulture);
        if (_cache.TryGetValue(key, out var b)) return b;
        var c = (Color)ColorConverter.ConvertFromString(hex);
        c.A = (byte)(255 * alpha);
        b = new SolidColorBrush(c);
        b.Freeze();
        _cache[key] = b;
        return b;
    }

    public static Pen P(Brush b, double w, bool dash = false)
    {
        var p = new Pen(b, w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        if (dash) p.DashStyle = new DashStyle(new double[] { 3, 3 }, 0);
        p.Freeze();
        return p;
    }

    /// <summary>Blue (cold) → green (in window) → red (hot) for tyre temps.</summary>
    public static Brush TempBrush(float t, float min, float max)
    {
        if (!float.IsFinite(t)) return Dim;
        if (t < min) return B("#4DA3FF", 0.9);
        if (t > max) return B("#FF4D5E", 0.9);
        return B("#22D37E", 0.9);
    }
}

public sealed class OverlayContext
{
    public required AppSettings Settings { get; init; }
    public required TelemetryHub Hub { get; init; }
    public bool EditMode { get; set; }
    public double Opacity { get; set; } = 0.92;
}

/// <summary>Base class: overlays draw themselves with immediate-mode WPF drawing at the overlay frame rate.</summary>
public abstract class OverlayView : FrameworkElement
{
    public abstract string Id { get; }
    public abstract double BaseWidth { get; }
    public abstract double BaseHeight { get; }
    public LiveState? State { get; set; }
    public OverlayContext Ctx { get; set; } = null!;

    double _ppd = 1;
    readonly Dictionary<(string, double, Brush, Typeface), FormattedText> _textCache = new();

    protected OverlayView()
    {
        Loaded += (_, _) => _ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        SnapsToDevicePixels = true;
    }

    protected AppSettings S => Ctx.Settings;

    protected override void OnRender(DrawingContext dc)
    {
        var s = State;
        var r = new Rect(0, 0, BaseWidth, BaseHeight);
        if (s == null) return;
        if (Background) dc.DrawRoundedRectangle(Theme.B("#0E1116", Ctx.Opacity), null, r, 8, 8);
        try { Draw(dc, s); }
        catch (Exception ex) { DrawText(dc, "render error: " + ex.Message, 8, 8, 10, Theme.Red); }
        if (Ctx.EditMode)
        {
            dc.DrawRoundedRectangle(null, Theme.P(Theme.Accent, 1.5, dash: true), r, 8, 8);
            var title = Fmt(OverlayNames.Name(Id), 10, Theme.Accent, Theme.UiBold);
            dc.DrawRectangle(Theme.B("#0E1116", 0.9), null, new Rect(6, BaseHeight - 18, title.Width + 8, 15));
            dc.DrawText(title, new Point(10, BaseHeight - 17));
        }
    }

    /// <summary>Draw a translucent panel behind the overlay (some overlays like the radar want none).</summary>
    protected virtual bool Background => true;

    protected abstract void Draw(DrawingContext dc, LiveState s);

    // ------------------------------------------------------------- text helpers

    protected FormattedText Fmt(string text, double size, Brush brush, Typeface? face = null)
    {
        face ??= Theme.Ui;
        var key = (text, size, brush, face);
        if (_textCache.TryGetValue(key, out var ft)) return ft;
        if (_textCache.Count > 600) _textCache.Clear();
        ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush, _ppd);
        _textCache[key] = ft;
        return ft;
    }

    /// <summary>align: 0 left, 1 centre, 2 right (x is the anchor).</summary>
    protected double DrawText(DrawingContext dc, string text, double x, double y, double size, Brush brush, Typeface? face = null, int align = 0, double maxWidth = 0, int maxLines = 1)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        FormattedText ft;
        if (maxWidth > 0)
        {
            ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face ?? Theme.Ui, size, brush, _ppd)
            { MaxTextWidth = maxWidth, MaxTextHeight = maxLines * size * 1.4, Trimming = TextTrimming.CharacterEllipsis };
        }
        else ft = Fmt(text, size, brush, face);
        double dx = align == 1 ? -ft.Width / 2 : align == 2 ? -ft.Width : 0;
        dc.DrawText(ft, new Point(x + dx, y));
        return ft.Width;
    }

    protected double Num(DrawingContext dc, string text, double x, double y, double size, Brush brush, int align = 0)
        => DrawText(dc, text, x, y, size, brush, Theme.Num, align);

    protected void Header(DrawingContext dc, string text, double x = 10, double y = 6)
        => DrawText(dc, text.ToUpperInvariant(), x, y, 9.5, Theme.Muted, Theme.UiBold);

    // ------------------------------------------------------------- shapes

    protected static void Bar(DrawingContext dc, Rect r, double frac, Brush fill, bool vertical = false, Brush? bg = null)
    {
        dc.DrawRoundedRectangle(bg ?? Theme.PanelRow, null, r, 2, 2);
        if (!double.IsFinite(frac)) return;
        frac = Math.Clamp(frac, 0, 1);
        var f = vertical ? new Rect(r.X, r.Bottom - r.Height * frac, r.Width, r.Height * frac) : new Rect(r.X, r.Y, r.Width * frac, r.Height);
        if (f.Width > 0 && f.Height > 0) dc.DrawRoundedRectangle(fill, null, f, 2, 2);
    }

    /// <summary>Polyline of normalised values (0..1 → bottom..top) across a rectangle.</summary>
    protected static void Trace(DrawingContext dc, float[] data, Rect r, Pen pen, double min = 0, double max = 1)
    {
        if (data.Length < 2) return;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            bool started = false;
            for (int i = 0; i < data.Length; i++)
            {
                float v = data[i];
                if (!float.IsFinite(v)) { started = false; continue; }
                double x = r.X + r.Width * i / (data.Length - 1);
                double y = r.Bottom - r.Height * Math.Clamp((v - min) / (max - min), 0, 1);
                if (!started) { ctx.BeginFigure(new Point(x, y), false, false); started = true; }
                else ctx.LineTo(new Point(x, y), true, true);
            }
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }

    // ------------------------------------------------------------- formatting / units

    protected static string LapTime(double s)
    {
        if (!double.IsFinite(s) || s <= 0) return "–:––.–––";
        int m = (int)(s / 60);
        double r = s - m * 60;
        return $"{m}:{r.ToString("00.000", CultureInfo.InvariantCulture)}";
    }

    protected static string Delta(double d, string fmt = "0.000")
        => !double.IsFinite(d) ? "–" : (d >= 0 ? "+" : "−") + Math.Abs(d).ToString(fmt, CultureInfo.InvariantCulture);

    protected static Brush DeltaBrush(double d) => !double.IsFinite(d) ? Theme.Muted : d <= 0 ? Theme.Green : Theme.Red;

    protected string Speed(double ms) => !double.IsFinite(ms) ? "–" : S.SpeedUnit == "mph" ? (ms * 2.23694).ToString("0") : (ms * 3.6).ToString("0");
    protected string SpeedUnit => S.SpeedUnit == "mph" ? "MPH" : "KM/H";
    protected double SpeedConv(double kmh) => S.SpeedUnit == "mph" ? kmh / 1.609344 : kmh;

    protected string Pressure(double kpa)
    {
        if (!double.IsFinite(kpa) || kpa <= 0) return "–";
        return S.PressureUnit switch
        {
            "psi" => (kpa * 0.1450377).ToString("0.0", CultureInfo.InvariantCulture),
            "bar" => (kpa / 100).ToString("0.00", CultureInfo.InvariantCulture),
            _ => kpa.ToString("0", CultureInfo.InvariantCulture),
        };
    }

    protected string Temp(double c) => !double.IsFinite(c) ? "–" : S.TempUnit == "F" ? (c * 9 / 5 + 32).ToString("0") + "°" : c.ToString("0") + "°";

    protected static string Pct(double v) => double.IsFinite(v) ? (v * 100).ToString("0") + "%" : "–";
}

public static class OverlayNames
{
    public static string Name(string id) => RacingHelper.Web.OverlayCatalog.All.FirstOrDefault(o => o.Id == id)?.Name ?? id;
}
