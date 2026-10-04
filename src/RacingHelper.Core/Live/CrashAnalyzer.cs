using RacingHelper.Analysis;
using RacingHelper.Sim;

namespace RacingHelper.Live;

/// <summary>
/// After a spin, crash or contact, works out what started it from the last few seconds of driving:
/// power oversteer, the rear stepping out under braking, lift-off oversteer, a kerb strike, running wide
/// (understeer), or contact with another car — and in a race, whether it was your doing.
/// Says it once the car has slowed, with a driving tip and (when it keeps happening) the setup fix.
/// </summary>
public sealed class CrashAnalyzer
{
    struct S
    {
        public double T; public float D, V, Thr, Brk, Steer, Yaw, Lat, Long, Vert, Ahead, Behind;
        public int LR, Surface;
    }

    const int N = 240;                 // ~8 s at 30 Hz
    readonly S[] _buf = new S[N];
    int _pos, _count;
    double _lastSample = double.NegativeInfinity;

    readonly RaceEngineer _eng;
    readonly Func<AppSettings> _settings;
    int _prevInc = -1;
    double _eventAt = double.NaN, _lastEvent = double.NegativeInfinity;
    int _eventInc;
    float _eventD;
    string _kind = "practice";

    public readonly List<CrashRecord> Session = new();
    public CrashRecord? Last { get; private set; }
    public event Action<CrashRecord, SetupRequest?>? Analysed;

    public CrashAnalyzer(RaceEngineer engineer, Func<AppSettings> settings)
    {
        _eng = engineer;
        _settings = settings;
    }

    public void Reset(string sessionType)
    {
        _kind = TyreManager.Kind(sessionType);
        _count = 0; _pos = 0; _prevInc = -1; _eventAt = double.NaN; _lastEvent = double.NegativeInfinity;
        Session.Clear(); Last = null;
    }

    public void Update(Frame f, float d, float steerK, TrackModel? model, ReferenceLap? reference)
    {
        if (!f.IsOnTrack) { _prevInc = f.PlayerCarMyIncidentCount; return; }
        if (f.SessionTime - _lastSample >= 1.0 / 30)
        {
            _lastSample = f.SessionTime;
            _buf[_pos] = new S
            {
                T = f.SessionTime, D = d, V = f.Speed, Thr = f.Throttle, Brk = f.Brake, Steer = f.Steer, Yaw = f.YawRate,
                Lat = f.LatAccel, Long = f.LongAccel, Vert = f.VertAccel, Ahead = f.CarDistAhead, Behind = f.CarDistBehind,
                LR = f.CarLeftRight, Surface = f.PlayerTrackSurface,
            };
            _pos = (_pos + 1) % N; _count = Math.Min(N, _count + 1);
        }

        int inc = f.PlayerCarMyIncidentCount;
        int added = _prevInc >= 0 ? inc - _prevInc : 0;
        _prevInc = inc;
        // iRacing counts spins / wall hits (2x) and car contact (4x) in every session, offline too
        bool trigger = added >= 2 && f.SessionTime - _lastEvent > 8;
        if (trigger && double.IsNaN(_eventAt)) { _eventAt = f.SessionTime; _eventInc = Math.Max(added, 0); _eventD = d; }
        else if (!double.IsNaN(_eventAt) && added > 0) _eventInc += added;

        // give it a moment to see the impact, then explain
        if (!double.IsNaN(_eventAt) && f.SessionTime - _eventAt >= 1.5)
        {
            _lastEvent = _eventAt;
            var rec = Analyse(_eventAt, _eventInc, _eventD, f.Lap, steerK, model, reference, out var req, out string tip);
            _eventAt = double.NaN;
            if (rec == null) return;
            Session.Add(rec);
            Last = rec;
            int sameKind = Session.Count(c => c.Kind == rec.Kind && c.SelfInflicted);
            string fix = req != null && rec.SelfInflicted && sameKind >= 2 ? SetupHint(req) : "";
            _eng.Say($"{rec.Summary} {tip}{fix}".Trim(), "incident", 2);
            Analysed?.Invoke(rec, rec.SelfInflicted ? req : null);
        }
    }

