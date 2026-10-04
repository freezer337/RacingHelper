using RacingHelper.Recording;
using RacingHelper.Sim;
using RacingHelper.Storage;

namespace RacingHelper.Analysis;

/// <summary>Loads stored laps and produces analysis for the dashboard / CLI.</summary>
public sealed class AnalysisService
{
    readonly SessionStore _store;
    Database Db => _store.Db;
    public Func<int>? MyDriverIdProvider { get; set; }

    public AnalysisService(SessionStore store) { _store = store; }

    public int MyDriverId
    {
        get
        {
            int id = MyDriverIdProvider?.Invoke() ?? 0;
            if (id > 0) return id;
            // most frequent driver in the database = the owner
            var s = Db.GetSessions(limit: 200);
            return s.GroupBy(x => x.DriverId).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        }
    }

    public (SessionInfo? info, TrackModel? model, SessionRow? row) SessionContext(long sessionId)
    {
        var row = Db.GetSession(sessionId);
        if (row == null) return (null, null, null);
        var yaml = Db.GetSessionInfoYaml(sessionId);
        SessionInfo? info = null;
        try { if (yaml != null) info = new SessionInfo(yaml); } catch { }
        return (info, _store.GetTrackModel(row.TrackKey), row);
    }

    public ReferenceLap? LoadReference(long lapId, string? label = null)
    {
        var row = Db.GetLap(lapId);
        if (row == null) return null;
        var model = _store.GetTrackModel(row.TrackKey!);
        var data = Db.GetLapData(lapId);
        var sess = Db.GetSession(row.SessionId);
        if (model == null || data == null || sess == null) return null;
        var dist = DistLap.TryCreate(data, model.Length, row.LapTime, !row.OutLap);
        if (dist == null) return null;
        return new ReferenceLap
        {
            LapId = lapId,
            HasGps = row.HasGps,
            Label = label ??(row.DriverId == MyDriverId ? $"your best ({Fmt.LapTime(row.LapTime)})" : $"{row.DriverName} ({Fmt.LapTime(row.LapTime)})"),
            Dist = dist,
            Analysis = LapAnalyzer.Analyze(dist, model),
        };
    }

    /// <summary>
    /// Fastest stored lap of the owner for this car/track in comparable conditions
    /// (same tyre compound and dry/wet track), optionally excluding a session.
    /// </summary>
    public LapRow? PersonalBestRow(string carPath, string trackKey, long? excludeSession = null, Conditions? cond = null)
        => Db.GetBestLaps(carPath, trackKey, 100, MyDriverId)
             .FirstOrDefault(l => (excludeSession == null || l.SessionId != excludeSession) && (cond == null || cond.Matches(l)));

    public sealed record Conditions(int Compound, bool Wet)
    {
        public static bool IsWet(int wetness) => wetness >= 3;
        public bool Matches(LapRow l) => (Compound < 0 || l.Compound < 0 || l.Compound == Compound) && (l.Wetness < 0 || IsWet(l.Wetness) == Wet);

        public static Conditions? Of(IEnumerable<LapRow> laps)
        {
            var v = laps.Where(l => l.Valid).ToList();
            if (v.Count == 0) return null;
            int compound = v.GroupBy(l => l.Compound).OrderByDescending(g => g.Count()).First().Key;
            bool wet = v.Count(l => l.Wetness >= 0 && IsWet(l.Wetness)) * 2 > v.Count;
            return new Conditions(compound, wet);
        }
    }

    public SessionReport? SessionReport(long sessionId, long? referenceLapId = null)
    {
        var (info, model, row) = SessionContext(sessionId);
        if (row == null) return null;
        var laps = Db.GetLaps(sessionId).Select(r => new AnalyzedLap { Row = r, Data = Db.GetLapData(r.Id) }).ToList();

        ReferenceLap? reference = null;
        if (referenceLapId is > 0) reference = LoadReference(referenceLapId.Value);
        else
        {
            // default reference: personal best from *other* sessions; fall back to nothing (session's own best is the baseline)
            var pb = PersonalBestRow(row.CarPath, row.TrackKey, sessionId, Conditions.Of(laps.Select(l => l.Row)));
            var sessionBest = laps.Where(l => l.Row.Valid).Select(l => l.Row.LapTime).DefaultIfEmpty(double.MaxValue).Min();
            if (pb != null && pb.LapTime < sessionBest) reference = LoadReference(pb.Id, $"your PB ({Fmt.LapTime(pb.LapTime)})");
        }

        var target = TyreAnalyzer.DefaultTarget(row.CarCategory);
        return SessionAnalyzer.Analyze(laps, model, row.CarCategory, info?.ShiftRpm ?? 0, info?.ShiftLightBlinkRpm ?? 0, reference, target);
    }
}
