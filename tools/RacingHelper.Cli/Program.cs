using System.Text.Json;
using RacingHelper.Analysis;
using RacingHelper.Sim;
using RacingHelper.Storage;

if (args.Length == 0)
{
    Console.WriteLine("usage: rh info <file.ibt> | xycheck <file.ibt> | import <db> <file|folder> [max] | sessions <db> | report <db> <sessionId> | corners <db> <trackKey>");
    return;
}

switch (args[0])
{
    case "info": Info(args[1]); break;
    case "xycheck": XyCheck(args[1]); break;
    case "import": ImportCmd(args[1], args[2], args.Length > 3 ? int.Parse(args[3]) : 5); break;
    case "sessions": SessionsCmd(args[1]); break;
    case "report": ReportCmd(args[1], long.Parse(args[2])); break;
    case "corners": CornersCmd(args[1], args[2]); break;
    case "gears": GearsCmd(args[1], long.Parse(args[2])); break;
    case "hub": HubCmd(args[1], args[2], double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture)); break;
    case "serve": await ServeCmd(args[1], int.Parse(args[2])); break;
    case "irating":
        foreach (var field in new[] { new[] { 2000, 2000, 2000, 2000, 2000, 2000, 2000, 2000, 2000, 2000 }, new[] { 3000, 2500, 2000, 1500, 1000 }, new[] { 1000, 1500, 2000, 2500, 3000 } })
            Console.WriteLine(string.Join(" ", field) + " → " + string.Join(" ", RacingHelper.Analysis.IRatingEstimator.Estimate(field).Select(x => x.ToString("+0;-0"))));
        break;
}

// Headless dashboard for testing: hub + web server against a given database, no WPF.
static async Task ServeCmd(string dbPath, int port)
{
    var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath))!;
    var settings = new RacingHelper.SettingsStore(Path.Combine(dir, "settings.json"));
    settings.Current.WebPort = port;
    settings.Current.DataFolder = dir;
    settings.Current.AutoImportIbt = false;
    settings.Current.AutoInstallSetups = false;
    settings.Current.AutoStartDiskTelemetry = false;
    settings.Current.SetupLibraryFolder = Path.Combine(dir, "SetupLibrary");
    settings.Current.IRacingSetupsFolder = Path.Combine(dir, "iRacingSetups");
    var db = new Database(dbPath);
    var store = new SessionStore(db);
    var hub = new RacingHelper.Live.TelemetryHub(settings, store, new AnalysisService(store));
    if (Environment.GetEnvironmentVariable("RH_CC_PORT") is { Length: > 0 } ccPort) settings.Current.CrewChiefPort = int.Parse(ccPort);
    if (Environment.GetEnvironmentVariable("RH_COACH") is { Length: > 0 } coach) settings.Current.CoachingMode = coach;
    hub.Engineer.Said += m =>
    {
        // same routing as the desktop app: CrewChief when connected, otherwise (here) just the console
        if (!m.Speak) Console.WriteLine($"  >> [{m.Category}] {m.Text}");
    };
    hub.Radio.Released += m =>
    {
        bool cc = hub.CrewChief.TryHandle(m);
        Console.WriteLine($"  >> [{m.Category}{(cc ? "/crewchief" : "/voice")}] {m.Text}");
    };
    hub.Start();
    var watcher = new IbtWatcher(new IbtImporter(store), db, () => settings.Current);
    var web = new RacingHelper.Web.WebServer(hub, watcher, new HeadlessBridge());
    await web.StartAsync();
    Console.WriteLine("serving " + web.Url);
    await Task.Delay(Timeout.Infinite);
}

