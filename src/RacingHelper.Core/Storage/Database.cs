using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RacingHelper.Recording;

namespace RacingHelper.Storage;

public sealed class SessionRow
{
    public long Id { get; set; }
    public string Key { get; set; } = "";
    public string CarPath { get; set; } = "";
    public string CarName { get; set; } = "";
    public string CarClass { get; set; } = "";
    public string CarCategory { get; set; } = "";
    public string TrackKey { get; set; } = "";
    public string TrackName { get; set; } = "";
    public string TrackConfig { get; set; } = "";
    public float TrackLength { get; set; }
    public string SessionType { get; set; } = "";
    public string SessionName { get; set; } = "";
    public string EventType { get; set; } = "";
    public long SubSessionId { get; set; }
    public bool Official { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime EndedAt { get; set; }
    public string DriverName { get; set; } = "";
    public int DriverId { get; set; }
    public int IRating { get; set; }
    public string Source { get; set; } = "live";
    public string SourceFile { get; set; } = "";
    public string Notes { get; set; } = "";
    // aggregates (filled by queries)
    public int LapCount { get; set; }
    public int ValidLaps { get; set; }
    public double BestLap { get; set; }
}

public sealed class LapRow
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public int LapNumber { get; set; }
    public int Stint { get; set; }
    public DateTime StartedAt { get; set; }
    public double LapTime { get; set; }
    public bool Valid { get; set; }
    public bool OutLap { get; set; }
    public bool InLap { get; set; }
    public string InvalidReason { get; set; } = "";
    public int Incidents { get; set; }
    public int OffTracks { get; set; }
    public float? FuelUsed { get; set; }
    public float? FuelStart { get; set; }
    public float? AirTemp { get; set; }
    public float? TrackTemp { get; set; }
    public int Wetness { get; set; }
    public float[] SectorTimes { get; set; } = Array.Empty<float>();
    public TyreLapStats[]? Tyres { get; set; }
    public float? BrakeBias { get; set; }
    public float MaxSpeed { get; set; }
    public int SetupVersion { get; set; }
    public string SetupHash { get; set; } = "";
    public bool HasGps { get; set; }
    public string Source { get; set; } = "";
    public int Compound { get; set; }
    public Dictionary<string, double> Metrics { get; set; } = new();
    // joined session fields (for leaderboards)
    public string? CarPath { get; set; }
    public string? TrackKey { get; set; }
    public string? DriverName { get; set; }
    public int DriverId { get; set; }
    public string? SessionType { get; set; }
}

