using System.Collections.Concurrent;
using RacingHelper.Analysis;
using RacingHelper.Recording;
using RacingHelper.Setups;
using RacingHelper.Sim;
using RacingHelper.Storage;

namespace RacingHelper.Live;

/// <summary>
/// The live engine: reads telemetry (iRacing or an .ibt replay), records laps, computes delta / sectors /
/// corner feedback / fuel / standings in real time, drives the race engineer and publishes <see cref="LiveState"/>.
/// All live computation happens on one thread; database work runs on a background worker.
/// </summary>
public sealed class TelemetryHub : IDisposable
{
    public SettingsStore Settings { get; }
    public SessionStore Store { get; }
    public AnalysisService Analysis { get; }
    public RaceEngineer Engineer { get; }
    public SetupInstaller Setups { get; }
    public TyreManager TyreManager { get; }
    public DrivingCoach Coach { get; }
    public CarManager CarManager { get; }
    public SetupEngineer SetupEngineer { get; }
    public Insights Insights { get; }
    public InCarAdvisor InCarAdvisor { get; }
    public PitAutoService Pit { get; }
    public CrewChiefBridge CrewChief { get; }
    /// <summary>Spoken messages come out here (after waiting for a straight); subscribe to this for voice output.</summary>
    public RadioGate Radio { get; }

    volatile LiveState _state = new();
    public LiveState State => _state;
    public TrackModel? Model => _model;
    public SessionInfo? Info => _info;
    public ReferenceLap? Reference => _ref;

    readonly SessionTracker _tracker = new();
    readonly IRacingLiveSource _live = new();
    ITelemetrySource _source;
    volatile ITelemetrySource? _pendingSource;
    volatile bool _stop;
    Thread? _thread;
    readonly BlockingCollection<Action> _work = new();
    Thread? _worker;

    // session-level live state
    SessionInfo? _info;
    int _infoVersion = -1;
    volatile TrackModel? _model;
    volatile ReferenceLap? _ref;
    double _pb = double.NaN, _sessionBest = double.NaN, _lastLap = double.NaN, _lastLapDelta = double.NaN;
    RecordedLap? _lastValidLap;
    bool _greeted;
    string _carTrack = "";
    readonly List<double> _fuelPerLap = new();
    double[] _bestSectors = Array.Empty<double>();
    float[] _sectorStarts = { 0f };
    readonly WeatherTrend _weather = new();
    float _sessionStartTrackTemp = float.NaN;
    bool _rainAnnounced;

    // current lap buffer
    DistLap? _cur;
    double _curLapStart = double.NaN;
    int _lastIdx = -1;
    float _prevD, _prevT, _prevSpeed, _prevThr, _prevBrk, _prevSteer, _prevRpm, _prevLat, _prevYaw;
    int _nextCorner;
    readonly List<CornerLive> _lapCorners = new();
    CornerLive? _lastCorner;

    // events & health
    uint _prevFlags;
    int _prevIncidents = -1;
    int _incidentLapWatch = -1;
    double _paceBeforeIncident = double.NaN;
    readonly List<double> _postIncidentLaps = new();
    bool _possibleDamage;
    float _paceLoss;
    uint _prevWarnings;
    bool _diskLoggingRequested;
    float _prevRepair;
    bool _refWet;
    double _historyFuelPerLap = double.NaN;
    float _lapFuelStart = float.NaN, _lapStartPct;
    double _prevFrameTime = double.NaN;
    PitStopInfo? _lastStop;
    LapComparison? _lastCmp;
    readonly List<double> _recentLaps = new();

    // input history ring buffers (~30 Hz, 6 s)
    const int Hist = 180;
    readonly float[] _hThr = new float[Hist], _hBrk = new float[Hist], _hSteer = new float[Hist], _hSpd = new float[Hist];
    readonly float[] _hRThr = new float[Hist], _hRBrk = new float[Hist], _hRSpd = new float[Hist];
    int _hPos, _frameCount;

    // driven line in the map frame (for the line-comparison overlay)
    const int TrailN = 150;
    readonly float[] _trailX = new float[TrailN], _trailY = new float[TrailN];
    int _trailPos, _trailCount;
    float _trailLastD = float.NaN, _offLastD = float.NaN;
    double _offX, _offY;
    bool _offInit;

    DateTime _lastPublish = DateTime.MinValue, _lastSlow = DateTime.MinValue;
    List<CarRow> _standings = new(), _relative = new();
    Dictionary<string, int> _sof = new();
    List<RadarCar> _radar = new();
    List<MapCar> _mapCars = new();
    int _carsInClass;
    WeatherLive _weatherLive = new();

    public bool IsReplay => !_source.IsLive;
    public long CurrentSessionDbId => _tracker.Current?.DbId ?? 0;

    public TelemetryHub(SettingsStore settings, SessionStore store, AnalysisService analysis)
    {
        Settings = settings;
        Store = store;
        Analysis = analysis;
        Engineer = new RaceEngineer(() => Settings.Current);
        Setups = new SetupInstaller(() => Settings.Current);
        TyreManager = new TyreManager(Engineer, () => Settings.Current);
        Coach = new DrivingCoach(Engineer, () => Settings.Current);
        CarManager = new CarManager(Engineer, () => Settings.Current);
        SetupEngineer = new SetupEngineer(Engineer, () => Settings.Current);
        Insights = new Insights(Engineer, () => Settings.Current);
        InCarAdvisor = new InCarAdvisor(Engineer, () => Settings.Current);
        Pit = new PitAutoService(Engineer, () => Settings.Current) { Commands = new IRacingPitCommands() };
        CrewChief = new CrewChiefBridge(() => Settings.Current);
        CrewChief.Log += m => Engineer.Info(m);
        Radio = new RadioGate(() => Settings.Current);
        Engineer.Said += m => { if (m.Speak) Radio.Enqueue(m); };
        _source = _live;
        Analysis.MyDriverIdProvider = () => Settings.Current.MyUserId;

        _tracker.SessionStarted += OnSessionStarted;
        _tracker.SessionEnded += OnSessionEnded;
        _tracker.StintStarted += OnStintStarted;
        _tracker.LapCompleted += OnLapCompleted;
        _tracker.PitStopCompleted += OnPitStop;
        Store.TrackModelUpdated += m => { if (_info != null && m.TrackKey == _info.TrackKey) _model = m; };
    }