static void HubCmd(string dbPath, string ibt, double speed)
{
    var settings = new RacingHelper.SettingsStore(Path.Combine(Path.GetDirectoryName(dbPath)!, "settings.json"));
    settings.Current.AutoInstallSetups = false;
    settings.Current.DataFolder = Path.GetDirectoryName(Path.GetFullPath(dbPath))!;
    settings.Current.CrewChiefEnabled = false;
    if (Environment.GetEnvironmentVariable("RH_COACH") is { Length: > 0 } coach) settings.Current.CoachingMode = coach;
    if (Environment.GetEnvironmentVariable("RH_VERBOSITY") is { Length: > 0 } verb) settings.Current.VoiceVerbosity = verb;
    if (Environment.GetEnvironmentVariable("RH_RUNLAPS") is { Length: > 0 } rl) settings.Current.SetupRunLaps = int.Parse(rl);
    var store = new SessionStore(new Database(dbPath));
    var hub = new RacingHelper.Live.TelemetryHub(settings, store, new AnalysisService(store));
    hub.Engineer.Said += m => { if (!m.Speak) Console.WriteLine($"  >> [{m.Category}/{m.Priority}] {m.Text}"); };
    hub.Radio.Released += m =>
    {
        var st = hub.State;
        var (inZone, toZone) = RacingHelper.Live.RadioGate.Zone(st.LapDist, st.TrackLength, hub.Model, hub.Reference);
        Console.WriteLine($"  >> [{m.Category}/{m.Priority}/voice] {m.Text}   (said at {st.LapDist:0} m, {(inZone ? "IN CORNER" : $"{toZone:0} m to next corner")}, waited {(DateTime.Now - m.At).TotalSeconds:0.0}s)");
    };
    hub.Start();
    hub.StartReplay(ibt, speed);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    string lastCorner = "";
    while (sw.Elapsed.TotalSeconds < 600)
    {
        Thread.Sleep(200);
        var s = hub.State;
        if (s.LastCorner != null && s.LastCorner.Name + s.LastCorner.Lap != lastCorner)
        {
            lastCorner = s.LastCorner.Name + s.LastCorner.Lap;
            var c = s.LastCorner;
            Console.WriteLine($"     corner {c.Name} lap {c.Lap}: {c.TimeDelta:+0.000;-0.000}s vmin {c.MinSpeed:0} ({c.MinSpeedDiff:+0;-0}) {c.Verdict}");
        }
        if (s.TyreLoad is { } tl && sw.ElapsedMilliseconds % 2000 < 200)
            Console.WriteLine($"     tyres lap {s.Lap} {s.LapPct:P0}: {tl.State} warm={tl.WarmPct:P0} front={tl.FrontLoad:0.00} rear={tl.RearLoad:0.00} [{string.Join(" ", tl.Load.Select(x => x.ToString("0.00")))}]");
        if (sw.ElapsedMilliseconds % 4000 < 200)
            Console.WriteLine($"[{s.Status}] lap {s.Lap} {s.LapPct:P0} cur={Fmt.LapTime(s.CurrentLapTime)} delta={s.Delta:+0.000;-0.000} pred={Fmt.LapTime(s.PredictedLap)} ref='{s.ReferenceLabel}' brakeIn={s.NextBrakeDist:0}m {s.NextCorner} fuel={s.Fuel?.PerLapAvg:0.00}L/lap {s.Fuel?.LapsInTank:0.0} laps sectors=[{string.Join(" ", s.Sectors.Select(x => $"{x.State[0]}{x.Delta:+0.00;-0.00}{x.Color}"))}] tyres={string.Join(",", s.Tyres.Select(t => $"{t.Pressure:0}/{t.TempMid:0}"))} wx={s.Weather.TrackTemp:0}C {s.Weather.Wetness}");
        if (s.Status == "waiting" && sw.Elapsed.TotalSeconds > 3) break;
    }
    hub.Dispose();
}

static void GearsCmd(string dbPath, long lapId)
{
    var db = new Database(dbPath);
    var d = db.GetLapData(lapId)!;
    var g = d[RacingHelper.Recording.Ch.Gear]; var rpm = d[RacingHelper.Recording.Ch.Rpm]; var dist = d[RacingHelper.Recording.Ch.D];
    for (int i = 1; i < d.Count; i++) if (g[i] != g[i - 1]) Console.Write($"[{dist[i]:0}m {g[i - 1]}->{g[i]} @{rpm[i - 1]:0}] ");
    Console.WriteLine();
}

