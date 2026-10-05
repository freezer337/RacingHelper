using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RacingHelper.Analysis;
using RacingHelper.Live;
using RacingHelper.Sim;
using RacingHelper.Storage;

namespace RacingHelper.Web;

/// <summary>Serialises NaN / ∞ as null so the browser gets clean JSON.</summary>
sealed class FiniteDoubleConverter : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => r.TokenType == JsonTokenType.Null ? double.NaN : r.TokenType == JsonTokenType.String && double.TryParse(r.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : r.GetDouble();
    public override void Write(Utf8JsonWriter w, double v, JsonSerializerOptions o) { if (double.IsFinite(v)) w.WriteNumberValue(Math.Round(v, 6)); else w.WriteNullValue(); }
}

sealed class FiniteFloatConverter : JsonConverter<float>
{
    public override float Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => r.TokenType == JsonTokenType.Null ? float.NaN : r.TokenType == JsonTokenType.String && float.TryParse(r.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : r.GetSingle();
    public override void Write(Utf8JsonWriter w, float v, JsonSerializerOptions o) { if (float.IsFinite(v)) w.WriteNumberValue(Math.Round(v, 4)); else w.WriteNullValue(); }
}

public sealed class WebServer : IAsyncDisposable
{
    readonly TelemetryHub _hub;
    readonly IbtWatcher _watcher;
    readonly IAppBridge _app;
    WebApplication? _web;
    public string Url { get; private set; } = "";
    bool _lan;
    int _port;

    /// <summary>Addresses other devices on your network (phone, tablet) can open, when "Allow LAN" is on.</summary>
    public List<string> LanUrls() => _lan ? LanAccess.Addresses(_port).Select(a => a.Url).ToList() : new();