public sealed class Database
{
    readonly string _cs;
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        IncludeFields = true,
    };

    public Database(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _cs = new SqliteConnectionStringBuilder { DataSource = path, Cache = SqliteCacheMode.Shared, Pooling = true }.ToString();
        using var c = Open();
        Exec(c, "PRAGMA journal_mode=WAL;");
        Exec(c, """
            CREATE TABLE IF NOT EXISTS sessions(
              id INTEGER PRIMARY KEY AUTOINCREMENT, key TEXT UNIQUE NOT NULL,
              car_path TEXT, car_name TEXT, car_class TEXT, car_category TEXT,
              track_key TEXT, track_name TEXT, track_config TEXT, track_length REAL,
              session_type TEXT, session_name TEXT, event_type TEXT, subsession_id INTEGER, official INTEGER,
              started_at INTEGER, ended_at INTEGER, driver_name TEXT, driver_id INTEGER, irating INTEGER,
              source TEXT, source_file TEXT, notes TEXT, info BLOB);
            CREATE TABLE IF NOT EXISTS laps(
              id INTEGER PRIMARY KEY AUTOINCREMENT, session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
              lap_number INTEGER, stint INTEGER, started_at INTEGER, lap_time REAL, valid INTEGER, out_lap INTEGER, in_lap INTEGER,
              invalid_reason TEXT, incidents INTEGER, off_tracks INTEGER, fuel_used REAL, fuel_start REAL,
              air_temp REAL, track_temp REAL, wetness INTEGER, sectors TEXT, tyres TEXT, brake_bias REAL, max_speed REAL,
              setup_version INTEGER, setup_hash TEXT, has_gps INTEGER, source TEXT, compound INTEGER, metrics TEXT);
            CREATE INDEX IF NOT EXISTS ix_laps_session ON laps(session_id);
            CREATE TABLE IF NOT EXISTS lap_data(lap_id INTEGER PRIMARY KEY REFERENCES laps(id) ON DELETE CASCADE, blob BLOB);
            CREATE TABLE IF NOT EXISTS track_models(track_key TEXT PRIMARY KEY, json TEXT, updated_at INTEGER);
            CREATE TABLE IF NOT EXISTS setups(hash TEXT PRIMARY KEY, car_path TEXT, name TEXT, yaml TEXT, first_seen INTEGER);
            CREATE TABLE IF NOT EXISTS pit_stops(id INTEGER PRIMARY KEY AUTOINCREMENT, session_id INTEGER REFERENCES sessions(id) ON DELETE CASCADE,
              lap INTEGER, at INTEGER, stationary REAL, fuel_added REAL, tyres_changed INTEGER);
            CREATE TABLE IF NOT EXISTS imported_files(path TEXT PRIMARY KEY, size INTEGER, imported_at INTEGER, laps INTEGER);
            CREATE TABLE IF NOT EXISTS kv(key TEXT PRIMARY KEY, value TEXT);
            """);
    }

    SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        Exec(c, "PRAGMA foreign_keys=ON;");
        return c;
    }

    static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static SqliteCommand Cmd(SqliteConnection c, string sql, params (string, object?)[] ps)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    static long Ms(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds();
    static DateTime FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
    static object? Nf(float v) => float.IsFinite(v) ? v : null;

    // ---------------- sessions ----------------

    public long UpsertSession(SessionRow s, string? infoYaml)
    {
        using var c = Open();
        using var find = Cmd(c, "SELECT id FROM sessions WHERE key=$k", ("$k", s.Key));
        var existing = find.ExecuteScalar();
        if (existing is long id)
        {
            using var up = Cmd(c, "UPDATE sessions SET ended_at=MAX(ended_at,$e), info=COALESCE($info,info), source_file=CASE WHEN $sf<>'' THEN $sf ELSE source_file END WHERE id=$id",
                ("$e", Ms(s.EndedAt)), ("$info", infoYaml == null ? null : Compress(infoYaml)), ("$sf", s.SourceFile), ("$id", id));
            up.ExecuteNonQuery();
            return id;
        }
        using var ins = Cmd(c, """
            INSERT INTO sessions(key,car_path,car_name,car_class,car_category,track_key,track_name,track_config,track_length,session_type,session_name,event_type,
              subsession_id,official,started_at,ended_at,driver_name,driver_id,irating,source,source_file,notes,info)
            VALUES($key,$cp,$cn,$cc,$cat,$tk,$tn,$tc,$tl,$st,$sn,$et,$ss,$off,$sa,$ea,$dn,$did,$ir,$src,$sf,'',$info);
            SELECT last_insert_rowid();
            """,
            ("$key", s.Key), ("$cp", s.CarPath), ("$cn", s.CarName), ("$cc", s.CarClass), ("$cat", s.CarCategory), ("$tk", s.TrackKey), ("$tn", s.TrackName),
            ("$tc", s.TrackConfig), ("$tl", s.TrackLength), ("$st", s.SessionType), ("$sn", s.SessionName), ("$et", s.EventType), ("$ss", s.SubSessionId),
            ("$off", s.Official ? 1 : 0), ("$sa", Ms(s.StartedAt)), ("$ea", Ms(s.EndedAt)), ("$dn", s.DriverName), ("$did", s.DriverId), ("$ir", s.IRating),
            ("$src", s.Source), ("$sf", s.SourceFile), ("$info", infoYaml == null ? null : Compress(infoYaml)));
        return (long)ins.ExecuteScalar()!;
    }

    const string SessionSelect = """
        SELECT s.id,s.key,s.car_path,s.car_name,s.car_class,s.car_category,s.track_key,s.track_name,s.track_config,s.track_length,
               s.session_type,s.session_name,s.event_type,s.subsession_id,s.official,s.started_at,s.ended_at,s.driver_name,s.driver_id,s.irating,
               s.source,s.source_file,s.notes,
               (SELECT COUNT(*) FROM laps l WHERE l.session_id=s.id),
               (SELECT COUNT(*) FROM laps l WHERE l.session_id=s.id AND l.valid=1),
               (SELECT MIN(lap_time) FROM laps l WHERE l.session_id=s.id AND l.valid=1)
        FROM sessions s
        """;

    static SessionRow ReadSession(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0), Key = r.GetString(1), CarPath = Str(r, 2), CarName = Str(r, 3), CarClass = Str(r, 4), CarCategory = Str(r, 5),
        TrackKey = Str(r, 6), TrackName = Str(r, 7), TrackConfig = Str(r, 8), TrackLength = r.IsDBNull(9) ? 0 : (float)r.GetDouble(9),
        SessionType = Str(r, 10), SessionName = Str(r, 11), EventType = Str(r, 12), SubSessionId = r.IsDBNull(13) ? 0 : r.GetInt64(13),
        Official = !r.IsDBNull(14) && r.GetInt64(14) == 1, StartedAt = FromMs(r.GetInt64(15)), EndedAt = FromMs(r.GetInt64(16)),
        DriverName = Str(r, 17), DriverId = r.IsDBNull(18) ? 0 : r.GetInt32(18), IRating = r.IsDBNull(19) ? 0 : r.GetInt32(19),
        Source = Str(r, 20), SourceFile = Str(r, 21), Notes = Str(r, 22),
        LapCount = r.GetInt32(23), ValidLaps = r.GetInt32(24), BestLap = r.IsDBNull(25) ? 0 : r.GetDouble(25),
    };

    static string Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);

    public List<SessionRow> GetSessions(string? carPath = null, string? trackKey = null, int limit = 500, bool includeEmpty = false)
    {
        using var c = Open();
        var where = new List<string>();
        if (!includeEmpty) where.Add("EXISTS(SELECT 1 FROM laps l WHERE l.session_id=s.id)");
        if (!string.IsNullOrEmpty(carPath)) where.Add("s.car_path=$cp");
        if (!string.IsNullOrEmpty(trackKey)) where.Add("s.track_key=$tk");
        using var cmd = Cmd(c, SessionSelect + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") + " ORDER BY s.started_at DESC LIMIT $lim",
            ("$cp", carPath), ("$tk", trackKey), ("$lim", limit));
        using var r = cmd.ExecuteReader();
        var list = new List<SessionRow>();
        while (r.Read()) list.Add(ReadSession(r));
        return list;
    }

    public SessionRow? GetSession(long id)
    {
        using var c = Open();
        using var cmd = Cmd(c, SessionSelect + " WHERE s.id=$id", ("$id", id));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadSession(r) : null;
    }

    public SessionRow? GetSessionByKey(string key)
    {
        using var c = Open();
        using var cmd = Cmd(c, SessionSelect + " WHERE s.key=$k", ("$k", key));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadSession(r) : null;
    }

    public string? GetSessionInfoYaml(long id)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT info FROM sessions WHERE id=$id", ("$id", id));
        return cmd.ExecuteScalar() is byte[] b ? Decompress(b) : null;
    }

    public void SetSessionNotes(long id, string notes)
    {
        using var c = Open();
        using var cmd = Cmd(c, "UPDATE sessions SET notes=$n WHERE id=$id", ("$n", notes), ("$id", id));
        cmd.ExecuteNonQuery();
    }

    public void DeleteSession(long id)
    {
        using var c = Open();
        using var cmd = Cmd(c, "DELETE FROM sessions WHERE id=$id", ("$id", id));
        cmd.ExecuteNonQuery();
    }

    public void DeleteEmptySessions()
    {
        using var c = Open();
        Exec(c, "DELETE FROM sessions WHERE id NOT IN (SELECT DISTINCT session_id FROM laps)");
    }

    // ---------------- laps ----------------

    public long InsertLap(long sessionId, RecordedLap lap)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var ins = Cmd(c, """
            INSERT INTO laps(session_id,lap_number,stint,started_at,lap_time,valid,out_lap,in_lap,invalid_reason,incidents,off_tracks,fuel_used,fuel_start,
              air_temp,track_temp,wetness,sectors,tyres,brake_bias,max_speed,setup_version,setup_hash,has_gps,source,compound,metrics)
            VALUES($sid,$ln,$st,$sa,$lt,$v,$ol,$il,$ir,$inc,$off,$fu,$fs,$at,$tt,$wet,$sec,$ty,$bb,$ms,$sv,$sh,$gps,$src,$cmp,$met);
            SELECT last_insert_rowid();
            """,
            ("$sid", sessionId), ("$ln", lap.LapNumber), ("$st", lap.Stint), ("$sa", Ms(lap.StartedAt)), ("$lt", lap.LapTime), ("$v", lap.Valid ? 1 : 0),
            ("$ol", lap.OutLap ? 1 : 0), ("$il", lap.InLap ? 1 : 0), ("$ir", lap.InvalidReason), ("$inc", lap.Incidents), ("$off", lap.OffTracks),
            ("$fu", Nf(lap.FuelUsed)), ("$fs", Nf(lap.FuelStart)), ("$at", Nf(lap.AirTemp)), ("$tt", Nf(lap.TrackTemp)), ("$wet", lap.TrackWetness),
            ("$sec", JsonSerializer.Serialize(lap.SectorTimes, Json)), ("$ty", lap.Tyres == null ? null : JsonSerializer.Serialize(lap.Tyres, Json)),
            ("$bb", Nf(lap.BrakeBias)), ("$ms", lap.MaxSpeed), ("$sv", lap.SetupVersion), ("$sh", lap.SetupHash), ("$gps", lap.HasGps ? 1 : 0),
            ("$src", lap.Source), ("$cmp", lap.TyreCompound), ("$met", JsonSerializer.Serialize(lap.Metrics, Json)));
        ins.Transaction = tx;
        long id = (long)ins.ExecuteScalar()!;
        if (lap.Data != null)
        {
            using var data = Cmd(c, "INSERT OR REPLACE INTO lap_data(lap_id,blob) VALUES($id,$b)", ("$id", id), ("$b", lap.Data.Serialize()));
            data.Transaction = tx;
            data.ExecuteNonQuery();
        }
        tx.Commit();
        lap.Id = id;
        lap.SessionId = sessionId;
        return id;
    }

    public void UpdateLapMetrics(long lapId, Dictionary<string, double> metrics)
    {
        using var c = Open();
        using var cmd = Cmd(c, "UPDATE laps SET metrics=$m WHERE id=$id", ("$m", JsonSerializer.Serialize(metrics, Json)), ("$id", lapId));
        cmd.ExecuteNonQuery();
    }

    public void DeleteLap(long lapId)
    {
        using var c = Open();
        using var cmd = Cmd(c, "DELETE FROM laps WHERE id=$id", ("$id", lapId));
        cmd.ExecuteNonQuery();
    }

    const string LapSelect = """
        SELECT l.id,l.session_id,l.lap_number,l.stint,l.started_at,l.lap_time,l.valid,l.out_lap,l.in_lap,l.invalid_reason,l.incidents,l.off_tracks,
               l.fuel_used,l.fuel_start,l.air_temp,l.track_temp,l.wetness,l.sectors,l.tyres,l.brake_bias,l.max_speed,l.setup_version,l.setup_hash,
               l.has_gps,l.source,l.compound,l.metrics,s.car_path,s.track_key,s.driver_name,s.driver_id,s.session_type
        FROM laps l JOIN sessions s ON s.id=l.session_id
        """;

    static float? F(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : (float)r.GetDouble(i);

    static LapRow ReadLap(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0), SessionId = r.GetInt64(1), LapNumber = r.GetInt32(2), Stint = r.GetInt32(3), StartedAt = FromMs(r.GetInt64(4)),
        LapTime = r.GetDouble(5), Valid = r.GetInt64(6) == 1, OutLap = r.GetInt64(7) == 1, InLap = r.GetInt64(8) == 1, InvalidReason = Str(r, 9),
        Incidents = r.GetInt32(10), OffTracks = r.GetInt32(11), FuelUsed = F(r, 12), FuelStart = F(r, 13), AirTemp = F(r, 14), TrackTemp = F(r, 15),
        Wetness = r.IsDBNull(16) ? -1 : r.GetInt32(16),
        SectorTimes = r.IsDBNull(17) ? Array.Empty<float>() : JsonSerializer.Deserialize<float[]>(r.GetString(17), Json) ?? Array.Empty<float>(),
        Tyres = r.IsDBNull(18) ? null : JsonSerializer.Deserialize<TyreLapStats[]>(r.GetString(18), Json),
        BrakeBias = F(r, 19), MaxSpeed = F(r, 20) ?? 0, SetupVersion = r.IsDBNull(21) ? 0 : r.GetInt32(21), SetupHash = Str(r, 22),
        HasGps = r.GetInt64(23) == 1, Source = Str(r, 24), Compound = r.IsDBNull(25) ? -1 : r.GetInt32(25),
        Metrics = r.IsDBNull(26) ? new() : JsonSerializer.Deserialize<Dictionary<string, double>>(r.GetString(26), Json) ?? new(),
        CarPath = Str(r, 27), TrackKey = Str(r, 28), DriverName = Str(r, 29), DriverId = r.IsDBNull(30) ? 0 : r.GetInt32(30), SessionType = Str(r, 31),
    };

    public List<LapRow> GetLaps(long sessionId)
    {
        using var c = Open();
        using var cmd = Cmd(c, LapSelect + " WHERE l.session_id=$s ORDER BY l.started_at", ("$s", sessionId));
        return ReadLaps(cmd);
    }

    public LapRow? GetLap(long id)
    {
        using var c = Open();
        using var cmd = Cmd(c, LapSelect + " WHERE l.id=$id", ("$id", id));
        return ReadLaps(cmd).FirstOrDefault();
    }

    /// <summary>Valid laps for a car/track, fastest first. driverId: null = anyone, otherwise filter.</summary>
    public List<LapRow> GetBestLaps(string carPath, string trackKey, int limit = 50, int? driverId = null, bool onlyWithData = true)
    {
        using var c = Open();
        string sql = LapSelect + " WHERE s.car_path=$cp AND s.track_key=$tk AND l.valid=1" + (driverId.HasValue ? " AND s.driver_id=$d" : "")
                     + (onlyWithData ? " AND EXISTS(SELECT 1 FROM lap_data d WHERE d.lap_id=l.id)" : "") + " ORDER BY l.lap_time LIMIT $lim";
        using var cmd = Cmd(c, sql, ("$cp", carPath), ("$tk", trackKey), ("$d", driverId), ("$lim", limit));
        return ReadLaps(cmd);
    }

    /// <summary>Best valid lap per (car, track, driver).</summary>
    public List<LapRow> GetPersonalBests()
    {
        using var c = Open();
        using var cmd = Cmd(c, LapSelect + """
             WHERE l.valid=1 AND l.id IN (
               SELECT (SELECT l2.id FROM laps l2 JOIN sessions s2 ON s2.id=l2.session_id
                       WHERE l2.valid=1 AND s2.car_path=s.car_path AND s2.track_key=s.track_key AND s2.driver_id=s.driver_id ORDER BY l2.lap_time LIMIT 1)
               FROM sessions s GROUP BY s.car_path, s.track_key, s.driver_id)
             ORDER BY s.track_key, s.car_path, l.lap_time
            """);
        return ReadLaps(cmd);
    }

    static List<LapRow> ReadLaps(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<LapRow>();
        while (r.Read()) list.Add(ReadLap(r));
        return list;
    }

    public LapData? GetLapData(long lapId)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT blob FROM lap_data WHERE lap_id=$id", ("$id", lapId));
        return cmd.ExecuteScalar() is byte[] b ? LapData.Deserialize(b) : null;
    }

    public void ReplaceLapData(long lapId, LapData data)
    {
        using var c = Open();
        using var cmd = Cmd(c, "INSERT OR REPLACE INTO lap_data(lap_id,blob) VALUES($id,$b)", ("$id", lapId), ("$b", data.Serialize()));
        cmd.ExecuteNonQuery();
    }

    /// <summary>All (car, track) combinations that have laps, with counts and personal best.</summary>
    public List<(string carPath, string carName, string trackKey, string trackName, string trackConfig, int laps, double best, DateTime last)> GetCombos(int? driverId)
    {
        using var c = Open();
        using var cmd = Cmd(c, """
            SELECT s.car_path, MAX(s.car_name), s.track_key, MAX(s.track_name), MAX(s.track_config), COUNT(l.id), MIN(CASE WHEN l.valid=1 THEN l.lap_time END), MAX(s.started_at)
            FROM sessions s JOIN laps l ON l.session_id=s.id
            WHERE ($d IS NULL OR s.driver_id=$d)
            GROUP BY s.car_path, s.track_key ORDER BY MAX(s.started_at) DESC
            """, ("$d", driverId));
        using var r = cmd.ExecuteReader();
        var list = new List<(string, string, string, string, string, int, double, DateTime)>();
        while (r.Read())
            list.Add((Str(r, 0), Str(r, 1), Str(r, 2), Str(r, 3), Str(r, 4), r.GetInt32(5), r.IsDBNull(6) ? 0 : r.GetDouble(6), FromMs(r.GetInt64(7))));
        return list;
    }

    // ---------------- pit stops ----------------

    public void InsertPitStop(long sessionId, PitStopInfo p)
    {
        using var c = Open();
        using var cmd = Cmd(c, "INSERT INTO pit_stops(session_id,lap,at,stationary,fuel_added,tyres_changed) VALUES($s,$l,$a,$st,$f,$t)",
            ("$s", sessionId), ("$l", p.Lap), ("$a", Ms(p.At)), ("$st", p.StationaryTime), ("$f", p.FuelAdded), ("$t", p.TyresChanged ? 1 : 0));
        cmd.ExecuteNonQuery();
    }

    public List<PitStopInfo> GetPitStops(long sessionId)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT lap,at,stationary,fuel_added,tyres_changed FROM pit_stops WHERE session_id=$s ORDER BY at", ("$s", sessionId));
        using var r = cmd.ExecuteReader();
        var list = new List<PitStopInfo>();
        while (r.Read())
            list.Add(new PitStopInfo { Lap = r.GetInt32(0), At = FromMs(r.GetInt64(1)), StationaryTime = r.GetDouble(2), FuelAdded = (float)r.GetDouble(3), TyresChanged = r.GetInt64(4) == 1 });
        return list;
    }

    // ---------------- track models / setups / kv ----------------

    public string? GetTrackModelJson(string trackKey)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT json FROM track_models WHERE track_key=$k", ("$k", trackKey));
        return cmd.ExecuteScalar() as string;
    }

    public void SaveTrackModelJson(string trackKey, string json)
    {
        using var c = Open();
        using var cmd = Cmd(c, "INSERT OR REPLACE INTO track_models(track_key,json,updated_at) VALUES($k,$j,$t)", ("$k", trackKey), ("$j", json), ("$t", Ms(DateTime.Now)));
        cmd.ExecuteNonQuery();
    }

    public void DeleteTrackModel(string trackKey)
    {
        using var c = Open();
        using var cmd = Cmd(c, "DELETE FROM track_models WHERE track_key=$k", ("$k", trackKey));
        cmd.ExecuteNonQuery();
    }

    public void SaveSetup(string hash, string carPath, string name, string yaml)
    {
        using var c = Open();
        using var cmd = Cmd(c, "INSERT OR IGNORE INTO setups(hash,car_path,name,yaml,first_seen) VALUES($h,$c,$n,$y,$t)",
            ("$h", hash), ("$c", carPath), ("$n", name), ("$y", yaml), ("$t", Ms(DateTime.Now)));
        cmd.ExecuteNonQuery();
    }

    public (string name, string yaml)? GetSetup(string hash)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT name,yaml FROM setups WHERE hash=$h", ("$h", hash));
        using var r = cmd.ExecuteReader();
        return r.Read() ? (Str(r, 0), Str(r, 1)) : null;
    }

    public bool IsFileImported(string path, long size)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT size FROM imported_files WHERE path=$p", ("$p", path));
        return cmd.ExecuteScalar() is long s && s == size;
    }

    public void MarkFileImported(string path, long size, int laps)
    {
        using var c = Open();
        using var cmd = Cmd(c, "INSERT OR REPLACE INTO imported_files(path,size,imported_at,laps) VALUES($p,$s,$t,$l)", ("$p", path), ("$s", size), ("$t", Ms(DateTime.Now)), ("$l", laps));
        cmd.ExecuteNonQuery();
    }

    public string? GetKv(string key)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT value FROM kv WHERE key=$k", ("$k", key));
        return cmd.ExecuteScalar() as string;
    }

    public void SetKv(string key, string value)
    {
        using var c = Open();
        using var cmd = Cmd(c, "INSERT OR REPLACE INTO kv(key,value) VALUES($k,$v)", ("$k", key), ("$v", value));
        cmd.ExecuteNonQuery();
    }

    static byte[] Compress(string s)
    {
        using var ms = new MemoryStream();
        using (var b = new BrotliStream(ms, CompressionLevel.Fastest, true)) b.Write(Encoding.UTF8.GetBytes(s));
        return ms.ToArray();
    }

    static string Decompress(byte[] b)
    {
        using var input = new BrotliStream(new MemoryStream(b), CompressionMode.Decompress);
        using var sr = new StreamReader(input, Encoding.UTF8);
        return sr.ReadToEnd();
    }
}