    /// <summary>Filled in by the hub: how to phrase a setup fix (in-car first) for the car you're in.</summary>
    public Func<SetupRequest, string>? DescribeFix { get; set; }

    string SetupHint(SetupRequest req)
    {
        string? fix = DescribeFix?.Invoke(req);
        return string.IsNullOrEmpty(fix) ? "" : $" That's twice now. Setup: {fix}.";
    }

    CrashRecord? Analyse(double t0, int inc, float d0, int lap, float k, TrackModel? model, ReferenceLap? reference, out SetupRequest? req, out string tip)
    {
        req = null; tip = "";
        var w = Window(t0 - 4, t0 + 1.5);
        if (w.Count < 20) return null;
        var before = w.Where(s => s.T <= t0 + 0.05).ToList();
        var after = w.Where(s => s.T > t0).ToList();
        string corner = Corner(model, d0);
        string at = corner.Length > 0 ? $" at {corner}" : "";
        float vBefore = before.Count > 0 ? before[^1].V : 0;
        bool hitWall = after.Any(s => Math.Abs(s.Long) > 30 || Math.Abs(s.Lat) > 30) && after.Count > 0 && after.Min(s => s.V) < vBefore * 0.5f;

        // other cars right there?
        bool carAlongside = before.Where(s => s.T >= t0 - 1).Any(s => s.LR >= 2);
        bool carBehind = before.Where(s => s.T >= t0 - 1).Any(s => float.IsFinite(s.Behind) && s.Behind > 0 && s.Behind < 4);
        bool carAhead = before.Where(s => s.T >= t0 - 1).Any(s => float.IsFinite(s.Ahead) && s.Ahead > 0 && s.Ahead < 4);
        bool contact = inc >= 4 || ((carAlongside || carBehind || carAhead) && (hitWall || inc >= 2));

        // the moment the rear let go: rotating clearly more than the steering asks for, or opposite lock
        int slide = -1;
        if (float.IsFinite(k) && Math.Abs(k) > 1e-3)
            for (int i = 0, run = 0; i < before.Count; i++)
            {
                var s = before[i];
                float req2 = s.V * s.Steer / k;
                bool counter = Math.Sign(req2) != Math.Sign(s.Yaw) && Math.Abs(s.Yaw) > 0.4f && Math.Abs(req2) > 0.1f;
                bool over = Math.Abs(s.Yaw) > 0.4f && Math.Abs(s.Yaw) > Math.Abs(req2) * 1.4f + 0.25f;
                run = s.V > 8 && (over || counter) ? run + 1 : 0;
                if (run >= 3) { slide = i - 2; break; }
            }

        string kind, summary;
        bool self = true;
        if (contact && slide < 0)
        {
            kind = "contact";
            if (carBehind && !carAhead) { self = false; summary = $"Contact from behind{at}. Not your doing."; }
            else if (carAhead && !carBehind) { summary = $"You ran into the car ahead{at}."; tip = "When you're that close, brake a bit earlier and take a defensive line."; }
            else { self = _kind != "race"; summary = $"Side-by-side contact{at}."; tip = "Leave a car's width when you're alongside."; }
        }
        else if (slide >= 0)
        {
            var s = before[slide];
            var prior = before.Where(x => x.T >= s.T - 0.6 && x.T <= s.T).ToList();
            // the inputs right around the moment it let go (a throttle stab shows up a few frames later)
            var around = before.Where(x => x.T >= s.T - 0.2 && x.T <= s.T + 0.35).ToList();
            float thrMax = around.Max(x => x.Thr), brkMax = around.Max(x => x.Brk);
            float thrRise = thrMax - prior.Select(x => x.Thr).DefaultIfEmpty(0).Min();
            bool kerb = prior.Any(x => Math.Abs(x.Vert - 9.8f) > 7);
            bool lifted = prior.Any(x => x.Thr > 0.5f) && s.Thr < 0.1f && s.Brk < 0.1f;
            bool power = thrMax > 0.3f && thrRise > 0.25f;
            string speed = SpeedNote(s, reference);
            if (kerb)
            {
                kind = "kerb"; summary = $"You hit the kerb{at} and it threw the car.";
                tip = "Stay off that kerb, or take less of it."; req = new SetupRequest { Symptom = "kerbs", Phase = "all" };
            }
            else if (power)
            {
                kind = "power-oversteer"; summary = $"Power oversteer{at}: throttle on while the car was still turning, and the rear let go{speed}.";
                tip = "Squeeze the throttle in and wait until the steering starts to unwind."; req = new SetupRequest { Symptom = "oversteer", Phase = "exit" };
            }
            else if (brkMax > 0.15f)
            {
                kind = "brake-oversteer"; summary = $"The rear stepped out under braking{at}{speed}.";
                tip = "Brake in a straighter line and release the brake more gently as you turn in."; req = new SetupRequest { Symptom = "oversteer", Phase = "entry" };
            }
            else if (lifted)
            {
                kind = "lift-oversteer"; summary = $"Lift-off oversteer{at}: you came off the throttle mid-corner and the rear came round{speed}.";
                tip = "Keep a little throttle through the corner instead of lifting sharply."; req = new SetupRequest { Symptom = "oversteer", Phase = "mid" };
            }
            else
            {
                kind = "mid-oversteer"; summary = $"The rear let go mid-corner{at}{speed}.";
                tip = "Smoother hands and feet there."; req = new SetupRequest { Symptom = "oversteer", Phase = "mid" };
            }
            if (contact) summary += " Then there was contact.";
        }
        else
        {
            // no slide: ran wide / pushed off
            var last = before.Where(x => x.T >= t0 - 1.5).ToList();
            float push = float.IsFinite(k) && Math.Abs(k) > 1e-3
                ? last.Where(x => x.V > 8).Select(x => Math.Abs(x.V * x.Steer / k) - Math.Abs(x.Yaw)).DefaultIfEmpty(0).Average() : 0;
            bool throttle = last.Any(x => x.Thr > 0.3f);
            var s = last.Count > 0 ? last[0] : before[^1];
            kind = "ran-wide";
            summary = push > 0.1f
                ? $"You ran out of road{at}: the front washed wide{(throttle ? " as you went to the throttle with lock still on" : "")}{SpeedNote(s, reference)}."
                : $"You went off{at}{SpeedNote(s, reference)}.";
            tip = throttle ? "Wait for the apex, then open the steering before you go to full throttle." : "Brake a touch earlier there.";
            req = push > 0.1f ? new SetupRequest { Symptom = "understeer", Phase = throttle ? "exit" : "mid" } : null;
        }
        if (hitWall && kind != "contact") summary += " Into the wall.";
        return new CrashRecord { Lap = lap, Corner = corner, Kind = kind, SelfInflicted = self, Summary = summary, Session = _kind };
    }

    static string SpeedNote(S s, ReferenceLap? reference)
    {
        if (reference == null) return "";
        float refV = reference.Dist.At(reference.Dist.Speed, s.D);
        if (!float.IsFinite(refV)) return "";
        float diff = (s.V - refV) * 3.6f;
        return diff >= 5 ? $", {diff:0} km/h faster than your reference there" : "";
    }

    static string Corner(TrackModel? model, float d)
    {
        var c = model?.CornerAt(d) ?? model?.Corners.OrderBy(x => Math.Abs(x.Apex - d)).FirstOrDefault(x => Math.Abs(x.Apex - d) < 250);
        if (c == null) return "";
        return c.Name.StartsWith('T') && c.Name.Length > 1 && char.IsDigit(c.Name[1]) ? "turn " + c.Name[1..] : c.Name;
    }

    List<S> Window(double from, double to)
    {
        var l = new List<S>(_count);
        for (int i = 0; i < _count; i++)
        {
            var s = _buf[(_pos - _count + i + N) % N];
            if (s.T >= from && s.T <= to) l.Add(s);
        }
        return l;
    }
}