    public static readonly JsonSerializerOptions Json = Create();
    static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, IncludeFields = true, NumberHandling = JsonNumberHandling.AllowReadingFromString };
        o.Converters.Add(new FiniteDoubleConverter());
        o.Converters.Add(new FiniteFloatConverter());
        return o;
    }

    // background import job
    volatile string _importStatus = "idle";
    volatile int _importDone, _importTotal;

    public WebServer(TelemetryHub hub, IbtWatcher watcher, IAppBridge app)
    {
        _hub = hub;
        _watcher = watcher;
        _app = app;
    }

    Database Db => _hub.Store.Db;
    AppSettings S => _hub.Settings.Current;

    public async Task StartAsync()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory, WebRootPath = root });
        builder.Logging.ClearProviders();
        int port = S.WebPort;
        builder.WebHost.UseUrls(S.AllowLan ? $"http://0.0.0.0:{port}" : $"http://127.0.0.1:{port}");
        _lan = S.AllowLan; _port = port;
        var app = builder.Build();
        app.UseWebSockets();
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = new PhysicalFileProvider(root) });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(root),
            OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache",
        });
        Map(app);
        await app.StartAsync();
        _web = app;
        Url = $"http://127.0.0.1:{port}/";
    }

    static IResult J(object? o) => Results.Json(o, Json);

    /// <summary>Things the desktop app does itself (bindable to keys and wheel buttons like the questions).</summary>
    public static readonly (string id, string label)[] AppActions =
    {
        ("overlays-edit", "Move / resize overlays (press again to lock)"),
        ("overlays-toggle", "Hide / show all overlays"),
        ("reference", "Switch delta reference: PB → session best → last lap"),
        ("dashboard", "Open the dashboard"),
    };

    static IEnumerable<(string id, string label)> AllActions => AppActions.Concat(TelemetryHub.Questions);
    IReadOnlyList<string> _keyErrors = Array.Empty<string>();

    void Map(WebApplication app)
    {
        // ---------------- status / live ----------------
        app.MapGet("/api/status", () => J(new
        {
            status = _hub.State.Status,
            source = _hub.State.Source,
            version = AppInfo.Version,
            replay = _hub.IsReplay,
            dataFolder = S.DataFolder,
            dbFile = Path.Combine(S.DataFolder, "racinghelper.db"),
            importer = _watcher.Status,
            importQueue = _watcher.QueueLength,
            importJob = new { status = _importStatus, done = _importDone, total = _importTotal },
            sessionId = _hub.CurrentSessionDbId,
            myId = _hub.Analysis.MyDriverId,
            url = Url,
            lanUrls = LanUrls(),
        }));
        app.MapGet("/api/live", () => J(_hub.State));
        app.Map("/ws/live", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            LiveState? last = null;
            var buf = new byte[256];
            var receive = ws.ReceiveAsync(buf, ctx.RequestAborted);
            while (ws.State == WebSocketState.Open && !ctx.RequestAborted.IsCancellationRequested)
            {
                var s = _hub.State;
                if (!ReferenceEquals(s, last))
                {
                    last = s;
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(s, Json);
                    await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ctx.RequestAborted);
                }
                if (receive.IsCompleted) break;
                await Task.Delay(100);
            }
        });
        app.MapGet("/api/track/current", () => J(_hub.Model));
        app.MapGet("/api/engineer", () => J(_hub.Engineer.Recent(50)));
        app.MapPost("/api/engineer/test", () =>
        {
            // CrewChief only speaks in a session (not in the menus or before Start Application); the text says why not
            var cc = _hub.CrewChief;
            string text = _hub.RadioCheck();
            return J(new { ok = cc.Live, text, crewChiefConnected = cc.Connected, crewChiefLive = cc.Live });
        });
        app.MapGet("/api/crewchief", () => J(_hub.CrewChief.Describe()));

        // ---------------- phone / tablet access ----------------
        app.MapGet("/api/lan", () =>
        {
            string exe = Environment.ProcessPath ?? "";
            var (blocks, allowRule) = _lan ? LanAccess.Firewall(exe) : (0, false);
            return J(new
            {
                allowLan = S.AllowLan,
                listening = _lan,
                port = _port,
                addresses = LanAccess.Addresses(_port),
                networks = _lan ? LanAccess.NetworkCategories() : new List<string>(),
                firewallBlocks = blocks,
                firewallRule = allowRule,
                windows = OperatingSystem.IsWindows(),
            });
        });
        app.MapPost("/api/lan/firewall", () =>
        {
            var (ok, message) = LanAccess.FixFirewall(Environment.ProcessPath ?? "", _port);
            return J(new { ok, message });
        });

        // ---------------- questions & wheel buttons ----------------
        app.MapPost("/api/ask/{id}", (string id) => J(new { answer = _hub.Ask(id) }));
        app.MapGet("/api/controls", () => J(new
        {
            questions = AllActions.Select(q => new { q.id, q.label, hotkey = S.EffectiveKeys().FirstOrDefault(k => k.Action == q.id)?.Keys, app = AppActions.Any(a => a.id == q.id) }),
            keyErrors = _keyErrors,
            bindings = S.ButtonBindings,
            controllers = _app.Controllers(),
            quiet = _hub.Radio.Quiet,
        }));
        app.MapPost("/api/controls/learn", async (HttpRequest req) =>
        {
            var body = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(req.Body, Json) ?? new();
            var action = body.GetValueOrDefault("action") ?? "";
            if (!AllActions.Any(q => q.id == action)) return J(new { ok = false, error = "Unknown action" });
            var press = await _app.LearnButton(10000);
            if (press == null) return J(new { ok = false, error = "No button pressed within 10 seconds." });
            _hub.Settings.Update(s =>
            {
                s.ButtonBindings.RemoveAll(b => b.Action == action || (b.Device == press.Device && b.Button == press.Button));
                s.ButtonBindings.Add(new ButtonBinding { Action = action, Device = press.Device, DeviceName = press.DeviceName, Button = press.Button });
            });
            return J(new { ok = true, binding = S.ButtonBindings.First(b => b.Action == action) });
        });
        app.MapPost("/api/controls/key", async (HttpRequest req) =>
        {
            var body = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(req.Body, Json) ?? new();
            var action = body.GetValueOrDefault("action") ?? "";
            var keys = (body.GetValueOrDefault("keys") ?? "").Trim();
            if (!AllActions.Any(q => q.id == action)) return J(new { ok = false, error = "Unknown action" });
            _hub.Settings.Update(s =>
            {
                var list = s.EffectiveKeys();
                list.RemoveAll(k => k.Action == action || (keys.Length > 0 && string.Equals(k.Keys, keys, StringComparison.OrdinalIgnoreCase)));
                if (keys.Length > 0) list.Add(new KeyBinding { Action = action, Keys = keys });
                s.KeyBindings = list;
            });
            _keyErrors = _app.ApplyHotkeys();
            bool failed = _keyErrors.Any(e => e.StartsWith(keys + " ", StringComparison.OrdinalIgnoreCase));
            return J(new { ok = !failed, error = failed ? $"Windows wouldn't give Racing Helper {keys}: another program already uses it. Pick another." : null });
        });
        app.MapPost("/api/controls/keys-reset", () =>
        {
            _hub.Settings.Update(s => s.KeyBindings = null);
            _keyErrors = _app.ApplyHotkeys();
            return J(new { ok = true });
        });
        app.MapPost("/api/controls/clear", async (HttpRequest req) =>
        {
            var body = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(req.Body, Json) ?? new();
            var action = body.GetValueOrDefault("action") ?? "";
            _hub.Settings.Update(s => s.ButtonBindings.RemoveAll(b => b.Action == action));
            return J(new { ok = true });
        });
        app.MapGet("/api/setup-session", () => J(new { state = _hub.SetupEngineer.State.ToString(), status = _hub.SetupEngineer.Status, instruction = _hub.SetupEngineer.Instruction, describe = _hub.SetupEngineer.Describe(), laps = _hub.SetupEngineer.LapsPerRun }));
        app.MapPost("/api/crewchief/configure", () =>
        {
            var (ok, message) = _hub.CrewChief.ConfigureCrewChief();
            return J(new { ok, message });
        });
        app.MapPost("/api/crewchief/restart", async () =>
        {
            await _hub.CrewChief.RestartAsync();
            return J(_hub.CrewChief.Describe());
        });

        // ---------------- sessions & laps ----------------
        app.MapGet("/api/sessions", (string? car, string? track) => J(Db.GetSessions(car, track)));
        app.MapGet("/api/sessions/{id:long}", (long id) =>
        {
            var s = Db.GetSession(id);
            if (s == null) return Results.NotFound();
            var laps = Db.GetLaps(id);
            SessionAnalyzer.AssignStints(laps);
            var setups = laps.Select(l => l.SetupHash).Where(h => h.Length > 0).Distinct().ToDictionary(h => h, h => Db.GetSetup(h)?.name ?? "");
            return J(new { session = s, laps, pitStops = Db.GetPitStops(id), setups });
        });
        app.MapGet("/api/sessions/{id:long}/report", (long id, long? refLap) => J(_hub.Analysis.SessionReport(id, refLap)));
        app.MapDelete("/api/sessions/{id:long}", (long id) => { Db.DeleteSession(id); return J(new { ok = true }); });
        app.MapPost("/api/sessions/{id:long}/notes", async (long id, HttpRequest req) =>
        {
            var body = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(req.Body, Json);
            Db.SetSessionNotes(id, body?.GetValueOrDefault("notes") ?? "");
            return J(new { ok = true });
        });
        app.MapGet("/api/combos", () => J(Db.GetCombos(null).Select(c => new { c.carPath, c.carName, c.trackKey, c.trackName, c.trackConfig, c.laps, c.best, c.last })));

        app.MapGet("/api/compare", (string laps, int? step) => J(Compare(laps, step ?? 2)));
        app.MapGet("/api/track/{key}/model", (string key) => J(_hub.Store.GetTrackModel(Uri.UnescapeDataString(key))));
        app.MapPost("/api/track/{key}/reset", (string key) => { _hub.Store.ResetTrackModel(Uri.UnescapeDataString(key)); return J(new { ok = true }); });

        app.MapGet("/api/leaderboard", (string car, string track) =>
        {
            int me = _hub.Analysis.MyDriverId;
            var all = Db.GetBestLaps(car, track, 400, null, false);
            var mine = all.Where(l => l.DriverId == me).Take(50).ToList();
            var rivals = all.Where(l => l.DriverId != me).GroupBy(l => l.DriverId).Select(g => g.First()).OrderBy(l => l.LapTime).Take(50).ToList();
            var sessions = all.Select(l => l.SessionId).Distinct().ToDictionary(id => id, id => Db.GetSession(id));
            return J(new
            {
                mine = mine.Select(l => LapSummary(l, sessions)),
                rivals = rivals.Select(l => LapSummary(l, sessions)),
                theoretical = Theoretical(mine.Take(15).ToList()),
            });
        });
        app.MapGet("/api/personal-bests", () =>
        {
            int me = _hub.Analysis.MyDriverId;
            return J(Db.GetPersonalBests().Where(l => l.DriverId == me).Select(l => new { l.Id, l.CarPath, l.TrackKey, l.LapTime, l.StartedAt, l.SessionId, session = Db.GetSession(l.SessionId) }));
        });
        app.MapPost("/api/reference", async (HttpRequest req) =>
        {
            var body = await JsonSerializer.DeserializeAsync<Dictionary<string, JsonElement>>(req.Body, Json) ?? new();
            string mode = body.TryGetValue("mode", out var m) ? m.GetString() ?? "pb" : "pb";
            long lap = body.TryGetValue("lapId", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt64() : 0;
            _hub.SetReference(mode, lap);
            return J(new { ok = true });
        });

        // ---------------- fuel ----------------
        app.MapPost("/api/fuel/plan", async (HttpRequest req) => J(FuelCalculator.Plan(await JsonSerializer.DeserializeAsync<FuelInput>(req.Body, Json) ?? new FuelInput())));
        app.MapGet("/api/fuel/suggest", (string? car, string? track) =>
        {
            var st = _hub.State;
            car ??= st.CarPath; track ??= st.TrackKey;
            if (string.IsNullOrEmpty(car) || string.IsNullOrEmpty(track)) return J(new { });
            var laps = Db.GetBestLaps(car, track, 400, _hub.Analysis.MyDriverId, false).OrderByDescending(l => l.StartedAt).Take(40).ToList();
            var fuel = laps.Where(l => l.FuelUsed is > 0.05f).Select(l => (double)l.FuelUsed!.Value).ToList();
            var times = laps.Select(l => l.LapTime).OrderBy(x => x).ToList();
            var sess = Db.GetSessions(car, track, 1).FirstOrDefault();
            float tank = st.TankCapacity;
            if (!(tank > 0) && sess != null)
            {
                var y = Db.GetSessionInfoYaml(sess.Id);
                if (y != null) tank = new SessionInfo(y).TankCapacityL;
            }
            return J(new
            {
                car, track,
                fuelPerLap = fuel.Count > 0 ? fuel.Average() : double.NaN,
                fuelPerLapMax = fuel.Count > 0 ? fuel.Max() : double.NaN,
                lapTime = times.Count > 0 ? times[times.Count / 2] : double.NaN,
                bestLap = times.Count > 0 ? times[0] : double.NaN,
                tank,
                samples = fuel.Count,
            });
        });
        app.MapPost("/api/pit/fuel", async (HttpRequest req) =>
        {
            var body = await JsonSerializer.DeserializeAsync<Dictionary<string, double>>(req.Body, Json) ?? new();
            return J(new { ok = _hub.ApplyFuel(body.GetValueOrDefault("litres")) });
        });

        // ---------------- tyres ----------------
        app.MapGet("/api/tyres/session/{id:long}", (long id, int? stint, string? mode) =>
        {
            var s = Db.GetSession(id);
            if (s == null) return Results.NotFound();
            var laps = Db.GetLaps(id);
            SessionAnalyzer.AssignStints(laps);
            var target = S.TyreTargetFor(s.CarPath, s.CarCategory);
            var sel = laps.Where(l => l.Tyres != null && !l.OutLap && (stint == null || l.Stint == stint)).ToList();
            var advice = TyreAnalyzer.Analyze(sel.Select(l => l.Tyres!).ToList(), target, mode == "max");
            return J(new
            {
                target,
                carPath = s.CarPath,
                laps = laps.Where(l => l.Tyres != null).Select(l => new { l.Id, l.LapNumber, l.Stint, l.LapTime, l.Valid, l.OutLap, tyres = l.Tyres, l.TrackTemp, l.AirTemp }),
                advice,
                stints = laps.Select(l => l.Stint).Distinct().OrderBy(x => x),
            });
        });
        app.MapPost("/api/tyres/target", async (HttpRequest req) =>
        {
            var body = await JsonSerializer.DeserializeAsync<TyreTargetRequest>(req.Body, Json);
            if (body?.CarPath is { Length: > 0 } && body.Target != null) _hub.Settings.Update(s => s.TyreTargets[body.CarPath] = body.Target);
            return J(new { ok = true });
        });
        app.MapPost("/api/tyres/apply", async (HttpRequest req) =>
        {
            var kpa = await JsonSerializer.DeserializeAsync<float[]>(req.Body, Json) ?? Array.Empty<float>();
            return J(new { ok = _hub.ApplyTyrePressures(kpa) });
        });

        // ---------------- setup optimiser / journal / installer ----------------
        app.MapGet("/api/setup/symptoms", () => J(SetupOptimiser.Symptoms.Select(s => new { s.id, s.label })));
        app.MapPost("/api/setup/advise", async (HttpRequest req, long? session) =>
        {
            var r = await JsonSerializer.DeserializeAsync<SetupRequest>(req.Body, Json) ?? new SetupRequest();
            var (setup, category, source, fixedSetup) = SetupFor(session);
            if (string.IsNullOrEmpty(r.Category) || r.Category == "auto") r.Category = category;
            return J(new { advice = SetupOptimiser.Advise(r, setup, fixedSetup), source, category, fixedSetup });
        });
        app.MapGet("/api/setup/auto", (long session) =>
        {
            var rep = _hub.Analysis.SessionReport(session);
            var (setup, category, source, fixedSetup) = SetupFor(session);
            if (rep?.Handling == null || !rep.Handling.Valid) return J(new { handling = rep?.Handling, items = Array.Empty<object>(), source });
            var reqs = SetupOptimiser.FromHandling(rep.Handling, category);
            // tyre-driven requests
            if (rep.Tyres.Count == 4)
            {
                if (rep.Tyres[0].TempStatus == "hot" || rep.Tyres[1].TempStatus == "hot") reqs.Add(new SetupRequest { Symptom = "tyres-hot-front", Category = category });
                if (rep.Tyres[2].TempStatus == "hot" || rep.Tyres[3].TempStatus == "hot") reqs.Add(new SetupRequest { Symptom = "tyres-hot-rear", Category = category });
            }
            // only a pattern, not the odd moment: on at least every other lap
            int often = Math.Max(3, (rep.ValidLaps + 1) / 2);
            if (rep.Style?.Wheelspins >= often) reqs.Add(new SetupRequest { Symptom = "traction", Category = category });
            if (rep.Corners.Sum(c => c.LockupsFront) >= often) reqs.Add(new SetupRequest { Symptom = "front-lockup", Category = category });
            if (rep.Corners.Sum(c => c.LockupsRear) >= often) reqs.Add(new SetupRequest { Symptom = "rear-lockup", Category = category });
            // only symptoms this car can actually do something about
            var items = reqs.Select(q => new { request = q, advice = SetupOptimiser.Advise(q, setup, fixedSetup) }).Where(x => x.advice.Changes.Count > 0).ToList();
            return J(new { handling = rep.Handling, items, source, fixedSetup });
        });
        app.MapGet("/api/setup/current", (long? session) =>
        {
            var (setup, category, source, fixedSetup) = SetupFor(session);
            var flat = new List<KeyValuePair<string, string>>();
            setup?.Flatten("", flat);
            return J(new { source, category, values = flat.Select(kv => new { key = kv.Key, value = kv.Value }) });
        });
        app.MapGet("/api/setup/journal", (string car, string track) => J(Journal(car, track)));

        app.MapGet("/api/setups/library", () => J(new
        {
            folder = S.SetupLibraryFolder,
            target = S.IRacingSetupsFolder,
            files = _hub.Setups.Scan(),
            exists = Directory.Exists(S.SetupLibraryFolder),
        }));
        app.MapPost("/api/setups/install", async (HttpRequest req) =>
        {
            var body = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(req.Body, Json) ?? new();
            string car = body.GetValueOrDefault("carPath") ?? _hub.State.CarPath;
            bool all = body.GetValueOrDefault("ignoreFilters") == "true";
            if (string.IsNullOrEmpty(car))
            {
                // install everything in the library
                var results = _hub.Setups.Scan().Select(f => f.CarPath).Distinct().Select(c => _hub.Setups.Install(c, ignoreFilters: all)).ToList();
                return J(results);
            }
            return J(new[] { _hub.Setups.Install(car, _hub.Info?.TrackName ?? "", _hub.Info?.TrackDisplayName ?? "", all) });
        });
        app.MapPost("/api/open-folder", async (HttpRequest req) =>
        {
            var body = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(req.Body, Json) ?? new();
            var which = body.GetValueOrDefault("which");
            string path = which switch
            {
                "library" => S.SetupLibraryFolder,
                "iracing-setups" => S.IRacingSetupsFolder,
                "telemetry" => S.TelemetryFolder,
                _ => S.DataFolder,
            };
            Directory.CreateDirectory(path);
            _app.OpenFolder(path);
            return J(new { ok = true, path });
        });

        // ---------------- settings & overlays ----------------
        app.MapGet("/api/settings", () => J(S));
        app.MapPost("/api/settings", async (HttpRequest req) =>
        {
            var incoming = await JsonSerializer.DeserializeAsync<AppSettings>(req.Body, Json);
            if (incoming == null) return Results.BadRequest();
            incoming.Overlays = S.Overlays; // overlays have their own endpoint
            incoming.TyreTargets = S.TyreTargets;
            incoming.ButtonBindings = S.ButtonBindings;   // bindings have their own endpoints
            incoming.KeyBindings = S.KeyBindings;
            bool restartCrewChief = incoming.CrewChiefEnabled != S.CrewChiefEnabled || incoming.CrewChiefPort != S.CrewChiefPort;
            _hub.Settings.Replace(incoming);
            if (restartCrewChief) await _hub.CrewChief.RestartAsync();
            return J(new { ok = true });
        });
        app.MapGet("/api/overlays", () => J(new
        {
            editMode = _app.OverlayEditMode,
            fps = S.OverlayFps,
            onlyInCar = S.OverlaysOnlyInCar,
            items = _app.OverlayCatalog.Select(o =>
            {
                var c = S.Overlays.FirstOrDefault(x => x.Id == o.Id);
                return new { o.Id, o.Name, o.Description, enabled = c?.Enabled ?? o.DefaultOn, scale = c?.Scale ?? 1, opacity = c?.Opacity ?? 0.92 };
            }),
        }));
        app.MapPost("/api/overlays", async (HttpRequest req) =>
        {
            var body = await JsonSerializer.DeserializeAsync<OverlayUpdate>(req.Body, Json);
            if (body == null) return Results.BadRequest();
            _hub.Settings.Update(s =>
            {
                if (body.Fps is > 0) s.OverlayFps = Math.Clamp(body.Fps.Value, 10, 60);
                if (body.OnlyInCar.HasValue) s.OverlaysOnlyInCar = body.OnlyInCar.Value;
                if (body.Id != null)
                {
                    var c = s.Overlays.FirstOrDefault(x => x.Id == body.Id);
                    if (c == null)
                    {
                        var info = _app.OverlayCatalog.FirstOrDefault(x => x.Id == body.Id);
                        c = new OverlayConfig { Id = body.Id, Enabled = info?.DefaultOn ?? false };
                        s.Overlays.Add(c);
                    }
                    if (body.Enabled.HasValue) c.Enabled = body.Enabled.Value;
                    if (body.Scale.HasValue) c.Scale = Math.Clamp(body.Scale.Value, 0.5, 3);
                    if (body.Opacity.HasValue) c.Opacity = Math.Clamp(body.Opacity.Value, 0.2, 1);
                    if (body.ResetPosition == true) { c.X = double.NaN; c.Y = double.NaN; }
                }
            });
            if (body.EditMode.HasValue) _app.OverlayEditMode = body.EditMode.Value;
            _app.OverlaysChanged();
            return J(new { ok = true });
        });

        // ---------------- import & replay ----------------
        app.MapGet("/api/import/files", () =>
        {
            var folder = S.TelemetryFolder;
            if (!Directory.Exists(folder)) return J(new { folder, files = Array.Empty<object>() });
            var files = new DirectoryInfo(folder).GetFiles("*.ibt").OrderByDescending(f => f.LastWriteTime).Take(3000)
                .Select(f => new
                {
                    path = f.FullName, name = f.Name, size = f.Length, modified = f.LastWriteTime,
                    car = ParseName(f.Name).car, track = ParseName(f.Name).track,
                    imported = Db.IsFileImported(f.FullName, f.Length),
                });
            return J(new { folder, files });
        });
        app.MapPost("/api/import", async (HttpRequest req) =>
        {
            var files = await JsonSerializer.DeserializeAsync<List<string>>(req.Body, Json) ?? new();
            if (_watcher.Busy || _importStatus == "running") return J(new { ok = false, error = "An import is already running." });
            _importTotal = files.Count; _importDone = 0; _importStatus = "running";
            _ = Task.Run(() =>
            {
                try
                {
                    foreach (var f in files)
                    {
                        _watcher.ImportFiles(new[] { f }, false);
                        _importDone++;
                    }
                }
                finally { _importStatus = "idle"; }
            });
            return J(new { ok = true });
        });
        app.MapPost("/api/replay", async (HttpRequest req) =>
        {
            var body = await JsonSerializer.DeserializeAsync<Dictionary<string, JsonElement>>(req.Body, Json) ?? new();
            var file = body.TryGetValue("file", out var f) ? f.GetString() : null;
            double speed = body.TryGetValue("speed", out var sp) && sp.ValueKind == JsonValueKind.Number ? sp.GetDouble() : 1;
            if (file == null || !File.Exists(file)) return J(new { ok = false, error = "File not found" });
            _hub.StartReplay(file, speed);
            return J(new { ok = true });
        });
        app.MapPost("/api/replay/stop", () => { _hub.StopReplay(); return J(new { ok = true }); });
    }

    sealed class OverlayUpdate
    {
        public string? Id { get; set; }
        public bool? Enabled { get; set; }
        public double? Scale { get; set; }
        public double? Opacity { get; set; }
        public bool? ResetPosition { get; set; }
        public bool? EditMode { get; set; }
        public int? Fps { get; set; }
        public bool? OnlyInCar { get; set; }
    }

    sealed class TyreTargetRequest
    {
        public string CarPath { get; set; } = "";
        public TyreTarget? Target { get; set; }
    }

    static (string car, string track) ParseName(string name)
    {
        // "<car>_<track> yyyy-MM-dd HH-mm-ss.ibt"
        var stem = Path.GetFileNameWithoutExtension(name);
        int us = stem.IndexOf('_');
        if (us < 0) return (stem, "");
        var car = stem[..us];
        var rest = stem[(us + 1)..];
        var m = System.Text.RegularExpressions.Regex.Match(rest, @"^(.*) \d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2}$");
        return (car, m.Success ? m.Groups[1].Value : rest);
    }

    object LapSummary(LapRow l, Dictionary<long, SessionRow?> sessions)
    {
        sessions.TryGetValue(l.SessionId, out var s);
        return new
        {
            l.Id, l.LapTime, l.LapNumber, l.StartedAt, l.SectorTimes, l.SessionId, l.DriverName, l.DriverId, l.SessionType, l.TrackTemp, l.Wetness, l.HasGps,
            sessionLabel = s == null ? "" : $"{s.SessionType} {s.StartedAt:g}",
        };
    }

    static double Theoretical(List<LapRow> laps)
    {
        int n = laps.Where(l => l.SectorTimes.Length > 1).Select(l => l.SectorTimes.Length).DefaultIfEmpty(0).Max();
        if (n < 2) return double.NaN;
        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            var v = laps.Where(l => l.SectorTimes.Length == n && l.SectorTimes[i] > 0).Select(l => (double)l.SectorTimes[i]).ToList();
            if (v.Count == 0) return double.NaN;
            sum += v.Min();
        }
        return sum;
    }

    (YNode? setup, string category, string source, bool fixedSetup) SetupFor(long? sessionId)
    {
        if (sessionId is > 0)
        {
            var row = Db.GetSession(sessionId.Value);
            var y = Db.GetSessionInfoYaml(sessionId.Value);
            if (row != null && y != null)
            {
                var si = new SessionInfo(y);
                return (si.CarSetup, si.CarCategory, $"{row.CarName} — {row.SessionType} {row.StartedAt:g}{(si.IsFixedSetup ? " (fixed setup)" : "")}", si.IsFixedSetup);
            }
        }
        var live = _hub.Info;
        if (live != null) return (live.CarSetup, live.CarCategory, $"Live: {live.CarName} ({live.SetupName}){(live.IsFixedSetup ? " (fixed setup)" : "")}", live.IsFixedSetup);
        var last = Db.GetSessions(limit: 1).FirstOrDefault();
        if (last != null)
        {
            var y = Db.GetSessionInfoYaml(last.Id);
            if (y != null) { var si = new SessionInfo(y); return (si.CarSetup, si.CarCategory, $"{last.CarName} — {last.SessionType} {last.StartedAt:g}", si.IsFixedSetup); }
        }
        return (null, "gt", "No setup available", false);
    }

    object Journal(string car, string track)
    {
        var laps = Db.GetBestLaps(car, track, 2000, _hub.Analysis.MyDriverId, false);
        var groups = laps.Where(l => l.SetupHash.Length > 0).GroupBy(l => l.SetupHash)
            .Select(g =>
            {
                var times = g.Select(l => l.LapTime).OrderBy(t => t).ToList();
                var top5 = times.Take(5).ToList();
                return new
                {
                    hash = g.Key,
                    name = Db.GetSetup(g.Key)?.name ?? "",
                    laps = g.Count(),
                    best = times[0],
                    avgTop5 = top5.Average(),
                    first = g.Min(l => l.StartedAt),
                    last = g.Max(l => l.StartedAt),
                    trackTemp = g.Where(l => l.TrackTemp.HasValue).Select(l => (double)l.TrackTemp!.Value).DefaultIfEmpty(double.NaN).Average(),
                    wet = g.Count(l => l.Wetness >= 3) * 2 > g.Count(),
                };
            })
            .OrderBy(x => x.first).ToList();
        var diffs = new List<object>();
        for (int i = 1; i < groups.Count; i++)
        {
            var a = Db.GetSetup(groups[i - 1].hash); var b = Db.GetSetup(groups[i].hash);
            if (a == null || b == null) continue;
            var d = SetupOptimiser.Diff(YNode.Parse(a.Value.yaml)["CarSetup"], YNode.Parse(b.Value.yaml)["CarSetup"]);
            diffs.Add(new { from = groups[i - 1].hash, to = groups[i].hash, changes = d.Select(x => new { x.key, x.a, x.b }) });
        }
        return new { setups = groups, diffs };
    }

    /// <summary>Lap telemetry for charts: downsampled channels on a shared distance grid + deltas + corner comparisons.</summary>
    object Compare(string lapIds, int step)
    {
        var ids = lapIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).Take(6).ToList();
        var rows = ids.Select(id => Db.GetLap(id)).Where(r => r != null).Select(r => r!).ToList();
        if (rows.Count == 0) return new { error = "no laps" };
        var model = _hub.Store.GetTrackModel(rows[0].TrackKey!);
        if (model == null) return new { error = "No track model for this track yet — complete a clean lap first." };
        step = Math.Clamp(step, 1, 10);

        var laps = new List<(LapRow row, DistLap dist, LapAnalysis an, float[] x, float[] y)>();
        foreach (var r in rows)
        {
            var data = Db.GetLapData(r.Id);
            var dl = DistLap.TryCreate(data, model.Length, r.LapTime, !r.OutLap);
            if (dl == null) continue;
            var (x, y) = LineAlign.ToModel(dl, model, r.HasGps);
            laps.Add((r, dl, LapAnalyzer.Analyze(dl, model), x, y));
        }
        if (laps.Count == 0) return new { error = "no lap data" };
        var baseLap = laps[0];
        int n = baseLap.dist.N;
        float[] Down(float[] a, float scale = 1) { var res = new float[(n + step - 1) / step]; for (int i = 0, k = 0; i < n; i += step, k++) res[k] = a[i] * scale; return res; }

        return new
        {
            step,
            length = model.Length,
            model = new { model.Corners, model.SectorStarts, x = model.X, y = model.Y, model.Step, model.FromGps },
            laps = laps.Select((l, idx) => new
            {
                id = l.row.Id,
                lapTime = l.row.LapTime,
                lapNumber = l.row.LapNumber,
                sessionId = l.row.SessionId,
                driver = l.row.DriverName,
                valid = l.row.Valid,
                startedAt = l.row.StartedAt,
                sectors = l.row.SectorTimes,
                time = Down(l.dist.Time),
                speed = Down(l.dist.Speed, 3.6f),
                throttle = Down(l.dist.Throttle, 100),
                brake = Down(l.dist.Brake, 100),
                steer = Down(l.dist.Steer, (float)(180 / Math.PI)),
                gear = Down(l.dist.Gear),
                rpm = Down(l.dist.Rpm),
                latG = Down(l.dist.LatG, 1 / 9.81f),
                lonG = Down(l.dist.LonG, 1 / 9.81f),
                abs = Down(l.dist.Abs),
                x = Down(l.x),
                y = Down(l.y),
                delta = idx == 0 ? null : Down(LapComparer.DeltaTrace(l.dist, baseLap.dist)),
                analysis = l.an,
                comparison = idx == 0 ? null : LapComparer.Compare(l.dist, l.an, baseLap.dist, baseLap.an).Corners,
            }),
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_web != null) await _web.DisposeAsync();
    }
}
