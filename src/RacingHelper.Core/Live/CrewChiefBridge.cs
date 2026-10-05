using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;

namespace RacingHelper.Live;

/// <summary>
/// Lets CrewChief V4 be the voice of the engineer. CrewChief has an MQTT client (Properties → "MQTT Telemetry enabled")
/// that subscribes to "&lt;SubscribeTopic&gt;/&lt;driver name&gt;" and speaks every message posted there through its own
/// radio queue (TTS, so it never talks over the spotter). We run the broker on 127.0.0.1 and point CrewChief at it by
/// writing Documents\CrewChiefV4\mqtt_telemetry.json.
/// Payload (see CrewChiefV4 Events/Mqtt.cs): {"message", "priority" 0–10, "immediate", "distance", "max_distance"}.
/// </summary>
public sealed class CrewChiefBridge : IDisposable
{
    public const string SubscribeTopic = "/coach";
    public const string TelemetryTopic = "crewchief";
    const string Login = "racinghelper";

    // categories CrewChief already announces on its own (flags, fuel, lap times, PBs, incidents, damage, engine)
    static readonly HashSet<string> Duplicates = new() { "flag", "fuel", "lap", "pb", "warning" };

    readonly Func<AppSettings> _settings;
    MqttServer? _server;
    readonly ConcurrentDictionary<string, string> _subscribers = new();   // client id → response topic
    DateTime _lastTelemetry = DateTime.MinValue;
    int _port;

    public string Status { get; private set; } = "off";      // off / listening / connected / error
    public string Error { get; private set; } = "";
    public string DriverName => _subscribers.Values.Select(t => t[(SubscribeTopic.Length + 1)..]).FirstOrDefault() ?? "";
    public bool Connected => !_subscribers.IsEmpty;
    /// <summary>
    /// CrewChief only publishes telemetry while it's started and in a live session phase (practice/quali/race on track,
    /// formation, countdown…) — exactly when it will play our messages. In menus or with "Start Application" not pressed it
    /// silently drops them, so until then they wait here (see TryHandle).
    /// </summary>
    public bool ReceivingTelemetry => (DateTime.UtcNow - _lastTelemetry).TotalSeconds < 15;
    /// <summary>Connected and in a session, so a message sent now will be heard.</summary>
    public bool Live => Connected && ReceivingTelemetry;
    public int Port => _port;
    public int Sent { get; private set; }

    public event Action<string>? Log;

    // messages said while CrewChief can't speak (not started, menus): sent as soon as it's live, dropped after 2 minutes
    readonly List<(EngineerMessage m, DateTime at)> _held = new();
    readonly Timer _pump;

    public CrewChiefBridge(Func<AppSettings> settings)
    {
        _settings = settings;
        _pump = new Timer(_ => Pump(), null, 1000, 1000);
    }

    /// <summary>Messages waiting for CrewChief to be in a session.</summary>
    public int Held { get { lock (_held) return _held.Count; } }

    void Pump()
    {
        List<EngineerMessage> go;
        lock (_held)
        {
            _held.RemoveAll(x => (DateTime.UtcNow - x.at).TotalSeconds > 120);
            if (_held.Count == 0 || !Live) return;
            go = _held.Select(x => x.m).ToList();
            _held.Clear();
        }
        Speak(go);
    }

    public async Task StartAsync()
    {
        var s = _settings();
        if (!s.CrewChiefEnabled) { Status = "off"; return; }
        _port = s.CrewChiefPort;
        try
        {
            var factory = new MqttFactory();
            var options = factory.CreateServerOptionsBuilder()
                .WithDefaultEndpoint()
                .WithDefaultEndpointBoundIPAddress(IPAddress.Loopback)
                .WithDefaultEndpointBoundIPV6Address(IPAddress.None)
                .WithDefaultEndpointPort(_port)
                .Build();
            _server = factory.CreateMqttServer(options);
            _server.ValidatingConnectionAsync += e => { e.ReasonCode = MqttConnectReasonCode.Success; return Task.CompletedTask; };
            _server.InterceptingSubscriptionAsync += e =>
            {
                var topic = e.TopicFilter.Topic ?? "";
                if (topic.StartsWith(SubscribeTopic + "/", StringComparison.Ordinal) && !topic.Contains("/_debug/"))
                {
                    _subscribers[e.ClientId] = topic;
                    Status = "connected";
                    Log?.Invoke($"CrewChief connected (driver name \"{topic[(SubscribeTopic.Length + 1)..]}\").");
                }
                return Task.CompletedTask;
            };
            _server.InterceptingPublishAsync += e =>
            {
                // anything CrewChief publishes (its telemetry topic may have been renamed) — not our own /coach messages
                var t = e.ApplicationMessage.Topic ?? "";
                if (e.ClientId != "RacingHelper" && !t.StartsWith(SubscribeTopic, StringComparison.Ordinal)) _lastTelemetry = DateTime.UtcNow;
                return Task.CompletedTask;
            };
            _server.ClientDisconnectedAsync += e =>
            {
                if (_subscribers.TryRemove(e.ClientId, out _) && _subscribers.IsEmpty)
                {
                    Status = "listening";
                    Log?.Invoke("CrewChief disconnected.");
                }
                return Task.CompletedTask;
            };
            await _server.StartAsync();
            Status = "listening";
            Error = "";
        }
        catch (Exception ex)
        {
            Status = "error";
            Error = $"Couldn't open port {_port} for CrewChief ({(ex.InnerException ?? ex).Message}). Another MQTT broker may be running — pick a different port in Settings.";
            _server?.Dispose();
            _server = null;
        }
    }

