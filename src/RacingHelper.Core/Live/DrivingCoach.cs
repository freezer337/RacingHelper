using RacingHelper.Analysis;
using RacingHelper.Sim;

namespace RacingHelper.Live;

/// <summary>
/// Corner coaching: when you keep losing time in a corner for the same reason, you get one short tip
/// *before* that corner on the next lap (timed to finish before the braking point), then "better" when it works.
/// </summary>
public sealed class DrivingCoach
{
    const float SpeechSeconds = 3.5f;

    readonly RaceEngineer _eng;
    readonly Func<AppSettings> _settings;
    readonly Dictionary<int, List<(int lap, CornerComparison cc)>> _hist = new();
    readonly Dictionary<int, (int lap, string issue, float before)> _tipped = new();
    readonly Dictionary<int, int> _lastTipLap = new();
    int _lap = -1, _tipsThisLap;
    float _prevD = float.NaN;
    string _kind = "practice";

    public string LastTip { get; private set; } = "";

    public DrivingCoach(RaceEngineer engineer, Func<AppSettings> settings)
    {
        _eng = engineer;
        _settings = settings;
    }

    public void Reset(string sessionType)
    {
        _kind = TyreManager.Kind(sessionType);
        _hist.Clear();
        _tipped.Clear();
        _lastTipLap.Clear();
        _lap = -1; _tipsThisLap = 0; _prevD = float.NaN;
        LastTip = "";
    }

    bool Active => _settings().CoachingMode switch
    {
        "always" => true,
        "practice" => _kind == "practice",
        _ => false,
    };

    int MaxTipsPerLap => _kind == "practice" ? 2 : 1;

    /// <summary>A corner was just completed and compared with the reference.</summary>
    public void OnCorner(CornerComparison cc, int lap)
    {
        if (!float.IsFinite(cc.TimeDelta)) return;
        if (!_hist.TryGetValue(cc.Corner, out var h)) _hist[cc.Corner] = h = new();
        if (h.Count > 0 && h[^1].lap == lap) h[^1] = (lap, cc); else h.Add((lap, cc));
        if (h.Count > 3) h.RemoveAt(0);

        if (_tipped.TryGetValue(cc.Corner, out var t) && t.lap == lap)
        {
            _tipped.Remove(cc.Corner);
            if (Active && (cc.TimeDelta < 0.03f || cc.TimeDelta < t.before - 0.06f))
                _eng.Say($"Better through {Spoken(cc.Name)}.", "coach", 0, minVerbosity: 1);
        }
    }

    /// <summary>Per frame on a flying lap: speak the tip for the next corner when we reach its call point.</summary>
    public void Update(Frame f, float d, TrackModel model, ReferenceLap reference)
    {
        if (f.Lap != _lap) { _lap = f.Lap; _tipsThisLap = 0; _prevD = float.NaN; }
        float prev = _prevD;
        _prevD = d;
        if (!Active || float.IsNaN(prev) || d <= prev || d - prev > 50) return;
        if (_tipsThisLap >= MaxTipsPerLap) return;

        var corners = model.Corners;
        for (int i = 0; i < corners.Count; i++)
        {
            var c = corners[i];
            var r = reference.Analysis.Corners.FirstOrDefault(x => x.Corner == c.Index);
            float brake = r != null && float.IsFinite(r.BrakePoint) ? r.BrakePoint
                        : r != null && float.IsFinite(r.LiftPoint) ? r.LiftPoint
                        : c.Start - 60;
            float approach = r != null && float.IsFinite(r.EntrySpeed) ? r.EntrySpeed : Math.Max(f.Speed, 20);
            float call = brake - approach * SpeechSeconds;
            float floor = i > 0 ? corners[i - 1].End : 0;
            if (call < floor) call = floor;
            if (brake - call < 40 || call < 0) continue;              // no room to say it before this corner
            if (!(prev < call && d >= call)) continue;

            if (_lastTipLap.TryGetValue(c.Index, out int lastLap) && lastLap >= f.Lap - 1) return; // give it a lap before repeating
            var tip = TipFor(c.Index, out float before);
            if (tip == null) return;
            string text = $"{Cap(Spoken(c.Name))}, {tip.Value.text}";
            if (_eng.Say(text, "coach", 1, "coach-" + c.Index, 20, lapDist: d, validUntil: brake))
            {
                _tipped[c.Index] = (f.Lap, tip.Value.issue, before);
                _lastTipLap[c.Index] = f.Lap;
                _tipsThisLap++;
                LastTip = text;
            }
            return;
        }
    }

    (string issue, string text)? TipFor(int corner, out float before)
    {
        before = float.NaN;
        if (!_hist.TryGetValue(corner, out var h) || h.Count < 2) return null;
        var last2 = h.TakeLast(2).Select(x => x.cc).ToList();
        float avg = last2.Average(x => x.TimeDelta);
        if (avg < 0.08f || last2[^1].TimeDelta < 0.05f) return null;
        before = avg;
        var recent = h.Select(x => x.cc).ToList();
        bool Most(Func<CornerComparison, bool> p) => recent.Count(p) >= 2;

        if (Most(c => c.BrakeDiff > 5 && c.MinSpeedDiff < -3))
            return ("overdrive", "easier on entry. Brake a touch earlier and carry more speed to the apex.");
        if (Most(c => c.BrakeDiff < -8 && !(c.MinSpeedDiff < -3)))
        {
            float m = -recent.Where(c => c.BrakeDiff < -8).Average(c => c.BrakeDiff);
            return ("early-brake", $"brake later. You're about {Math.Max(5, MathF.Round(m / 5) * 5):0} metres early.");
        }
        if (Most(c => c.MinSpeedDiff < -3))
            return ("apex", "more speed through the middle. Release the brake earlier and let it roll.");
        if (Most(c => c.ThrottleDiff > 10))
            return ("throttle", "turn it in a bit later and sharper, so you can get on the power earlier.");
        if (Most(c => c.ExitSpeedDiff < -3))
            return ("exit", "later apex and a straighter exit. Exit speed is what counts here.");
        if (Most(c => c.CoastDiff > 12))
            return ("coast", "no coasting. Go straight from brake to throttle.");
        return null;
    }

    static string Spoken(string name) => name.StartsWith('T') && name.Length > 1 && char.IsDigit(name[1]) ? "turn " + name[1..] : name;
    static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