static void ImportCmd(string dbPath, string target, int max)
{
    var store = new SessionStore(new Database(dbPath));
    var imp = new IbtImporter(store);
    var files = Directory.Exists(target)
        ? new DirectoryInfo(target).GetFiles("*.ibt").OrderByDescending(f => f.LastWriteTime).Take(max).Select(f => f.FullName).ToList()
        : new List<string> { target };
    foreach (var f in files)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = imp.Import(f, force: true);
        Console.WriteLine($"{Path.GetFileName(f)}: laps={r.Laps} valid={r.ValidLaps} sessions={string.Join(",", r.Sessions)} err={r.Error} {sw.ElapsedMilliseconds}ms");
    }
    Console.WriteLine($"db size: {new FileInfo(dbPath).Length / 1024} KB");
}

static void SessionsCmd(string dbPath)
{
    var db = new Database(dbPath);
    foreach (var s in db.GetSessions())
        Console.WriteLine($"#{s.Id} {s.StartedAt:g} {s.CarName} @ {s.TrackName} {s.SessionType} laps={s.LapCount} valid={s.ValidLaps} best={Fmt.LapTime(s.BestLap)} driver={s.DriverName}");
}

static void ReportCmd(string dbPath, long id)
{
    var store = new SessionStore(new Database(dbPath));
    var svc = new AnalysisService(store);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var (info, _, _) = svc.SessionContext(id);
    Console.WriteLine($"info={(info != null)} shift={info?.ShiftRpm} blink={info?.ShiftLightBlinkRpm}");
    var rep = svc.SessionReport(id);
    Console.WriteLine($"report in {sw.ElapsedMilliseconds} ms");
    if (rep == null) { Console.WriteLine("no session"); return; }
    Console.WriteLine($"best={Fmt.LapTime(rep.BestLap)} avg={Fmt.LapTime(rep.AverageLap)} sd={rep.StdDev:0.000} theo={Fmt.LapTime(rep.TheoreticalBest)} optSectors={Fmt.LapTime(rep.OptimalSectors)} ref={rep.ReferenceLabel}");
    foreach (var st in rep.Stints) Console.WriteLine($"  stint {st.Stint}: laps={st.Laps} valid={st.ValidLaps} best={Fmt.LapTime(st.Best)} trend={st.TrendPerLap:0.000} fuel={st.FuelPerLap:0.00} times={string.Join(" ", st.LapTimes.Select(t => t.ToString("0.0")))}");
    foreach (var c in rep.Corners) Console.WriteLine($"  {c.Name}: best={c.BestTime:0.000} avg={c.AvgTime:0.000} loss={c.AvgLossToBest:0.000} vmin={c.MinSpeedBest:0}/{c.MinSpeedAvg:0} bpSpread={c.BrakePointSpread:0.0} lock={c.Lockups} {(c.VsReference != null ? $"vsRef={c.VsReference.TimeDelta:+0.000;-0.000} {c.VsReference.Verdict}" : "")}");
    var s = rep.Style;
    if (s != null) Console.WriteLine($"  style: full={s.FullThrottlePct:0.0}% brake={s.BrakingPct:0.0}% coast={s.CoastingPct:0.0}% overlap={s.OverlapPct:0.0}% trail={s.TrailBrakePct:0}% upshift={s.UpshiftRpm:0}x{s.Upshifts} abs={s.AbsPct:0}% lock={s.Lockups} spin={s.Wheelspins} maxLat={s.MaxLatG:0.00}g top={s.TopSpeed * 3.6:0}");
    if (rep.Handling != null) Console.WriteLine($"  handling valid={rep.Handling.Valid} K={rep.Handling.SteerRatioK:0.00} cs={rep.Handling.CountersteerEvents} cells: {string.Join(" | ", rep.Handling.Cells.Select(c => $"{c.Phase}/{c.SpeedBand}: us={c.UndersteerRate:0.0}% os={c.OversteerRate:0.0}% cs={c.Countersteer} n={c.Samples} {c.Tendency}"))}");
    if (rep.Handling != null) Console.WriteLine($"  hotspots: {string.Join(", ", rep.Handling.HotSpots.Select(h => $"{h.Corner}/{h.Phase}/{h.Kind}x{h.Count}"))}");
    foreach (var t in rep.Tyres) Console.WriteLine($"  {t.Tyre}: hot={t.PressHot:0.0} cold={t.PressCold:0.0} {t.PressStatus} T={t.TempAvg:0.0} spread={t.Spread:0.0} mid={t.MidVsEdges:0.0} wear={t.Wear:0.000} sugg={t.SuggestedCold:0}");
    Console.WriteLine("INSIGHTS:");
    foreach (var i in rep.Insights) Console.WriteLine($"  [{i.Category}/{i.Severity}] {i.Title} — {i.Detail}");
}

