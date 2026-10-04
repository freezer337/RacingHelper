using System.Security.Cryptography;
using System.Text;
using RacingHelper.Analysis;
using RacingHelper.Recording;
using RacingHelper.Sim;

namespace RacingHelper.Storage;

/// <summary>Owns track models (cached) and persists what a <see cref="SessionTracker"/> produces.</summary>
public sealed class SessionStore
{
    public Database Db { get; }
    readonly Dictionary<string, TrackModel?> _models = new();
    readonly object _lock = new();

    public event Action<TrackModel>? TrackModelUpdated;

    public SessionStore(Database db) { Db = db; }

    public TrackModel? GetTrackModel(string trackKey)
    {
        lock (_lock)
        {
            if (_models.TryGetValue(trackKey, out var m)) return m;
            m = TrackModel.FromJson(Db.GetTrackModelJson(trackKey));
            _models[trackKey] = m;
            return m;
        }
    }

    public void ResetTrackModel(string trackKey)
    {
        lock (_lock) { _models.Remove(trackKey); Db.DeleteTrackModel(trackKey); }
    }

    /// <summary>Builds or upgrades the track model from a clean lap. Returns the current model.</summary>
    public TrackModel? OfferLapForModel(string trackKey, SessionInfo si, RecordedLap lap)
    {
        if (!lap.Valid || lap.Data == null) return GetTrackModel(trackKey);
        var existing = GetTrackModel(trackKey);
        // upgrade when we get GPS for the first time, or a much faster lap (first model may come from a wet / damaged lap)
        bool better = existing == null || (!existing.FromGps && lap.HasGps)
                      || (existing.FromGps == lap.HasGps && lap.LapTime < existing.SourceLapTime * 0.98);
        if (!better) return existing;
        var dist = DistLap.TryCreate(lap.Data, si.TrackLengthM, lap.LapTime, true);
        if (dist == null) return existing;
        var model = TrackModel.Build(trackKey, dist, si.SectorStarts, lap.HasGps, lap.Id);
        if (model.Corners.Count < 2) return existing;
        lock (_lock)
        {
            _models[trackKey] = model;
            Db.SaveTrackModelJson(trackKey, model.ToJson());
        }
        TrackModelUpdated?.Invoke(model);
        return model;
    }

    public static SessionRow ToRow(SessionContext ctx, string source, string sourceFile = "")
    {
        var si = ctx.Info;
        return new SessionRow
        {
            Key = ctx.Key,
            CarPath = si.CarPath, CarName = si.CarName, CarClass = si.CarClass, CarCategory = si.CarCategory,
            TrackKey = si.TrackKey, TrackName = si.TrackDisplayName, TrackConfig = si.TrackConfig, TrackLength = si.TrackLengthM,
            SessionType = ctx.SessionType, SessionName = ctx.SessionName, EventType = si.EventType,
            SubSessionId = si.SubSessionId, Official = si.Official,
            StartedAt = ctx.StartedAt, EndedAt = ctx.StartedAt,
            DriverName = si.Player?.Name ?? "", DriverId = si.PlayerUserId, IRating = si.Player?.IRating ?? 0,
            Source = source, SourceFile = sourceFile,
        };
    }

    public long EnsureSession(SessionContext ctx, string source, string sourceFile = "")
    {
        if (ctx.DbId > 0) return ctx.DbId;
        ctx.DbId = Db.UpsertSession(ToRow(ctx, source, sourceFile), ctx.Info.RawYaml);
        return ctx.DbId;
    }

    /// <summary>Saves a lap, replacing an earlier recording of the same lap (e.g. live → richer .ibt data).</summary>
    public long SaveLap(SessionContext ctx, RecordedLap lap, string source, string sourceFile = "")
    {
        long sid = EnsureSession(ctx, source, sourceFile);
        lap.SetupHash = SaveSetup(ctx.Info);
        var existing = Db.GetLaps(sid).FirstOrDefault(x =>
            Math.Abs(x.LapTime - lap.LapTime) < 0.05 && Math.Abs((x.StartedAt - lap.StartedAt).TotalSeconds) < 90);
        if (existing != null)
        {
            bool richer = (lap.HasGps && !existing.HasGps) || (lap.Tyres != null && existing.Tyres == null);
            if (!richer) { lap.Id = existing.Id; lap.SessionId = sid; return existing.Id; }
            Db.DeleteLap(existing.Id);
        }
        var id = Db.InsertLap(sid, lap);
        Db.UpsertSession(new SessionRow { Key = ctx.Key, EndedAt = lap.StartedAt.AddSeconds(lap.LapTime), SourceFile = sourceFile }, null);
        return id;
    }

    public string SaveSetup(SessionInfo si)
    {
        if (si.CarSetup == null) return "";
        var flat = new List<KeyValuePair<string, string>>();
        si.CarSetup.Flatten("", flat);
        var stable = flat.Where(kv => !(kv.Key.Contains("LastHot") || kv.Key.Contains("LastTemps") || kv.Key.Contains("TreadRemaining") || kv.Key.EndsWith("UpdateCount")))
                         .Select(kv => kv.Key + "=" + kv.Value);
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(string.Join("\n", stable))))[..12];
        // store the setup subtree as YAML text for the journal
        int idx = si.RawYaml.IndexOf("CarSetup:", StringComparison.Ordinal);
        string yaml = idx >= 0 ? si.RawYaml[idx..] : "";
        Db.SaveSetup(hash, si.CarPath, si.SetupName, yaml);
        return hash;
    }
}

public sealed class ImportResult
{
    public string File { get; set; } = "";
    public int Laps { get; set; }
    public int ValidLaps { get; set; }
    public List<long> Sessions { get; set; } = new();
    public string? Error { get; set; }
    public bool Skipped { get; set; }
}

/// <summary>Imports iRacing .ibt files into the database (also used to enrich live recordings).</summary>
public sealed class IbtImporter
{
    readonly SessionStore _store;
    public IbtImporter(SessionStore store) { _store = store; }

    public static string DefaultTelemetryFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing", "telemetry");

    public ImportResult Import(string path, bool force = false, CancellationToken ct = default)
    {
        var res = new ImportResult { File = path };
        try
        {
            var fi = new FileInfo(path);
            if (!force && _store.Db.IsFileImported(path, fi.Length)) { res.Skipped = true; return res; }
            using var src = new IbtSource(path);
            var si = src.Session!;
            var tracker = new SessionTracker { SourceName = "ibt" };
            tracker.OnSessionInfo(si);
            tracker.LapCompleted += (ctx, lap) =>
            {
                _store.SaveLap(ctx, lap, "ibt", path);
                if (!res.Sessions.Contains(ctx.DbId)) res.Sessions.Add(ctx.DbId);
                res.Laps++;
                if (lap.Valid) { res.ValidLaps++; _store.OfferLapForModel(si.TrackKey, si, lap); }
            };
            tracker.PitStopCompleted += (ctx, stop) => _store.Db.InsertPitStop(_store.EnsureSession(ctx, "ibt", path), stop);
            int n = 0;
            while (src.Next(0) == SourceStatus.Frame)
            {
                tracker.Process(src.Frame, src.WallClock);
                if ((++n & 4095) == 0) ct.ThrowIfCancellationRequested();
            }
            tracker.Flush();
            _store.Db.MarkFileImported(path, fi.Length, res.Laps);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { res.Error = e.Message; }
        return res;
    }
}