    public async Task RestartAsync()
    {
        await StopAsync();
        await StartAsync();
    }

    async Task StopAsync()
    {
        var srv = _server;
        _server = null;
        _subscribers.Clear();
        if (srv != null)
        {
            try { await srv.StopAsync(); } catch { }
            srv.Dispose();
        }
        Status = "off";
    }

    /// <summary>CrewChief is the only voice: true whenever our broker is running.</summary>
    public bool UseCrewChief => _server != null;

    /// <summary>
    /// Routes a spoken engineer message to CrewChief. Returns false only when the broker isn't running.
    /// Things CrewChief announces itself are skipped; while CrewChief isn't in a session the message waits.
    /// </summary>
    public bool TryHandle(EngineerMessage m) => TryHandle(new[] { m });

    /// <summary>Several messages released together (one straight): sent back to back so none of them expires.</summary>
    public bool TryHandle(IReadOnlyList<EngineerMessage> ms)
    {
        if (_server == null) return false;
        var list = ms.Where(m => !(_settings().CrewChiefSkipDuplicates && Duplicates.Contains(m.Category))).ToList();
        if (list.Count == 0) return true;
        if (!Live)
        {
            // corner tips are only worth hearing right now
            lock (_held) _held.AddRange(list.Where(m => !float.IsFinite(m.ValidUntil)).Select(m => (m, DateTime.UtcNow)));
            return true;
        }
        Speak(list);
        return true;
    }

    /// <summary>
    /// Jim's recorded clip where he has one; the rest as text (CrewChief reads that with a TTS voice), joined into one
    /// message so it plays in one go. Everything goes to CrewChief's "immediate" queue: we already waited for a straight,
    /// and its normal queue throws messages away after 10 seconds when it's busy.
    /// </summary>
    void Speak(IReadOnlyList<EngineerMessage> ms)
    {
        bool jimOnly = _settings().CrewChiefJimOnly;
        var text = new List<string>();
        int prio = ms.Max(m => m.Priority);
        foreach (var m in ms)
        {
            if (float.IsFinite(m.LapDist) && float.IsFinite(m.ValidUntil))
            {
                // timed corner tip: CrewChief plays it only between here and the braking point
                if (!jimOnly) Send(m.Text, m.Priority, m.LapDist, m.ValidUntil);
                continue;
            }
            var (clips, rest) = CrewChiefPhrases.Map(m);
            foreach (var c in clips) SendRaw(c, m.Priority, immediate: true);
            if (!jimOnly && rest.Length > 0) text.Add(rest.TrimEnd('.', ' ') + ".");
        }
        if (text.Count > 0) Send(string.Join(" ", text), prio, immediate: true);
    }

    /// <summary>Radio check in Jim's own voice. False when CrewChief can't speak right now.</summary>
    public bool RadioCheck()
    {
        if (!Live) return false;
        SendRaw(CrewChiefPhrases.RadioCheck, 2, immediate: true);
        return true;
    }

    public void Send(string text, int priority, float lapDist = float.NaN, float validUntil = float.NaN, bool immediate = false)
        => SendRaw(ForSpeech(text), priority, lapDist, validUntil, immediate);

    void SendRaw(string message, int priority, float lapDist = float.NaN, float validUntil = float.NaN, bool immediate = false)
    {
        var srv = _server;
        if (srv == null || _subscribers.IsEmpty) return;
        var payload = new JsonObject
        {
            ["message"] = message,
            // CrewChief: 0 lowest, 5 default, 10 spotter. Higher = inserted nearer the head of its queue.
            ["priority"] = priority switch { <= 0 => 3, 1 => 5, 2 => 7, _ => 9 },
        };
        if (priority >= 3 || immediate) payload["immediate"] = true;
        if (float.IsFinite(lapDist) && float.IsFinite(validUntil) && validUntil > lapDist)
        {
            // only play it between here and the braking point; dropped if CrewChief is busy until then
            payload["distance"] = Math.Max(0, lapDist - 5);
            payload["max_distance"] = validUntil;
        }
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        foreach (var topic in _subscribers.Values.Distinct())
        {
            var msg = new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(bytes).Build();
            _ = srv.InjectApplicationMessage(new InjectedMqttApplicationMessage(msg) { SenderClientId = "RacingHelper" });
        }
        Sent++;
    }

