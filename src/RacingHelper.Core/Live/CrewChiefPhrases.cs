namespace RacingHelper.Live;

/// <summary>
/// CrewChief plays its own recorded voice (Jim) when the text it gets is the name of one of its sound folders, and
/// only reads anything else with a Windows text-to-speech voice. So every engineer call that Jim has actually recorded
/// is sent as his clip; only what he never recorded (corner coaching, setup changes, crash analysis…) is left as text.
/// Clip names are the ones CrewChief's own events use (Events/TyreMonitor.cs, LapCounter.cs, CommonActions.cs).
/// </summary>
public static class CrewChiefPhrases
{
    public const string RadioCheck = "acknowledge/radio_check";

    /// <summary>Jim's clips for this message, and the part (if any) he has no recording for.</summary>
    public static (List<string> clips, string rest) Map(EngineerMessage m)
    {
        string key = m.Key ?? "", t = m.Text ?? "";
        var clips = new List<string>();

        if (key == "radio-check") return (new() { RadioCheck }, "");
        if (key == "tyres-cold") return (new() { "tyre_monitor/cold_tyres_all_round" }, "");
        if (key is "tyres-warm" or "tyres-ok") return (new() { "tyre_monitor/good_tyre_temps" }, "");
        if (key == "quali-last") return (new() { "lap_counter/last_lap" }, "");

        if (key.StartsWith("tyres-sliding-"))
        {
            // "Still sliding the rears, they can't cool like this. Ease off a bit more."
            clips.Add(t.Contains("front", StringComparison.OrdinalIgnoreCase) ? "tyre_monitor/cooking_front_tyres" : "tyre_monitor/cooking_rear_tyres");
            return (clips, "");
        }
        if (key == "tyres-partial")
        {
            // "Rears are OK again. Fronts still hot, keep cooling them."
            clips.Add(t.Contains("Fronts still hot", StringComparison.Ordinal) ? "tyre_monitor/hot_front_tyres" : "tyre_monitor/hot_rear_tyres");
            return (clips, "");
        }
        if (key.StartsWith("tyres-hot-"))
        {
            // first sentence names the tyres ("Rear tyres overheating." / "Right rear overheating." / "All four tyres…");
            // the rest is the how-to and the cool-down estimate, which Jim never recorded
            int dot = t.IndexOf(". ", StringComparison.Ordinal);
            string head = (dot > 0 ? t[..dot] : t).ToLowerInvariant();
            string? clip =
                head.StartsWith("all four") ? "tyre_monitor/hot_tyres_all_round" :
                head.StartsWith("left front") ? "tyre_monitor/hot_left_front_tyre" :
                head.StartsWith("right front") ? "tyre_monitor/hot_right_front_tyre" :
                head.StartsWith("left rear") ? "tyre_monitor/hot_left_rear_tyre" :
                head.StartsWith("right rear") ? "tyre_monitor/hot_right_rear_tyre" :
                head.StartsWith("front") ? "tyre_monitor/hot_front_tyres" :
                head.StartsWith("rear") ? "tyre_monitor/hot_rear_tyres" : null;
            if (clip != null) return (new() { clip }, dot > 0 ? t[(dot + 2)..].Trim() : "");
        }
        return (clips, t);
    }
}