static void CornersCmd(string dbPath, string trackKey)
{
    var store = new SessionStore(new Database(dbPath));
    var m = store.GetTrackModel(trackKey);
    if (m == null) { Console.WriteLine("no model"); return; }
    Console.WriteLine($"len={m.Length} gps={m.FromGps} pts={m.X.Length} corners={m.Corners.Count}");
    foreach (var c in m.Corners) Console.WriteLine($"  {c.Name} dir={c.Dir} seg=[{c.SegStart:0},{c.SegEnd:0}] region=[{c.Start:0},{c.End:0}] apex={c.Apex:0} vmin={c.RefMinSpeed * 3.6:0} brake={c.Braking}");
}

static void Info(string path)
{
    using var ibt = IbtFile.Open(path);
    var si = new SessionInfo(ibt.SessionYaml);
    Console.WriteLine($"records={ibt.RecordCount} laps={ibt.LapCount} start={ibt.SessionStartLocal} vars={ibt.Vars.All.Count}");
    Console.WriteLine($"track={si.TrackLabel} len={si.TrackLengthM} key={si.TrackKey} car={si.CarName} ({si.CarPath}) class={si.CarClass} cat={si.CarCategory}");
    Console.WriteLine($"event={si.EventType} subsession={si.SubSessionId} sessions={string.Join(",", si.Sessions.Select(s => $"{s.Num}:{s.Type}:{s.Laps}:{s.TimeSec}"))} current={si.CurrentSessionNum}");
    Console.WriteLine($"player={si.Player?.Name} ir={si.Player?.IRating} drivers={si.Drivers.Count} sectors={string.Join(",", si.SectorStarts)} tank={si.TankCapacityL}");
}

// Verifies that integrating velocity + yaw reproduces the GPS path (used when live telemetry has no GPS).
static void XyCheck(string path)
{
    using var src = new IbtSource(path);
    var si = src.Session!;
    double lat0 = si.TrackLat, lon0 = si.TrackLon;
    double mPerDegLat = 111132.92, mPerDegLon = 111412.84 * Math.Cos(lat0 * Math.PI / 180);
    double x = 0, y = 0, lastT = double.NaN, dist = 0;
    bool init = false;
    while (src.Next(0) == SourceStatus.Frame)
    {
        var f = src.Frame;
        if (!f.IsOnTrack || !f.HasGps || f.Speed < 1) { lastT = double.NaN; continue; }
        double gx = (f.Lon - lon0) * mPerDegLon, gy = (f.Lat - lat0) * mPerDegLat;
        if (!init || double.IsNaN(lastT)) { x = gx; y = gy; lastT = f.SessionTime; init = true; continue; }
        double dt = f.SessionTime - lastT; lastT = f.SessionTime;
        dist += f.Speed * dt;
        double h = f.YawNorth;
        x += (f.VelX * Math.Sin(h) - f.VelY * Math.Cos(h)) * dt;
        y += (f.VelX * Math.Cos(h) + f.VelY * Math.Sin(h)) * dt;
        if (dist > 4000) { Console.WriteLine($"error after {dist:F0} m = {Math.Sqrt((x - gx) * (x - gx) + (y - gy) * (y - gy)):F1} m"); break; }
    }
}