    /// <summary>CrewChief reads our text with a TTS voice: spell out symbols it would stumble over.</summary>
    public static string ForSpeech(string text)
    {
        var s = text;
        s = Regex.Replace(s, @"(\d)\s*°C", "$1 degrees");
        s = Regex.Replace(s, @"(\d)\s*°", "$1 degrees");
        s = Regex.Replace(s, @"(^|[\s(])[-−](\d)", "$1minus $2");
        s = s.Replace("km/h", " kilometres an hour").Replace("→", " to ").Replace("–", " to ").Replace("…", ".");
        s = Regex.Replace(s, @"\s*—\s*", ", ");
        s = Regex.Replace(s, @"(\d)\s*kPa", "$1 K P A");
        s = Regex.Replace(s, @"(\d)\s*L\b", "$1 litres");
        s = Regex.Replace(s, @"(\d)\s*m\b", "$1 metres");
        s = Regex.Replace(s, @"(\d)\s*s\b", "$1 seconds");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length > 400 ? s[..400] : s;
    }

    // ------------------------------------------------------------------ CrewChief configuration

    public static string ConfigPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CrewChiefV4", "mqtt_telemetry.json");

    /// <summary>
    /// Points CrewChief's MQTT client at our broker. Keeps the existing file (and its channels) and backs it up once.
    /// CrewChief reads it when it starts, so it has to be restarted afterwards.
    /// </summary>
    public (bool ok, string message) ConfigureCrewChief()
    {
        try
        {
            var path = ConfigPath;
            var dir = Path.GetDirectoryName(path)!;
            if (!Directory.Exists(dir))
                return (false, $"CrewChief's folder ({dir}) wasn't found. Install CrewChief V4 and start it once, then try again.");
            JsonObject cfg;
            if (File.Exists(path))
            {
                var backup = path + ".before-racinghelper";
                if (!File.Exists(backup)) File.Copy(path, backup);
                cfg = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
            }
            else cfg = new JsonObject();

            cfg["Server"] = "127.0.0.1";
            cfg["Port"] = _settings().CrewChiefPort;
            cfg["Login"] = Login;
            cfg["Password"] = Login;
            cfg["UseTLS"] = false;
            cfg["Topic"] = TelemetryTopic;
            cfg["SubscribeTopic"] = SubscribeTopic;
            cfg["AllowSoundDownload"] = false;
            cfg["DebugResponse"] = false;
            // we read iRacing ourselves; CrewChief only needs to send a heartbeat (~1/s at 60 ticks/s)
            cfg["UpdateRateLimit"] = 59;
            if (cfg["Channels"] is not JsonArray ch || ch.Count == 0)
                cfg["Channels"] = new JsonArray(
                    new JsonObject { ["CrewChiefField"] = "PositionAndMotionData.DistanceRoundTrack", ["TelemetryField"] = "DistanceRoundTrack" },
                    new JsonObject { ["CrewChiefField"] = "SessionData.LapCount", ["TelemetryField"] = "CurrentLap" });
            File.WriteAllText(path, cfg.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return (true, $"CrewChief is set to use Racing Helper on 127.0.0.1:{_settings().CrewChiefPort}. Restart CrewChief to apply.");
        }
        catch (Exception ex)
        {
            return (false, "Couldn't write CrewChief's config: " + ex.Message);
        }
    }

    public object Describe()
    {
        bool configured = false;
        string? server = null; int port = 0;
        try
        {
            if (File.Exists(ConfigPath) && JsonNode.Parse(File.ReadAllText(ConfigPath)) is JsonObject cfg)
            {
                server = cfg["Server"]?.ToString();
                int.TryParse(cfg["Port"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out port);
                configured = (server == "127.0.0.1" || server == "localhost") && port == _settings().CrewChiefPort;
            }
        }
        catch { }
        return new
        {
            enabled = _settings().CrewChiefEnabled,
            status = Status,
            error = Error,
            port = _port,
            connected = Connected,
            driverName = DriverName,
            receivingTelemetry = ReceivingTelemetry,
            live = Live,
            held = Held,
            usingCrewChief = UseCrewChief,
            sent = Sent,
            configPath = ConfigPath,
            configFound = File.Exists(ConfigPath),
            configured,
            configServer = server,
            configPort = port,
        };
    }

    public void Dispose()
    {
        _pump.Dispose();
        Task.Run(StopAsync).Wait(2000);
    }
}
