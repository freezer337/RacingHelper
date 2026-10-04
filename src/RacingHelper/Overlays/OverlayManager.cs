using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using RacingHelper.Live;

namespace RacingHelper.App.Overlays;

/// <summary>Creates / destroys overlay windows from settings and drives their redraw at the configured frame rate.</summary>
public sealed class OverlayManager : IDisposable
{
    readonly TelemetryHub _hub;
    readonly SettingsStore _settings;
    readonly Dictionary<string, OverlayWindow> _windows = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    long _lastFrameMs, _lastTopMs;
    bool _edit, _hidden;

    static readonly Dictionary<string, Func<OverlayView>> Factory = new()
    {
        ["inputs"] = () => new InputsOverlay(),
        ["input-telemetry"] = () => new InputTelemetryOverlay(),
        ["input-compare"] = () => new InputCompareOverlay(),
        ["speed-compare"] = () => new SpeedCompareOverlay(),
        ["brake-indicator"] = () => new BrakeIndicatorOverlay(),
        ["delta-sectors"] = () => new DeltaSectorsOverlay(),
        ["delta-bar"] = () => new DeltaBarOverlay(),
        ["standings"] = () => new StandingsOverlay(),
        ["relative"] = () => new RelativeOverlay(),
        ["comparison-target"] = () => new ComparisonTargetOverlay(),
        ["corner-analysis"] = () => new CornerAnalysisOverlay(),
        ["track-map"] = () => new TrackMapOverlay(),
        ["mini-map"] = () => new MiniMapOverlay(),
        ["fuel"] = () => new FuelOverlay(),
        ["tyres"] = () => new TyresOverlay(),
        ["weather"] = () => new WeatherOverlay(),
        ["radar"] = () => new RadarOverlay(),
        ["damage"] = () => new DamageOverlay(),
        ["line-compare"] = () => new LineCompareOverlay(),
        ["weather-forecast"] = () => new WeatherForecastOverlay(),
        ["engineer"] = () => new EngineerOverlay(),
    };

    public OverlayManager(TelemetryHub hub, SettingsStore settings)
    {
        _hub = hub;
        _settings = settings;
        _settings.Changed += _ => Application.Current.Dispatcher.InvokeAsync(Sync);
    }

    public bool EditMode
    {
        get => _edit;
        set
        {
            _edit = value;
            foreach (var w in _windows.Values)
            {
                w.SetClickThrough(!value);
                w.View.Ctx.EditMode = value;
            }
            if (value) _hidden = false;
            UpdateVisibility(_hub.State);
        }
    }

    public bool Hidden
    {
        get => _hidden;
        set { _hidden = value; UpdateVisibility(_hub.State); }
    }

    public void Start()
    {
        Sync();
        CompositionTarget.Rendering += OnRendering;
    }

    /// <summary>Reconciles open windows with the enabled overlays in settings.</summary>
    public void Sync()
    {
        var s = _settings.Current;
        foreach (var info in RacingHelper.Web.OverlayCatalog.All)
        {
            var cfg = s.Overlays.FirstOrDefault(o => o.Id == info.Id);
            bool enabled = cfg?.Enabled ?? info.DefaultOn;
            if (enabled && !_windows.ContainsKey(info.Id) && Factory.TryGetValue(info.Id, out var make))
            {
                var view = make();
                view.Ctx = new OverlayContext { Settings = s, Hub = _hub, EditMode = _edit, Opacity = cfg?.Opacity ?? 0.92 };
                var w = new OverlayWindow(view);
                w.SetScale(cfg?.Scale ?? 1);
                PlaceWindow(w, cfg, info);
                w.Moved += SavePosition;
                w.ScaleChanged += (win, sc) => _settings.Update(st => Cfg(st, win.View.Id).Scale = sc);
                w.SetClickThrough(!_edit);
                _windows[info.Id] = w;
                w.Show();
                // remember the default position so it's stable from now on
                if (cfg == null || double.IsNaN(cfg.X)) SavePosition(w);
            }
            else if (!enabled && _windows.TryGetValue(info.Id, out var w))
            {
                w.Close();
                _windows.Remove(info.Id);
            }
            else if (enabled && _windows.TryGetValue(info.Id, out var existing))
            {
                existing.View.Ctx = new OverlayContext { Settings = s, Hub = _hub, EditMode = _edit, Opacity = cfg?.Opacity ?? 0.92 };
                if (cfg != null && Math.Abs(existing.Scale - cfg.Scale) > 0.001) existing.SetScale(cfg.Scale);
                if (cfg != null && double.IsNaN(cfg.X)) { PlaceWindow(existing, cfg, info); SavePosition(existing); }
            }
        }
        UpdateVisibility(_hub.State);
    }