    public void Start()
    {
        _ = CrewChief.StartAsync();
        _thread = new Thread(Loop) { IsBackground = true, Name = "telemetry", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        _worker = new Thread(() =>
        {
            foreach (var a in _work.GetConsumingEnumerable())
            {
                try { a(); } catch (Exception e) { Engineer.Info("Background error: " + e.Message); }
            }
        }) { IsBackground = true, Name = "hub-worker" };
        _worker.Start();
    }

    void Enqueue(Action a) => _work.Add(a);

    // ------------------------------------------------------------------ public controls

    public void StartReplay(string ibtPath, double speed = 1)
    {
        _pendingSource = new IbtSource(ibtPath, Math.Max(0, speed)); // 0 = as fast as possible
    }

    public void StopReplay() => _pendingSource = _live;

    public void SetReference(string mode, long lapId = 0)
    {
        Settings.Update(s => { s.ReferenceMode = mode; s.ReferenceLapId = lapId; });
        LoadReferenceAsync();
    }

    public bool ApplyTyrePressures(float[] kpa)
    {
        if (IsReplay) return false;
        bool ok = true;
        for (int i = 0; i < 4 && i < kpa.Length; i++) if (kpa[i] > 0) ok &= IRacingBroadcast.SetTyrePressure(i, kpa[i]);
        if (ok) Engineer.Info($"Pit tyre pressures set: {string.Join(" / ", kpa.Select(k => k.ToString("0")))} kPa", speak: false);
        return ok;
    }

    public bool ApplyFuel(double litres)
    {
        if (IsReplay) return false;
        bool ok = IRacingBroadcast.SetFuel(litres);
        if (ok) Engineer.Info($"Pit fuel set to add {litres:0.0} L", speak: false);
        return ok;
    }

    // ------------------------------------------------------------------ questions from the driver (wheel buttons / hotkeys)

    public static readonly (string id, string label)[] Questions =
    {
        ("tyres", "How are my tyres?"),
        ("fuel", "Fuel: how much, how many laps, do I need to save?"),
        ("gaps", "Gaps: car ahead / behind and who's catching who"),
        ("pace", "My pace: last lap and where I lost time"),
        ("potential", "Where's the time? (my best corners combined)"),
        ("setup", "Setup session: where are we?"),
        ("setup-toggle", "Start / stop a setup session"),
        ("car", "What should I change in the car? (TC, ABS, brake bias…)"),
        ("pit", "What will you set at my pit stop?"),
        ("repeat", "Repeat the last message"),
        ("quiet", "Quiet mode on / off (only important calls)"),
    };

    /// <summary>Answers a driver question out loud straight away (no waiting for a straight) and returns the text.</summary>
    public string Ask(string id)
    {
        string answer;
        try { answer = Answer(id); }
        catch (Exception e) { answer = "Sorry, I couldn't work that out. " + e.Message; }
        if (id != "repeat" && answer.Length > 0) Engineer.Say(answer, "answer", 2, immediate: true);
        return answer;
    }

    string Answer(string id)
    {
        var f = _source.Frame;
        bool driving = _info != null;
        switch (id)
        {
            case "tyres":
                return TyreManager.Describe();
            case "fuel":
                return Insights.DescribeFuel(driving ? ComputeFuel(f) : null);
            case "gaps":
                return driving ? Insights.DescribeGaps(_standings, f.PlayerCarIdx, ComputeFuel(f)?.LapsRemaining ?? double.NaN) : "No session running.";
            case "pace":
            {
                if (!double.IsFinite(_lastLap)) return "No lap times yet.";
                var parts = new List<string> { $"Last lap {RaceEngineer.Speak(_lastLap)}" + (double.IsFinite(_lastLapDelta) ? $", {(_lastLapDelta >= 0 ? "plus" : "minus")} {Math.Abs(_lastLapDelta):0.00} to the reference." : ".") };
                var top = _lastCmp?.TopLosses.FirstOrDefault();
                if (top != null) parts.Add($"Most lost at {(top.Name.StartsWith('T') ? "turn " + top.Name[1..] : top.Name)}, {top.TimeDelta:0.00}{(string.IsNullOrEmpty(top.Verdict) ? "" : ", " + top.Verdict)}.");
                if (_recentLaps.Count >= 3)
                {
                    double avg = _recentLaps.Average(), spread = _recentLaps.Max() - _recentLaps.Min();
                    parts.Add($"Last {_recentLaps.Count} clean laps average {RaceEngineer.Speak(avg)}, spread {spread:0.0}.");
                }
                return string.Join(" ", parts);
            }
            case "potential":
                return Insights.DescribePotential();
            case "setup":
                return SetupEngineer.Describe();
            case "setup-toggle":
                if (SetupEngineer.State is SetupEngineer.Phase.Off or SetupEngineer.Phase.Done) SetupEngineer.Start();
                else SetupEngineer.Stop();
                return "";
            case "car":
                return InCarAdvisor.Describe();
            case "pit":
                return driving ? Pit.Describe(f, ComputeFuel(f)) : "No session running.";
            case "repeat":
            {
                var last = Radio.Last;
                if (last == null) { Engineer.Say("Nothing to repeat yet.", "answer", 2, immediate: true); return ""; }
                Engineer.Say(last.Text, "answer", 2, immediate: true);
                return last.Text;
            }
            case "quiet":
                Radio.Quiet = !Radio.Quiet;
                return Radio.Quiet ? "Quiet mode on. Only important calls from now." : "Quiet mode off. Full radio.";
            default:
                return "";
        }
    }

    /// <summary>Cold pressures your last run here suggests (Tyres page logic), for the automatic pit service.</summary>
    void LoadPitPressures(SessionInfo si, long currentId)
    {
        Enqueue(() =>
        {
            try
            {
                foreach (var s in Store.Db.GetSessions(si.CarPath, si.TrackKey).Where(x => x.Id != currentId).OrderByDescending(x => x.StartedAt).Take(5))
                {
                    var laps = Store.Db.GetLaps(s.Id).Where(l => l.Tyres != null && !l.OutLap).ToList();
                    if (laps.Count < 3) continue;
                    var adv = TyreAnalyzer.Analyze(laps.Select(l => l.Tyres!).ToList(), Settings.Current.TyreTargetFor(si.CarPath, si.CarCategory));
                    if (adv.Count != 4) continue;
                    var kpa = adv.Select(a => float.IsFinite(a.SuggestedCold) ? a.SuggestedCold : a.PressCold).ToArray();
                    if (kpa.Any(k => !float.IsFinite(k) || k < 50)) continue;
                    Pit.SetHistoryPressures(kpa, $"your run here on {s.StartedAt:d MMM}");
                    return;
                }
                Pit.SetHistoryPressures(null, "");
            }
            catch { Pit.SetHistoryPressures(null, ""); }
        });
    }

    // ------------------------------------------------------------------ main loop

    void Loop()
    {
        while (!_stop)
        {
            var pending = _pendingSource;
            if (pending != null)
            {
                _pendingSource = null;
                if (_source != _live) _source.Dispose();
                _tracker.Flush();
                _source = pending;
                _tracker.SourceName = pending.IsLive ? "live" : "ibt";
                _infoVersion = -1;
                ResetSessionState();
            }

            SourceStatus st;
            try { st = _source.Next(50); }
            catch (Exception e) { Engineer.Info("Telemetry error: " + e.Message); Thread.Sleep(500); continue; }

            if (st == SourceStatus.Ended)
            {
                _tracker.Flush();
                Engineer.Info("Replay finished.");
                _pendingSource = _live;
                continue;
            }
            if (st is SourceStatus.Disconnected or SourceStatus.Ended) Radio.Flush(_source.Frame.SessionTime);
            if (st == SourceStatus.Disconnected)
            {
                if (_tracker.Current != null) _tracker.Flush();
                if (_info != null) { _info = null; _infoVersion = -1; }
                if ((DateTime.Now - _lastPublish).TotalMilliseconds > 500) PublishIdle();
                continue;
            }
            if (st != SourceStatus.Frame) continue;

            var f = _source.Frame;
            if (_source.SessionVersion != _infoVersion && _source.Session != null)
            {
                _infoVersion = _source.SessionVersion;
                OnSessionInfo(_source.Session);
            }
            if (_info == null) continue;

            try
            {
                double dt = double.IsFinite(_prevFrameTime) ? f.SessionTime - _prevFrameTime : 0;
                _prevFrameTime = f.SessionTime;
                _tracker.Process(f, _source.WallClock);
                TyreManager.Update(f, _info.TrackLengthM, dt, f.SessionTime);
                CarManager.Update(f);
                Insights.SampleFuel(f);
                Pit.Update(f, f.OnPitRoad ? ComputeFuel(f) : null, _source.IsLive);
                UpdateLiveLap(f);
                if (!_tracker.CurrentLapIsOut) Insights.Update(f, f.LapDistPct * _info.TrackLengthM, _model, _ref, Coach);
                var (inZone, toZone) = RadioGate.Zone(f.LapDistPct * _info.TrackLengthM, _info.TrackLengthM, _model, _ref);
                Radio.Tick(f, f.SessionTime, toZone, inZone);
                UpdateTrail(f);
                Watch(f);
                if ((++_frameCount & 1) == 0) PushHistory(f);
                if ((DateTime.Now - _lastPublish).TotalMilliseconds >= 45) Publish(f);
            }
            catch (Exception e)
            {
                Engineer.Say("Live processing error: " + e.Message, "warning", 0, "live-error", 10, speak: false);
            }
        }
    }

    void OnSessionInfo(SessionInfo si)
    {
        bool carTrackChanged = _info == null || _info.CarPath != si.CarPath || _info.TrackKey != si.TrackKey;
        _info = si;
        _tracker.OnSessionInfo(si);
        CarManager.OnSessionInfo(si);
        SetupEngineer.OnSessionInfo(si);
        _sectorStarts = SectorStarts(si);
        if (carTrackChanged)
        {
            _carTrack = si.CarPath + "|" + si.TrackKey;
            _model = Store.GetTrackModel(si.TrackKey);
            LoadReferenceAsync();
        }
    }

    float[] SectorStarts(SessionInfo si)
    {
        var mode = Settings.Current.DeltaSectors;
        if (int.TryParse(mode, out int n) && n >= 2 && n <= 20) return Enumerable.Range(0, n).Select(i => (float)i / n).ToArray();
        return si.SectorStarts.Length >= 2 ? si.SectorStarts : new[] { 0f, 1f / 3, 2f / 3 };
    }

    void ResetSessionState()
    {
        _sessionBest = double.NaN;
        _lastLap = double.NaN;
        _lastLapDelta = double.NaN;
        _lastValidLap = null;
        _fuelPerLap.Clear();
        _bestSectors = Array.Empty<double>();
        _weather.Clear();
        _sessionStartTrackTemp = float.NaN;
        _rainAnnounced = false;
        _greeted = false;
        _prevIncidents = -1;
        _possibleDamage = false;
        _postIncidentLaps.Clear();
        _incidentLapWatch = -1;
        _lapCorners.Clear();
        _lastCorner = null;
        _diskLoggingRequested = false;
        _lastCmp = null;
        _recentLaps.Clear();
    }

    // ------------------------------------------------------------------ reference handling

    void LoadReferenceAsync()
    {
        var si = _info;
        if (si == null) return;
        var s = Settings.Current;
        string mode = s.ReferenceMode;
        long lapId = s.ReferenceLapId;
        var f = _source.Frame;
        _refWet = AnalysisService.Conditions.IsWet(f.TrackWetness);
        var cond = new AnalysisService.Conditions(f.PlayerTireCompound, _refWet);
        Enqueue(() =>
        {
            var model = Store.GetTrackModel(si.TrackKey);
            if (model != null) _model = model;
            var pbRow = Analysis.PersonalBestRow(si.CarPath, si.TrackKey, null, cond);
            _pb = pbRow?.LapTime ?? double.NaN;
            var fuelHist = Store.Db.GetBestLaps(si.CarPath, si.TrackKey, 200, Analysis.MyDriverId, false)
                .OrderByDescending(l => l.StartedAt).Where(l => l.FuelUsed is > 0.05f).Take(30).Select(l => (double)l.FuelUsed!.Value).ToList();
            _historyFuelPerLap = fuelHist.Count >= 3 ? fuelHist.Average() : double.NaN;
            ReferenceLap? r = null;
            if (mode == "lap" && lapId > 0) r = Analysis.LoadReference(lapId);
            else if (mode == "pb" && pbRow != null) r = Analysis.LoadReference(pbRow.Id, $"PB {Fmt.LapTime(pbRow.LapTime)}");
            // session mode (or no PB yet) → filled in as soon as a clean lap is completed
            if (r == null && _lastValidLap != null && _model != null) r = FromRecorded(_lastValidLap, "session best");
            _ref = r;
        });
    }

    ReferenceLap? FromRecorded(RecordedLap lap, string label)
    {
        var m = _model;
        if (m == null || lap.Data == null) return null;
        var d = DistLap.TryCreate(lap.Data, m.Length, lap.LapTime, true);
        if (d == null) return null;
        return new ReferenceLap { Label = $"{label} {Fmt.LapTime(lap.LapTime)}", Dist = d, Analysis = LapAnalyzer.Analyze(d, m), LapId = lap.Id, HasGps = lap.HasGps };
    }

    // ------------------------------------------------------------------ tracker events (telemetry thread)

    void OnSessionStarted(SessionContext ctx)
    {
        ResetSessionState();
        TyreManager.OnSession(ctx.Info, ctx.SessionType);
        Coach.Reset(ctx.SessionType);
        CarManager.Reset(ctx.Info, ctx.SessionType);
        SetupEngineer.Reset(ctx.Info, ctx.SessionType);
        Insights.Reset(ctx.Info, ctx.SessionType);
        InCarAdvisor.Reset();
        Pit.Reset(ctx.SessionType);
        LoadPitPressures(ctx.Info, ctx.DbId);
        SetupEngineer.OnModel(_model);
        var row = SessionStore.ToRow(ctx, _source.IsLive ? "live" : "ibt");
        Enqueue(() => Store.EnsureSession(ctx, row.Source, _source is IbtSource ibt ? ibt.File.Path : ""));
    }

    void OnSessionEnded(SessionContext ctx)
    {
        if (ctx.Laps.Count == 0) return;
        long id = ctx.DbId;
        Enqueue(() =>
        {
            if (id <= 0) return;
            var rep = Analysis.SessionReport(id);
            if (rep == null || rep.ValidLaps == 0) return;
            var focus = rep.Insights.Where(i => i.Severity >= 2).Select(i => i.Title).FirstOrDefault();
            Engineer.Say($"Session complete. {rep.ValidLaps} clean laps, best {RaceEngineer.Speak(rep.BestLap)}." + (focus != null ? $" Focus next time: {focus}." : ""), "info", 1, "session-end", 60, speak: true);
        });
    }

    void OnStintStarted(SessionContext ctx)
    {
        var si = ctx.Info;
        var f = _source.Frame;
        var stop = _lastStop;
        _lastStop = null;
        TyreManager.OnStintStart(stop?.StationaryTime ?? double.NaN, stop == null || stop.TyresChanged);
        SetupEngineer.OnStint();
        if (_source.IsLive && Settings.Current.AutoStartDiskTelemetry && f.HasDiskLoggingVar && !f.DiskLoggingEnabled && !_diskLoggingRequested)
        {
            _diskLoggingRequested = true;
            if (IRacingBroadcast.StartDiskTelemetry()) Engineer.Info("Turned on iRacing telemetry logging (adds racing lines and tyre data after the session).");
        }
        if (_greeted) return;
        _greeted = true;
        string sessionType = ctx.SessionType;
        float track = f.TrackTemp, air = f.AirTemp;
        string wet = f.TrackWetness >= 3 ? Irsdk.WetnessName(f.TrackWetness).ToLowerInvariant() : "dry";
        string conditions = float.IsFinite(track) ? $"Track {track:0}°, air {air:0}°, {wet}." : "";
        Enqueue(() =>
        {
            // wait (briefly) for the reference/PB lookup queued before us — it's on this same worker, so it already ran
            string? setupMsg = SetupChangeMessage(si);
            Engineer.SessionStart(si, sessionType, _pb, _ref?.Label ?? "", setupMsg, conditions);
            if (Settings.Current.AutoInstallSetups && _source.IsLive)
            {
                var res = Setups.Install(si.CarPath, si.TrackName, si.TrackDisplayName);
                if (res.Installed.Count > 0) Engineer.Info($"Installed {res.Installed.Count} setup(s) into iRacing › setups › {si.CarPath} › {SetupInstaller.TargetSubfolder}.");
            }
        });
    }

    string? SetupChangeMessage(SessionInfo si)
    {
        try
        {
            var hash = Store.SaveSetup(si);
            var prev = Store.Db.GetBestLaps(si.CarPath, si.TrackKey, 200, null, false)
                .Where(l => !string.IsNullOrEmpty(l.SetupHash)).OrderByDescending(l => l.StartedAt).FirstOrDefault();
            if (prev == null || prev.SetupHash == hash) return null;
            var old = Store.Db.GetSetup(prev.SetupHash);
            if (old == null) return null;
            var diff = SetupOptimiser.Diff(YNode.Parse(old.Value.yaml)["CarSetup"], si.CarSetup);
            if (diff.Count == 0) return null;
            var items = diff.Take(3).Select(d => $"{string.Join(" ", d.key.Split('.').TakeLast(2).Distinct())} {d.a} → {d.b}");
            return $"Setup changed since your last run ({diff.Count} values): {string.Join(", ", items)}{(diff.Count > 3 ? "…" : "")}.";
        }
        catch { return null; }
    }

    void OnLapCompleted(SessionContext ctx, RecordedLap lap)
    {
        var si = ctx.Info;
        var model = _model;
        var reference = _ref;
        string source = _source.IsLive ? "live" : "ibt";
        string file = _source is IbtSource ibt ? ibt.File.Path : "";

        // persist + model building on the worker
        Enqueue(() =>
        {
            Store.SaveLap(ctx, lap, source, file);
            if (lap.Valid)
            {
                var before = _model;
                var m = Store.OfferLapForModel(si.TrackKey, si, lap);
                if (m != null && m != before)
                {
                    _model = m;
                    Engineer.Info(before == null ? $"Track map built: {m.Corners.Count} corners detected." : "Track map refined.", speak: false);
                    var r = _ref;
                    // corner metrics are tied to the model's corner list → recompute for the new layout
                    if (r != null) _ref = new ReferenceLap { Label = r.Label, Dist = r.Dist, Analysis = LapAnalyzer.Analyze(r.Dist, m), LapId = r.LapId, HasGps = r.HasGps };
                    else if (_lastValidLap != null) _ref = FromRecorded(_lastValidLap, "session best");
                }
            }
        });

        _lastLap = lap.LapTime;
        if (!lap.OutLap && !lap.InLap && lap.FuelUsed is > 0.05f and < 50) _fuelPerLap.Add(lap.FuelUsed);

        DistLap? dl = null; LapAnalysis? la = null; LapComparison? cmp = null;
        if (model != null && lap.Data != null && !lap.OutLap)
        {
            dl = DistLap.TryCreate(lap.Data, model.Length, lap.LapTime, true);
            if (dl != null)
            {
                la = LapAnalyzer.Analyze(dl, model, si.ShiftRpm);
                if (reference != null && lap.Valid) cmp = LapComparer.Compare(dl, la, reference.Dist, reference.Analysis);
                // close the corners that end at the line
                if (cmp != null)
                    foreach (var cc in cmp.Corners.Where(c => c.Corner > _nextCorner))
                    {
                        var cl = ToLive(cc, la.Corners.FirstOrDefault(x => x.Corner == cc.Corner), lap.LapNumber);
                        _lapCorners.Add(cl);
                        _lastCorner = cl;
                        Coach.OnCorner(cc, lap.LapNumber);
                    }
                UpdateBestSectors(dl, lap.Valid);
            }
        }

        _lastLapDelta = reference != null && lap.Valid ? lap.LapTime - reference.Dist.LapTime : double.NaN;
        bool sessionBest = false, pb = false, hadPb = double.IsFinite(_pb);
        if (lap.Valid)
        {
            if (!(lap.LapTime >= _sessionBest)) { sessionBest = true; _sessionBest = lap.LapTime; }
            if (!(lap.LapTime >= _pb)) { pb = true; _pb = lap.LapTime; }
            _lastValidLap = lap;
            string mode = Settings.Current.ReferenceMode;
            bool replace = mode switch
            {
                "session" => sessionBest,
                "pb" => pb || reference == null,
                "last" => true,
                _ => reference == null,
            };
            if (replace && dl != null && la != null)
                _ref = new ReferenceLap { Label = $"{(pb && mode == "pb" ? "PB" : mode == "last" ? "last lap" : "session best")} {Fmt.LapTime(lap.LapTime)}", Dist = dl, Analysis = la, LapId = lap.Id, HasGps = lap.HasGps };
        }

        bool isRace = si.Session(ctx.SessionNum)?.IsRace ?? false;
        Engineer.LapDone(lap.LapNumber, lap.LapTime, lap.Valid, lap.InvalidReason, _lastLapDelta, reference?.Label ?? "", sessionBest && !(pb && hadPb), pb && hadPb, cmp, _source.Frame.PlayerCarPosition, isRace);

        var fuel = ComputeFuel(_source.Frame);
        if (fuel != null) Engineer.Fuel(fuel, isRace, ctx.Laps.Count);

        var fr = _source.Frame;
        TyreManager.OnLap(lap, _sessionBest);
        CarManager.OnLap(lap, fr.SessionTimeRemain, fr.SessionLapsRemainEx);
        SetupEngineer.OnModel(_model);
        SetupEngineer.OnLap(lap, fr);
        Insights.OnLap(lap, la);
        InCarAdvisor.OnModel(_model);
        InCarAdvisor.OnLap(lap, fr, SetupEngineer.State is not (SetupEngineer.Phase.Off or SetupEngineer.Phase.Done), TyreManager.Snapshot());
        Insights.OnLapGaps(lap.LapNumber, _standings, fr.PlayerCarIdx, fuel?.LapsRemaining ?? double.NaN);
        Insights.OnLapFuel(lap.LapNumber, fuel, reference, model);
        _lastCmp = cmp;
        if (lap.Valid && !lap.OutLap && !lap.InLap) { _recentLaps.Add(lap.LapTime); if (_recentLaps.Count > 5) _recentLaps.RemoveAt(0); }

        // damage heuristic: pace after an incident
        if (_incidentLapWatch >= 0 && lap.LapNumber > _incidentLapWatch && lap.Valid)
        {
            _postIncidentLaps.Add(lap.LapTime);
            if (_postIncidentLaps.Count >= 2 && double.IsFinite(_paceBeforeIncident))
            {
                double avg = _postIncidentLaps.Average();
                _paceLoss = (float)((avg / _paceBeforeIncident - 1) * 100);
                _possibleDamage = _paceLoss > 1.5f;
                if (_possibleDamage) Engineer.PossibleDamage(_paceLoss);
                _incidentLapWatch = -1;
                _postIncidentLaps.Clear();
            }
        }
    }

    void OnPitStop(SessionContext ctx, PitStopInfo p)
    {
        _lastStop = p;
        Enqueue(() => Store.Db.InsertPitStop(Store.EnsureSession(ctx, _source.IsLive ? "live" : "ibt"), p));
        if (p.FuelAdded > 0.5f || p.TyresChanged) Engineer.PitStop(new PitStopSummary(p.StationaryTime, p.FuelAdded, p.TyresChanged));
    }

    void UpdateBestSectors(DistLap dl, bool valid)
    {
        if (!valid) return;
        int n = _sectorStarts.Length;
        if (_bestSectors.Length != n) { _bestSectors = new double[n]; Array.Fill(_bestSectors, double.NaN); }
        for (int s = 0; s < n; s++)
        {
            float a = _sectorStarts[s] * dl.Length, b = (s + 1 < n ? _sectorStarts[s + 1] : 1f) * dl.Length;
            double t = dl.TimeBetween(a, Math.Min(b, dl.Length));
            if (double.IsFinite(t) && !(t >= _bestSectors[s])) _bestSectors[s] = t;
        }
    }

    // ------------------------------------------------------------------ per-frame live lap

    void UpdateLiveLap(Frame f)
    {
        var si = _info!;
        if (!_tracker.LapInProgress || !f.IsOnTrack) return;
        float L = si.TrackLengthM;
        double start = _tracker.CurrentLapStartTime;
        if (start != _curLapStart)
        {
            _cur = new DistLap(L);
            _curLapStart = start;
            _lapFuelStart = f.FuelLevel;
            _lapStartPct = _tracker.CurrentLapIsOut ? f.LapDistPct : 0;
            _nextCorner = 0;
            _lapCorners.Clear();
            _lastIdx = -1;
            if (!_tracker.CurrentLapIsOut)
            {
                // anchor the start line at t = 0
                _cur.Time[0] = 0; _cur.Speed[0] = f.Speed; _cur.Throttle[0] = f.Throttle; _cur.Brake[0] = f.Brake;
                _cur.Steer[0] = f.Steer; _cur.Gear[0] = f.Gear; _cur.Rpm[0] = f.RPM; _cur.LatG[0] = f.LatAccel; _cur.YawRate[0] = f.YawRate;
                _lastIdx = 0;
                _prevD = 0; _prevT = 0; Remember(f);
            }
        }
        var cur = _cur!;
        float d = f.LapDistPct * L;
        float t = (float)(f.SessionTime - start);
        int idx = Math.Min(cur.N - 1, (int)(d / cur.Step));
        if (_lastIdx < 0)
        {
            cur.Time[idx] = t; cur.Speed[idx] = f.Speed; cur.Throttle[idx] = f.Throttle; cur.Brake[idx] = f.Brake;
            cur.Steer[idx] = f.Steer; cur.Gear[idx] = f.Gear; cur.Rpm[idx] = f.RPM; cur.LatG[idx] = f.LatAccel; cur.YawRate[idx] = f.YawRate;
            _lastIdx = idx; _prevD = d; _prevT = t; Remember(f);
        }
        else if (idx > _lastIdx && idx - _lastIdx < 300)
        {
            float span = d - _prevD;
            for (int i = _lastIdx + 1; i <= idx; i++)
            {
                float a = span > 0 ? Math.Clamp((i * cur.Step - _prevD) / span, 0, 1) : 1;
                cur.Time[i] = _prevT + a * (t - _prevT);
                cur.Speed[i] = _prevSpeed + a * (f.Speed - _prevSpeed);
                cur.Throttle[i] = _prevThr + a * (f.Throttle - _prevThr);
                cur.Brake[i] = _prevBrk + a * (f.Brake - _prevBrk);
                cur.Steer[i] = _prevSteer + a * (f.Steer - _prevSteer);
                cur.Rpm[i] = _prevRpm + a * (f.RPM - _prevRpm);
                cur.LatG[i] = _prevLat + a * (f.LatAccel - _prevLat);
                cur.YawRate[i] = _prevYaw + a * (f.YawRate - _prevYaw);
                cur.Gear[i] = f.Gear == 0 && f.Speed > 5 && i > 0 ? cur.Gear[i - 1] : f.Gear;
                cur.Abs[i] = f.AbsActive ? 1 : 0;
            }
            _lastIdx = idx; _prevD = d; _prevT = t; Remember(f);
        }
        else if (idx >= _lastIdx) { _prevD = d; _prevT = t; Remember(f); }

        // corner completion → live corner feedback
        var model = _model;
        var reference = _ref;
        if (model == null || _tracker.CurrentLapIsOut) return;
        // wait until the segment end is actually in the buffer (+2 m) before judging the corner
        while (_nextCorner < model.Corners.Count && d >= model.Corners[_nextCorner].SegEnd + 2 && model.Corners[_nextCorner].SegEnd < L - 3)
        {
            var c = model.Corners[_nextCorner];
            _nextCorner++;
            if (reference == null) continue;
            var m = LapAnalyzer.AnalyzeCorner(cur, c, new List<LapEvent>());
            var r = reference.Analysis.Corners.FirstOrDefault(x => x.Corner == c.Index);
            if (r == null || !float.IsFinite(m.SegTime)) continue;
            var cc = LapComparer.CompareCorner(m, r);
            var cl = ToLive(cc, m, f.Lap);
            _lapCorners.Add(cl);
            _lastCorner = cl;
            Engineer.CornerDone(cl);
            Coach.OnCorner(cc, f.Lap);
        }
        if (reference != null) Coach.Update(f, d, model, reference);
    }

    void UpdateTrail(Frame f)
    {
        var model = _model;
        if (model == null || !_tracker.PosValid || !f.IsOnTrack) { _trailCount = 0; _offInit = false; _trailLastD = float.NaN; _offLastD = float.NaN; return; }
        float d = f.LapDistPct * model.Length;
        double px = _tracker.PosX, py = _tracker.PosY;
        if (!(f.HasGps && model.FromGps))
        {
            // pull the dead-reckoned position onto the map with a slowly adapting offset (~300 m time constant)
            var (mx, my) = model.PosAt(d);
            double tx = mx - px, ty = my - py;
            if (!_offInit) { _offX = tx; _offY = ty; _offInit = true; }
            else
            {
                float moved = float.IsNaN(_offLastD) ? 0 : Math.Abs(d - _offLastD);
                if (moved > model.Length / 2) moved = 0;
                double a = Math.Clamp(moved / 300.0, 0, 1);
                _offX += (tx - _offX) * a; _offY += (ty - _offY) * a;
            }
            _offLastD = d;
            px += _offX; py += _offY;
        }
        if (!float.IsNaN(_trailLastD) && Math.Abs(d - _trailLastD) < 2 && Math.Abs(d - _trailLastD) < model.Length / 2) return;
        _trailLastD = d;
        _trailX[_trailPos] = (float)px; _trailY[_trailPos] = (float)py;
        _trailPos = (_trailPos + 1) % TrailN;
        _trailCount = Math.Min(TrailN, _trailCount + 1);
    }

    void EnsureAligned(ReferenceLap r, TrackModel m)
    {
        if (r.AlignedX != null) return;
        var (x, y) = LineAlign.ToModel(r.Dist, m, r.HasGps);
        r.AlignedY = y;
        r.AlignedX = x;
    }

    void Remember(Frame f)
    {
        _prevSpeed = f.Speed; _prevThr = f.Throttle; _prevBrk = f.Brake; _prevSteer = f.Steer; _prevRpm = f.RPM; _prevLat = f.LatAccel; _prevYaw = f.YawRate;
    }

    static CornerLive ToLive(CornerComparison cc, CornerMetrics? m, int lap) => new()
    {
        Name = cc.Name, TimeDelta = cc.TimeDelta, MinSpeedDiff = cc.MinSpeedDiff, BrakeDiff = cc.BrakeDiff, ExitSpeedDiff = cc.ExitSpeedDiff,
        Verdict = cc.Verdict, Advice = cc.Advice.FirstOrDefault() ?? "", MinSpeed = m != null ? m.MinSpeed * 3.6f : float.NaN, Lap = lap,
    };

    // ------------------------------------------------------------------ watchers (flags, incidents, warnings, weather)

    void Watch(Frame f)
    {
        if (!f.IsOnTrack) { _prevFlags = f.SessionFlags; return; }
        if (f.SessionFlags != _prevFlags)
        {
            Engineer.Flags(f.SessionFlags, _prevFlags, f.PlayerCarPosition);
            _prevFlags = f.SessionFlags;
        }
        int inc = f.PlayerCarMyIncidentCount;
        if (_prevIncidents >= 0 && inc > _prevIncidents)
        {
            int added = inc - _prevIncidents;
            Engineer.Incident(added, inc, _info?.IncidentLimit ?? 0);
            if (added >= 2 && double.IsFinite(_sessionBest))
            {
                _incidentLapWatch = f.Lap;
                _paceBeforeIncident = _sessionBest;
                _postIncidentLaps.Clear();
            }
        }
        _prevIncidents = inc;

        // engine warnings only matter while driving (a parked car reports low oil pressure / stalled)
        uint warn = f.Speed > 10 && !f.OnPitRoad ? f.EngineWarnings & ~(uint)(Irsdk.EngineWarnings.PitSpeedLimiter | Irsdk.EngineWarnings.RevLimiterActive | Irsdk.EngineWarnings.EngineStalled) : 0;
        uint newWarn = warn & ~_prevWarnings;
        if (newWarn != 0) foreach (var w in WarningNames(newWarn)) Engineer.EngineWarning(w);
        _prevWarnings = warn;

        float repair = float.IsFinite(f.PitRepairLeft) ? f.PitRepairLeft : 0;
        if (repair > 1 && _prevRepair <= 1) Engineer.Damage(repair, f.PitOptRepairLeft);
        else if (float.IsFinite(f.PitOptRepairLeft) && f.PitOptRepairLeft > 5 && repair <= 1 && _prevRepair <= 0) Engineer.Damage(0, f.PitOptRepairLeft);
        _prevRepair = Math.Max(repair, float.IsFinite(f.PitOptRepairLeft) && f.PitOptRepairLeft > 5 ? 0.5f : 0);

        _weather.Add(f.SessionTime, f.TrackTemp, f.AirTemp, f.TrackWetness, f.Precipitation);
        if (float.IsNaN(_sessionStartTrackTemp) && float.IsFinite(f.TrackTemp)) _sessionStartTrackTemp = f.TrackTemp;
        if (float.IsFinite(f.TrackTemp) && float.IsFinite(_sessionStartTrackTemp) && Math.Abs(f.TrackTemp - _sessionStartTrackTemp) >= 5)
        {
            Engineer.Weather($"Track temperature {(f.TrackTemp > _sessionStartTrackTemp ? "up" : "down")} to {f.TrackTemp:0} degrees — expect {(f.TrackTemp > _sessionStartTrackTemp ? "less" : "more")} grip and changing tyre pressures.");
            _sessionStartTrackTemp = f.TrackTemp;
        }
        if (!_rainAnnounced && f.Precipitation > 0.05f) { _rainAnnounced = true; Engineer.Weather("Rain is falling."); }
        if (f.TrackWetness > 0 && AnalysisService.Conditions.IsWet(f.TrackWetness) != _refWet)
        {
            Engineer.Weather(_refWet ? "Track is drying. Switching to a dry reference." : "Track is wet now. Switching to a wet reference.");
            LoadReferenceAsync();
        }
    }

    static IEnumerable<string> WarningNames(uint w)
    {
        if ((w & (uint)Irsdk.EngineWarnings.WaterTemp) != 0) yield return "water temperature high";
        if ((w & (uint)Irsdk.EngineWarnings.OilTemp) != 0) yield return "oil temperature high";
        if ((w & (uint)Irsdk.EngineWarnings.OilPressure) != 0) yield return "oil pressure low";
        if ((w & (uint)Irsdk.EngineWarnings.FuelPressure) != 0) yield return "fuel pressure low";
        if ((w & (uint)Irsdk.EngineWarnings.EngineStalled) != 0) yield return "engine stalled";
    }

    void PushHistory(Frame f)
    {
        float steerNorm = float.IsFinite(f.SteerMax) && f.SteerMax > 0 ? f.Steer / f.SteerMax : f.Steer / 4f;
        _hThr[_hPos] = f.Throttle; _hBrk[_hPos] = f.Brake; _hSteer[_hPos] = steerNorm; _hSpd[_hPos] = f.Speed;
        var r = _ref;
        float d = f.LapDistPct * (_info?.TrackLengthM ?? 0);
        if (r != null && !_tracker.CurrentLapIsOut)
        {
            _hRThr[_hPos] = r.Dist.At(r.Dist.Throttle, d); _hRBrk[_hPos] = r.Dist.At(r.Dist.Brake, d); _hRSpd[_hPos] = r.Dist.At(r.Dist.Speed, d);
        }
        else { _hRThr[_hPos] = float.NaN; _hRBrk[_hPos] = float.NaN; _hRSpd[_hPos] = float.NaN; }
        _hPos = (_hPos + 1) % Hist;
    }

    float[] Ring(float[] src)
    {
        var res = new float[Hist];
        for (int i = 0; i < Hist; i++) res[i] = src[(_hPos + i) % Hist];
        return res;
    }

    // ------------------------------------------------------------------ fuel

    LiveFuel? ComputeFuel(Frame f)
    {
        var si = _info;
        if (si == null || float.IsNaN(f.FuelLevel)) return null;
        double avgLap = double.IsFinite(_sessionBest) ? _sessionBest * 1.02 : si.EstLapTime > 0 ? si.EstLapTime : double.NaN;
        double laps = double.NaN;
        if (f.SessionLapsRemainEx > 0 && f.SessionLapsRemainEx < 30000) laps = f.SessionLapsRemainEx;
        else if (double.IsFinite(f.SessionTimeRemain) && f.SessionTimeRemain > 0 && f.SessionTimeRemain < 86400 && double.IsFinite(avgLap) && avgLap > 0)
            laps = Math.Ceiling(f.LapDistPct + f.SessionTimeRemain / avgLap);
        bool estimated = _fuelPerLap.Count == 0;
        var hist = !estimated ? _fuelPerLap : EstimateFuelPerLap(f);
        var res = FuelCalculator.Live(f.FuelLevel, hist, laps, si.TankCapacityL, Settings.Current.FuelMarginLaps, f.LapDistPct);
        res.Estimated = estimated;
        return res;
    }

    /// <summary>Before the first full lap: your history for this car/track, else what this lap has used so far.</summary>
    List<double> EstimateFuelPerLap(Frame f)
    {
        if (double.IsFinite(_historyFuelPerLap)) return new List<double> { _historyFuelPerLap };
        if (float.IsNaN(_lapFuelStart)) return new();
        float progress = f.LapDistPct - _lapStartPct;
        if (progress < 0) progress += 1;
        float used = _lapFuelStart - f.FuelLevel;
        return progress > 0.25f && used > 0.01f ? new List<double> { used / progress } : new();
    }

    // ------------------------------------------------------------------ publishing

    void PublishIdle()
    {
        _lastPublish = DateTime.Now;
        _state = new LiveState { Status = "waiting", Source = _source.Name, Messages = Engineer.Recent() };
    }

    void Publish(Frame f)
    {
        _lastPublish = DateTime.Now;
        var si = _info!;
        var reference = _ref;
        var model = _model;
        float L = si.TrackLengthM;
        float d = f.LapDistPct * L;
        bool inCar = f.IsOnTrack && !f.IsReplayPlaying;
        var ctx = _tracker.Current;

        if ((DateTime.Now - _lastSlow).TotalMilliseconds > 250)
        {
            _lastSlow = DateTime.Now;
            ComputeField(f, si);
            _weatherLive = BuildWeather(f);
        }

        var s = new LiveState
        {
            Status = !_source.IsLive ? "replay" : inCar ? "driving" : "connected",
            Source = _source.Name,
            InCar = inCar,
            OnPitRoad = f.OnPitRoad,
            Car = si.CarName, CarPath = si.CarPath, CarClass = si.CarClass,
            Track = si.TrackLabel, TrackKey = si.TrackKey, TrackLength = L,
            SessionType = ctx?.SessionType ?? si.Session(f.SessionNum)?.Type ?? "",
            SessionTimeRemain = f.SessionTimeRemain,
            SessionLapsRemain = f.SessionLapsRemainEx is > 0 and < 30000 ? f.SessionLapsRemainEx : -1,
            SessionLapsTotal = si.Session(f.SessionNum)?.Laps ?? -1,
            Flags = FlagNames(f.SessionFlags),
            SessionDbId = ctx?.DbId ?? 0,
            Position = f.PlayerCarPosition,
            ClassPosition = f.PlayerCarClassPosition,
            CarsInClass = _carsInClass,
            Lap = f.Lap,
            LapPct = f.LapDistPct,
            LapDist = d,
            LastLapTime = _lastLap,
            SessionBest = _sessionBest,
            PersonalBest = _pb,
            ReferenceLabel = reference?.Label ?? "",
            ReferenceLapTime = reference?.Dist.LapTime ?? double.NaN,
            ReferenceMode = Settings.Current.ReferenceMode,
            OutLap = _tracker.CurrentLapIsOut,
            LastLapDelta = _lastLapDelta,
            Throttle = f.Throttle, Brake = f.Brake, Clutch = f.Clutch, Steer = f.Steer, SteerMax = f.SteerMax, Speed = f.Speed, Rpm = f.RPM, Gear = f.Gear,
            ShiftRpm = si.ShiftRpm, RedLine = si.RedLine, Abs = f.AbsActive, BrakeBias = f.BrakeBias,
            HistThrottle = Ring(_hThr), HistBrake = Ring(_hBrk), HistSteer = Ring(_hSteer), HistSpeed = Ring(_hSpd),
            HistRefThrottle = Ring(_hRThr), HistRefBrake = Ring(_hRBrk), HistRefSpeed = Ring(_hRSpd),
            LastCorner = _lastCorner,
            LapCorners = _lapCorners.ToList(),
            TankCapacity = si.TankCapacityL,
            Weather = _weatherLive,
            Standings = _standings, Relative = _relative, ClassSof = _sof, Radar = _radar, MapCars = _mapCars,
            CarLeftRight = f.CarLeftRight,
            TrackModelVersion = model == null ? 0 : (int)(model.SourceLapId % 1_000_000_000) + 1,
            Messages = Engineer.Recent(),
            TyreLoad = TyreManager.Snapshot(),
            CoachTip = Coach.LastTip,
            SetupState = SetupEngineer.State.ToString(),
            SetupStatus = SetupEngineer.Status,
            SetupInstruction = SetupEngineer.Instruction,
            QuietMode = Radio.Quiet,
            InCarValues = new Dictionary<string, float>(f.Dc),
        };

        if (_tracker.LapInProgress)
        {
            double curT = f.SessionTime - _tracker.CurrentLapStartTime;
            s.CurrentLapTime = _tracker.CurrentLapIsOut ? double.NaN : curT;
            if (reference != null && !_tracker.CurrentLapIsOut)
            {
                float refT = reference.Dist.At(reference.Dist.Time, d);
                if (float.IsFinite(refT))
                {
                    s.Delta = curT - refT;
                    s.PredictedLap = reference.Dist.LapTime + s.Delta;
                    // trend: change of delta over the last ~50 m
                    float back = Math.Max(0, d - 50);
                    float curBack = _cur != null ? _cur.At(_cur.Time, back) : float.NaN;
                    float refBack = reference.Dist.At(reference.Dist.Time, back);
                    if (float.IsFinite(curBack) && float.IsFinite(refBack)) s.DeltaRate = s.Delta - (curBack - refBack);
                }
                s.RefThrottle = reference.Dist.At(reference.Dist.Throttle, d);
                s.RefBrake = reference.Dist.At(reference.Dist.Brake, d);
                s.RefSpeed = reference.Dist.At(reference.Dist.Speed, d);
                s.RefGear = (int)reference.Dist.At(reference.Dist.Gear, d);
            }
            s.Sectors = BuildSectors(d, curT, reference);
        }

        // brake boards: next reference braking point ahead
        if (model != null && reference != null && inCar)
        {
            foreach (var c in model.Corners)
            {
                var rc = reference.Analysis.Corners.FirstOrDefault(x => x.Corner == c.Index);
                if (rc == null) continue;
                float bp = float.IsFinite(rc.BrakePoint) ? rc.BrakePoint : rc.LiftPoint;
                if (!float.IsFinite(bp)) continue;
                float dist = bp - d;
                if (dist < -L / 2) dist += L;
                if (dist >= -5 && dist < 400)
                {
                    s.NextBrakeDist = dist;
                    s.NextCorner = c.Name;
                    s.NextCornerRefMinSpeed = rc.MinSpeed * 3.6f;
                    break;
                }
            }
        }

        if (model != null)
        {
            var (x, y) = model.PosAt(d);
            s.PlayerX = x; s.PlayerY = y;
            var (ax, ay) = model.PosAt(d - 6); var (bx, by) = model.PosAt(d + 6);
            s.PlayerHeading = MathF.Atan2(by - ay, bx - ax);
            if (_trailCount > 1)
            {
                var tx = new float[_trailCount]; var ty = new float[_trailCount];
                for (int i = 0; i < _trailCount; i++)
                {
                    int k = (_trailPos - _trailCount + i + TrailN) % TrailN;
                    tx[i] = _trailX[k]; ty[i] = _trailY[k];
                }
                s.TrailX = tx; s.TrailY = ty;
                s.PlayerX = tx[^1]; s.PlayerY = ty[^1];
            }
            if (reference != null) EnsureAligned(reference, model);
        }
        s.Fuel = ComputeFuel(f);
        s.Tyres = BuildTyres(f, si);
        s.Health = BuildHealth(f, si);
        _state = s;
    }

    List<SectorLive> BuildSectors(float d, double curT, ReferenceLap? reference)
    {
        var list = new List<SectorLive>();
        var cur = _cur;
        int n = _sectorStarts.Length;
        float L = _info!.TrackLengthM;
        for (int i = 0; i < n; i++)
        {
            float a = _sectorStarts[i] * L, b = (i + 1 < n ? _sectorStarts[i + 1] : 1f) * L;
            var sl = new SectorLive { Index = i + 1, StartPct = _sectorStarts[i] };
            if (_bestSectors.Length == n) sl.BestTime = _bestSectors[i];
            if (reference != null) sl.RefTime = reference.Dist.TimeBetween(a, Math.Min(b, reference.Dist.Length));
            if (cur != null && !_tracker.CurrentLapIsOut)
            {
                if (d >= b)
                {
                    sl.State = "done";
                    sl.Time = cur.TimeBetween(a, b);
                    sl.Delta = sl.Time - sl.RefTime;
                    sl.Color = double.IsFinite(sl.BestTime) && sl.Time < sl.BestTime - 0.0005 ? "purple" : double.IsFinite(sl.Delta) ? (sl.Delta <= 0 ? "green" : "yellow") : "";
                }
                else if (d >= a)
                {
                    sl.State = "current";
                    float ta = cur.At(cur.Time, a);
                    sl.Time = curT - ta;
                    if (reference != null)
                    {
                        double running = sl.Time - (reference.Dist.At(reference.Dist.Time, d) - reference.Dist.At(reference.Dist.Time, a));
                        sl.Delta = running;
                    }
                }
            }
            list.Add(sl);
        }
        return list;
    }

    List<TyreLive> BuildTyres(Frame f, SessionInfo si)
    {
        var target = Settings.Current.TyreTargetFor(si.CarPath, si.CarCategory);
        var list = new List<TyreLive>();
        for (int i = 0; i < 4; i++)
        {
            var t = f.Tyres[i];
            bool left = i % 2 == 0;
            bool surface = float.IsFinite(t.TempM) && t.TempM > 0;
            var tl = new TyreLive
            {
                Name = Frame.TyreNames[i],
                Pressure = t.Pressure,
                ColdPressure = t.ColdPressure,
                Carcass = !surface,
                TempIn = surface ? (left ? t.TempR : t.TempL) : (left ? t.TempCR : t.TempCL),
                TempMid = surface ? t.TempM : t.TempCM,
                TempOut = surface ? (left ? t.TempL : t.TempR) : (left ? t.TempCL : t.TempCR),
                Wear = float.IsFinite(t.WearM) ? (t.WearL + t.WearM + t.WearR) / 3 : float.NaN,
            };
            if (float.IsFinite(tl.Pressure) && tl.Pressure > 0)
                tl.PressStatus = tl.Pressure < target.HotPressureMin ? "low" : tl.Pressure > target.HotPressureMax ? "high" : "ok";
            float avg = (tl.TempIn + tl.TempMid + tl.TempOut) / 3;
            if (float.IsFinite(avg)) tl.TempStatus = avg < target.TempMin ? "cold" : avg > target.TempMax ? "hot" : "ok";
            list.Add(tl);
        }
        return list;
    }

    HealthLive BuildHealth(Frame f, SessionInfo si) => new()
    {
        WaterTemp = f.WaterTemp, OilTemp = f.OilTemp, OilPress = f.OilPress, Voltage = f.Voltage,
        Warnings = WarningNames(f.EngineWarnings).ToList(),
        RepairLeft = float.IsFinite(f.PitRepairLeft) ? f.PitRepairLeft : 0,
        OptRepairLeft = float.IsFinite(f.PitOptRepairLeft) ? f.PitOptRepairLeft : 0,
        RepairFlag = f.Flag(Irsdk.Flags.Repair),
        PossibleDamage = _possibleDamage,
        PaceLossPct = _paceLoss,
        Incidents = f.PlayerCarMyIncidentCount,
        IncidentLimit = si.IncidentLimit,
    };

    WeatherLive BuildWeather(Frame f) => new()
    {
        AirTemp = f.AirTemp, TrackTemp = f.TrackTemp, Skies = Irsdk.SkiesName(f.Skies), Wetness = Irsdk.WetnessName(f.TrackWetness),
        WetnessLevel = f.TrackWetness, Precipitation = float.IsFinite(f.Precipitation) ? f.Precipitation : 0,
        WindSpeed = float.IsFinite(f.WindVel) ? f.WindVel : 0, WindDir = float.IsFinite(f.WindDir) ? f.WindDir : 0,
        Humidity = float.IsFinite(f.Humidity) ? f.Humidity : 0, DeclaredWet = f.DeclaredWet,
        TrackTempTrend = _weather.TrackTempRatePerHour,
        Forecast = _weather.Project(15, 30, 45, 60),
    };

    static List<string> FlagNames(uint flags)
    {
        var l = new List<string>();
        void A(Irsdk.Flags f, string n) { if ((flags & (uint)f) != 0) l.Add(n); }
        A(Irsdk.Flags.Checkered, "checkered"); A(Irsdk.Flags.White, "white"); A(Irsdk.Flags.Green, "green"); A(Irsdk.Flags.Yellow, "yellow");
        A(Irsdk.Flags.YellowWaving, "yellow"); A(Irsdk.Flags.Red, "red"); A(Irsdk.Flags.Blue, "blue"); A(Irsdk.Flags.Debris, "debris");
        A(Irsdk.Flags.Black, "black"); A(Irsdk.Flags.Disqualify, "dq"); A(Irsdk.Flags.Repair, "repair"); A(Irsdk.Flags.Caution, "caution");
        A(Irsdk.Flags.CautionWaving, "caution");
        return l.Distinct().ToList();
    }

    // ------------------------------------------------------------------ field (standings / relative / radar / map)

    void ComputeField(Frame f, SessionInfo si)
    {
        var map = new List<MapCar>();
        int me = si.PlayerCarIdx;
        if (!f.HasCarIdx)
        {
            map.Add(new MapCar { CarIdx = me, Pct = f.LapDistPct, IsPlayer = true, ClassColor = si.Player?.ClassColor ?? "#fff", Position = f.PlayerCarPosition, Number = si.Player?.CarNumber ?? "" });
            _mapCars = map;
            _standings = new(); _relative = new(); _radar = new(); _sof = new();
            return;
        }

        float L = si.TrackLengthM;
        var meDrv = si.Player;
        float lapEst = meDrv?.ClassEstLapTime > 0 ? meDrv.ClassEstLapTime : si.EstLapTime > 0 ? si.EstLapTime : 90;
        bool isRace = si.Session(f.SessionNum)?.IsRace ?? false;
        var rows = new List<CarRow>();
        foreach (var drv in si.Drivers)
        {
            int i = drv.CarIdx;
            if (i < 0 || i >= Frame.MaxCars || drv.IsPaceCar || drv.IsSpectator) continue;
            int surface = f.CarIdxTrackSurface[i];
            bool inWorld = surface != Irsdk.TrkLoc.NotInWorld;
            var row = new CarRow
            {
                CarIdx = i, Number = drv.CarNumber, Name = drv.Name, ClassName = drv.CarClassShort, ClassColor = drv.ClassColor,
                IRating = drv.IRating, License = drv.LicString, LicenseColor = drv.LicColor,
                Position = f.CarIdxPosition[i], ClassPosition = f.CarIdxClassPosition[i],
                LastLap = f.CarIdxLastLapTime[i] > 0 ? f.CarIdxLastLapTime[i] : double.NaN,
                BestLap = f.CarIdxBestLapTime[i] > 0 ? f.CarIdxBestLapTime[i] : double.NaN,
                Lap = f.CarIdxLap[i], InPit = f.CarIdxOnPitRoad[i] || surface == Irsdk.TrkLoc.InPitStall,
                IsPlayer = i == me, Pct = f.CarIdxLapDistPct[i],
                Tyre = CompoundLetter(si, f.CarIdxTireCompound[i]),
            };
            if (isRace && f.CarIdxF2Time[i] > 0) row.Gap = f.CarIdxF2Time[i];
            if (inWorld)
            {
                // relative time via each car's estimated time around the lap
                double dt = f.CarIdxEstTime[i] - f.CarIdxEstTime[me];
                if (dt > lapEst / 2) dt -= lapEst; else if (dt < -lapEst / 2) dt += lapEst;
                row.Relative = dt;
                int lapDiff = (f.CarIdxLap[i] + f.CarIdxLapDistPct[i] > f.CarIdxLap[me] + f.CarIdxLapDistPct[me] + 0.5f) ? 1
                            : (f.CarIdxLap[i] + f.CarIdxLapDistPct[i] < f.CarIdxLap[me] + f.CarIdxLapDistPct[me] - 0.5f) ? -1 : 0;
                row.LapDiff = lapDiff;
                map.Add(new MapCar { CarIdx = i, Pct = f.CarIdxLapDistPct[i], IsPlayer = i == me, ClassColor = drv.ClassColor, Position = row.ClassPosition, Number = drv.CarNumber, InPit = row.InPit });
            }
            rows.Add(row);
        }

        // order: official positions first (0 = no position yet), then best lap
        var ordered = rows.OrderBy(r => r.Position > 0 ? r.Position : 1000)
                          .ThenBy(r => double.IsFinite(r.BestLap) ? r.BestLap : 1e9)
                          .ToList();
        // gaps / intervals per class
        _sof = new Dictionary<string, int>();
        foreach (var cls in ordered.GroupBy(r => r.ClassName))
        {
            var list = cls.ToList();
            double leaderBest = list.Where(r => double.IsFinite(r.BestLap)).Select(r => r.BestLap).DefaultIfEmpty(double.NaN).Min();
            for (int k = 0; k < list.Count; k++)
            {
                var r = list[k];
                if (!isRace && double.IsFinite(r.BestLap)) r.Gap = r.BestLap - leaderBest;
                if (r.ClassPosition <= 0) r.ClassPosition = k + 1;
                r.Interval = k == 0 ? double.NaN : r.Gap - list[k - 1].Gap;
            }
            var ratings = list.Where(r => r.IRating > 0).Select(r => r.IRating).ToList();
            if (ratings.Count > 0)
            {
                double br = 1600 / Math.Log(2);
                double sum = ratings.Sum(ir => Math.Exp(-ir / br));
                _sof[cls.Key] = (int)Math.Round(br * Math.Log(ratings.Count / sum));
            }
            if (isRace && list.Count >= 2 && list.All(r => r.IRating > 0))
            {
                var est = IRatingEstimator.Estimate(list.Select(r => r.IRating).ToList());
                for (int k = 0; k < list.Count; k++) list[k].IRatingChange = est[k];
            }
        }
        _carsInClass = rows.Count(r => r.ClassName == (meDrv?.CarClassShort ?? ""));
        _standings = ordered;

        // relative: closest cars by time, 4 ahead / 4 behind
        var rel = rows.Where(r => double.IsFinite(r.Relative) && !(r.InPit && !r.IsPlayer && f.CarIdxTrackSurface[r.CarIdx] == Irsdk.TrkLoc.InPitStall)).ToList();
        var ahead = rel.Where(r => r.Relative > 0 && !r.IsPlayer).OrderBy(r => r.Relative).Take(4).Reverse();
        var behind = rel.Where(r => r.Relative <= 0 && !r.IsPlayer).OrderByDescending(r => r.Relative).Take(4);
        var player = rows.FirstOrDefault(r => r.IsPlayer);
        _relative = ahead.Concat(player != null ? new[] { player } : Array.Empty<CarRow>()).Concat(behind).ToList();

        // radar: cars within 40 m along the track
        var radar = new List<RadarCar>();
        float myPct = f.CarIdxLapDistPct[me];
        foreach (var r in rows)
        {
            if (r.IsPlayer || r.InPit) continue;
            float dp = f.CarIdxLapDistPct[r.CarIdx] - myPct;
            if (dp > 0.5f) dp -= 1; else if (dp < -0.5f) dp += 1;
            float m = dp * L;
            if (Math.Abs(m) < 40 && f.CarIdxTrackSurface[r.CarIdx] >= Irsdk.TrkLoc.OffTrack) radar.Add(new RadarCar { CarIdx = r.CarIdx, Dist = m, ClassColor = r.ClassColor });
        }
        _radar = radar;
        _mapCars = map;
    }

    static string CompoundLetter(SessionInfo si, int compound)
    {
        if (compound < 0) return "";
        var name = compound < si.TireCompounds.Count ? si.TireCompounds[compound] : "";
        return name.Length > 0 ? name[..1].ToUpperInvariant() : compound == 0 ? "D" : "W";
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(1000);
        _work.CompleteAdding();
        try { _tracker.Flush(); } catch { }
        _source.Dispose();
        _live.Dispose();
        CrewChief.Dispose();
    }
}
