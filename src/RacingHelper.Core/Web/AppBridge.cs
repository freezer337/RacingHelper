namespace RacingHelper.Web;

public sealed record ButtonPress(string Device, string DeviceName, int Button);

public sealed record OverlayInfo(string Id, string Name, string Description, double Width, double Height, bool DefaultOn);

/// <summary>Things only the desktop shell can do (overlay windows, opening folders, wheel buttons, hotkeys).</summary>
public interface IAppBridge
{
    bool OverlayEditMode { get; set; }
    void OverlaysChanged();
    void OpenFolder(string path);
    IReadOnlyList<OverlayInfo> OverlayCatalog { get; }
    /// <summary>Waits for the next button press on any wheel / button box (null on timeout or if unsupported).</summary>
    Task<ButtonPress?> LearnButton(int timeoutMs);
    IReadOnlyList<string> Controllers();
    /// <summary>(Re)registers the keyboard shortcuts from settings; returns the ones Windows refused (already in use / unknown key).</summary>
    IReadOnlyList<string> ApplyHotkeys();
}

public static class OverlayCatalog
{
    public static readonly IReadOnlyList<OverlayInfo> All = new List<OverlayInfo>
    {
        new("inputs", "Essential Inputs", "Throttle, brake, clutch, gear, speed and steering at a glance.", 300, 120, true),
        new("input-telemetry", "Input Telemetry", "Scrolling trace of throttle, brake and steering over the last few seconds.", 420, 130, true),
        new("input-compare", "Input Comparison", "Your throttle and brake traces against the reference lap at the same point on track.", 420, 140, false),
        new("speed-compare", "Speed Comparison", "Your speed vs the reference lap's speed at the same point, with the difference.", 420, 140, false),
        new("brake-indicator", "Brake Indicator", "Countdown boards to the reference braking point for the next corner.", 110, 260, true),
        new("delta-sectors", "Delta Sectors", "Sector times and deltas vs reference, with session-best colours.", 300, 170, true),
        new("delta-bar", "Delta Bar", "Live delta to your reference lap with a gain/loss bar and predicted lap.", 380, 80, true),
        new("standings", "Standings", "Race order with gaps, intervals, last laps, iRating and projected iRating change.", 520, 360, false),
        new("relative", "Relatives", "Cars around you on track with relative time.", 400, 260, true),
        new("comparison-target", "Comparison Target", "Which lap you are comparing against, plus best / last / predicted.", 300, 130, false),
        new("corner-analysis", "Corner Analysis", "After each corner: time gained or lost and why (braking, apex speed, throttle).", 330, 120, true),
        new("track-map", "Track Map", "Full track map with every car, your position and corner numbers.", 300, 300, false),
        new("mini-map", "Mini Map", "Heading-up close-up of the track around you.", 220, 220, false),
        new("fuel", "Fuel Calculator", "Usage per lap, laps left in the tank, fuel to finish and to add.", 300, 190, true),
        new("tyres", "Tyres", "Tyre temperatures (inner/middle/outer), pressures and wear.", 300, 200, false),
        new("weather", "Weather Conditions", "Air and track temperature, skies, wetness, wind.", 260, 120, false),
        new("radar", "Radar", "Cars alongside and close behind / ahead.", 200, 240, true),
        new("damage", "Damage", "Repair times, engine warnings, incidents and pace-loss after contact.", 280, 150, false),
        new("line-compare", "Line Comparison", "Close-up of your line vs the reference line through the current corner.", 260, 260, false),
        new("weather-forecast", "Weather Forecast", "Projected track temperature and wetness trend for the next hour.", 300, 140, false),
        new("engineer", "Race Engineer", "The engineer's latest messages as text.", 380, 150, true),
    };
}