    static OverlayConfig Cfg(AppSettings s, string id)
    {
        var c = s.Overlays.FirstOrDefault(o => o.Id == id);
        if (c == null)
        {
            c = new OverlayConfig { Id = id, Enabled = true };
            s.Overlays.Add(c);
        }
        return c;
    }

    void SavePosition(OverlayWindow w)
    {
        double x = w.Left, y = w.Top;
        _settings.Update(s => { var c = Cfg(s, w.View.Id); c.X = x; c.Y = y; c.Enabled = true; });
    }

    void PlaceWindow(OverlayWindow w, OverlayConfig? cfg, RacingHelper.Web.OverlayInfo info)
    {
        var area = SystemParameters.WorkArea;
        if (cfg != null && double.IsFinite(cfg.X) && double.IsFinite(cfg.Y)
            && cfg.X > SystemParameters.VirtualScreenLeft - 50 && cfg.X < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40
            && cfg.Y > SystemParameters.VirtualScreenTop - 50 && cfg.Y < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40)
        {
            w.Left = cfg.X; w.Top = cfg.Y;
            return;
        }
        // sensible default layout on the primary screen
        var (fx, fy) = info.Id switch
        {
            "delta-bar" => (0.5 - info.Width / 2 / area.Width, 0.03),
            "delta-sectors" => (0.5 + 200 / area.Width, 0.03),
            "inputs" => (0.5 - info.Width / 2 / area.Width, 0.86),
            "input-telemetry" => (0.5 - 220 / area.Width - info.Width / area.Width, 0.86),
            "input-compare" => (0.5 + 170 / area.Width, 0.86),
            "speed-compare" => (0.5 + 170 / area.Width, 0.72),
            "relative" => (0.02, 0.55),
            "standings" => (0.02, 0.05),
            "radar" => (0.5 - info.Width / 2 / area.Width, 0.55),
            "brake-indicator" => (0.72, 0.35),
            "corner-analysis" => (0.5 - info.Width / 2 / area.Width, 0.13),
            "fuel" => (0.80, 0.70),
            "tyres" => (0.80, 0.48),
            "engineer" => (0.75, 0.05),
            "track-map" => (0.80, 0.20),
            "mini-map" => (0.84, 0.25),
            "line-compare" => (0.04, 0.30),
            "comparison-target" => (0.5 - info.Width / 2 / area.Width, 0.18),
            "weather" => (0.30, 0.05),
            "weather-forecast" => (0.30, 0.18),
            "damage" => (0.80, 0.05),
            _ => (0.1, 0.1),
        };
        w.Left = area.Left + area.Width * fx;
        w.Top = area.Top + area.Height * fy;
    }

    void OnRendering(object? sender, EventArgs e)
    {
        long now = _clock.ElapsedMilliseconds;
        int fps = Math.Clamp(_settings.Current.OverlayFps, 10, 60);
        if (now - _lastFrameMs < 1000 / fps) return;
        _lastFrameMs = now;
        var state = _hub.State;
        UpdateVisibility(state);
        foreach (var w in _windows.Values)
        {
            if (!w.IsVisible) continue;
            w.View.State = state;
            w.View.InvalidateVisual();
        }
        if (now - _lastTopMs > 3000)
        {
            _lastTopMs = now;
            foreach (var w in _windows.Values) if (w.IsVisible) w.BringToTop();
        }
    }

    void UpdateVisibility(LiveState state)
    {
        var s = _settings.Current;
        bool active = state.InCar || state.Status == "replay" || !s.OverlaysOnlyInCar;
        bool show = _edit || (!_hidden && active);
        foreach (var w in _windows.Values)
        {
            if (show && !w.IsVisible) w.Show();
            else if (!show && w.IsVisible) w.Hide();
        }
    }

    public void Dispose()
    {
        CompositionTarget.Rendering -= OnRendering;
        foreach (var w in _windows.Values) w.Close();
        _windows.Clear();
    }
}
