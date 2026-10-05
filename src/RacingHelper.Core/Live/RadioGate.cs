using RacingHelper.Analysis;
using RacingHelper.Sim;

namespace RacingHelper.Live;

/// <summary>
/// Holds spoken messages until you're on a straight with room to hear them: not braking, not cornering hard, and far
/// enough from the next braking point to finish the sentence. Urgent messages, timed corner tips and answers to your
/// own questions go out straight away. Stale chatter is dropped instead of being read out late.
/// </summary>
public sealed class RadioGate
{
    readonly Func<AppSettings> _settings;
    readonly List<(EngineerMessage m, double at)> _queue = new();
    readonly object _lock = new();
    double _busyUntil = double.NegativeInfinity;
    double _calmSince = double.NaN;
    double _now;

    public event Action<EngineerMessage>? Released;
    /// <summary>The messages that go out together (one straight, or everything at once in the pits).</summary>
    public event Action<IReadOnlyList<EngineerMessage>>? ReleasedBatch;
    public EngineerMessage? Last { get; private set; }
    /// <summary>Quiet mode: only important calls (priority 2+) and answers to your questions.</summary>
    public bool Quiet { get; set; }
    public int Waiting { get { lock (_lock) return _queue.Count; } }
    /// <summary>The radio profile in use (practice / quali / race / minimal), see RaceEngineer.Profile.</summary>
    public Func<string> Profile { get; set; } = () => "race";

    public RadioGate(Func<AppSettings> settings) { _settings = settings; }

    public static double Duration(string text) => 0.6 + text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length / 2.7;

    public void Enqueue(EngineerMessage m)
    {
        if (Quiet && m.Priority < 2 && !m.Immediate) return;
        if (Profile() == "quali" && !QualiAllows(m)) return;
        bool now = !_settings().QuietInCorners || m.Priority >= 3 || m.Immediate || float.IsFinite(m.ValidUntil);
        if (now) { Send(m); return; }
        lock (_lock) _queue.Add((m, _now));
    }

    void Send(EngineerMessage m) => Send(new[] { m });

    void Send(IReadOnlyList<EngineerMessage> ms)
    {
        if (ms.Count == 0) return;
        foreach (var m in ms)
        {
            _busyUntil = Math.Max(_busyUntil, _now) + Duration(m.Text);
            if (m.Category != "answer") Last = m;
            Released?.Invoke(m);
        }
        ReleasedBatch?.Invoke(ms);
    }

    /// <summary>
    /// Qualifying radio is half silent: tyre warm-up / "push now" / overheating, where you're losing time (corner losses,
    /// coaching, crash analysis), "time for one more lap", anything urgent, and answers to your own questions. No lap
    /// times, setup chatter, gaps or general info.
    /// </summary>
    public static bool QualiAllows(EngineerMessage m) =>
        m.Immediate || m.Priority >= 2 || m.Category is "tyres" or "corner" or "coach" or "incident" || m.Key == "quali-one-more";

    /// <summary>Not driving (in the pits, sim closed, replay paused): everything can be said now.</summary>
    public void Flush(double now)
    {
        _now = now;
        List<(EngineerMessage m, double at)> all;
        lock (_lock) { all = _queue.ToList(); _queue.Clear(); }
        if (all.Count > 0) Send(all.OrderByDescending(x => x.m.Priority).ThenBy(x => x.at).Select(x => x.m).ToList());
        _calmSince = double.NaN;
    }

    /// <param name="toZone">metres to the next braking / turn-in zone (NaN if the track isn't mapped yet)</param>
    /// <param name="inZone">currently between a braking point and the corner exit</param>
    public void Tick(Frame f, double now, float toZone, bool inZone)
    {
        _now = now;
        if (!f.IsOnTrack || f.OnPitRoad || f.Speed < 8) { Flush(now); return; }   // in the pits, stopped or spun
        (EngineerMessage m, double at) next;
        lock (_lock)
        {
            if (_queue.Count == 0) return;
            // drop what's no longer worth saying (long enough that a few slow laps of corners don't lose anything)
            _queue.RemoveAll(x => now - x.at > (x.m.Priority switch { 0 => 120, 1 => 240, _ => 600 }) || now < x.at);
            if (_queue.Count == 0) return;
            next = _queue.OrderByDescending(x => x.m.Priority).ThenBy(x => x.at).First();
        }
        if (now < _busyUntil) return;

        bool calm = !inZone && f.Brake < 0.05f && Math.Abs(f.LatAccel) < 4f;
        if (!calm) { _calmSince = double.NaN; return; }
        if (double.IsNaN(_calmSince)) _calmSince = now;

        double need = Duration(next.m.Text);
        double waited = now - next.at;
        bool ok;
        // a settled moment of straight-line driving first (no track map yet: a full second)
        if (now - _calmSince < (float.IsFinite(toZone) ? 0.5 : 1.0)) return;
        if (float.IsFinite(toZone)) ok = toZone / Math.Max(f.Speed, 10f) >= (waited > 25 ? need * 0.6 : need);
        else ok = true;
        if (!ok && next.m.Priority >= 2 && waited > 20) ok = true;
        if (!ok) return;

        // everything else that's waiting goes out on the same straight if there's room (one after the other, so nothing
        // waits for a straight of its own and gets forgotten); what doesn't fit waits for the next straight
        var batch = new List<EngineerMessage> { next.m };
        lock (_lock)
        {
            _queue.Remove(next);
            double room = float.IsFinite(toZone) ? toZone / Math.Max(f.Speed, 10f) : 3 * need;
            double used = need;
            foreach (var x in _queue.OrderByDescending(x => x.m.Priority).ThenBy(x => x.at).ToList())
            {
                double d = Duration(x.m.Text);
                if (used + d > room * 1.1) continue;
                used += d;
                batch.Add(x.m);
                _queue.Remove(x);
            }
        }
        Send(batch);
    }

    /// <summary>Braking / turn-in zones from the track model (braking points from the reference lap when there is one).</summary>
    public static (bool inZone, float toZone) Zone(float d, float L, TrackModel? model, ReferenceLap? reference)
    {
        if (model == null || model.Corners.Count == 0 || L <= 0) return (false, float.NaN);
        bool inZone = false;
        float toZone = float.PositiveInfinity;
        foreach (var c in model.Corners)
        {
            var rc = reference?.Analysis.Corners.FirstOrDefault(x => x.Corner == c.Index);
            float start = rc != null && float.IsFinite(rc.BrakePoint) ? rc.BrakePoint
                        : rc != null && float.IsFinite(rc.LiftPoint) ? rc.LiftPoint
                        : c.Start - 80;
            float end = c.End + 20;
            float len = ((end - start) % L + L) % L;
            float into = ((d - start) % L + L) % L;
            if (into <= len) inZone = true;
            float ahead = ((start - d) % L + L) % L;
            toZone = Math.Min(toZone, ahead);
        }
        return (inZone, toZone);
    }
}
