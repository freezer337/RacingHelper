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
    public bool ReceivingTelemetry => (DateTime.UtcNow - _lastTelemetry).TotalSeconds < 10;
    public int Port => _port;
    public int Sent { get; private set; }

    public event Action<string>? Log;

    public CrewChiefBridge(Func<AppSettings> settings) { _settings = settings; }

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
                if (e.ApplicationMessage.Topic?.StartsWith(TelemetryTopic + "/", StringComparison.Ordinal) == true) _lastTelemetry = DateTime.UtcNow;
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

    /// <summary>Should this message go to CrewChief instead of the Windows voice?</summary>
    public bool UseCrewChief => _settings().VoiceOutput switch
    {
        "windows" => false,
        "crewchief" => _server != null,
        _ => Connected,
    };

    /// <summary>
    /// Routes a spoken engineer message. Returns true when CrewChief took care of it (spoken, or deliberately left to
    /// CrewChief because it announces that itself); false → speak it with the Windows voice.
    /// </summary>
    public bool TryHandle(EngineerMessage m)
    {
        if (!UseCrewChief) return false;
        if (_settings().CrewChiefSkipDuplicates && Duplicates.Contains(m.Category)) return true;
        Send(m.Text, m.Priority, m.LapDist, m.ValidUntil);
        return true;
    }

    public void Send(string text, int priority, float lapDist = float.NaN, float validUntil = float.NaN)
    {
        var srv = _server;
        if (srv == null || _subscribers.IsEmpty) return;
        var payload = new JsonObject
        {
            ["message"] = ForSpeech(text),
            // CrewChief: 0 lowest, 5 default, 10 spotter. Higher = inserted nearer the head of its queue.
            ["priority"] = priority switch { <= 0 => 3, 1 => 5, 2 => 7, _ => 9 },
        };
        if (priority >= 3) payload["immediate"] = true;
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
        s = s.Replace("km/h", " K P H").Replace("→", " to ").Replace("–", " to ").Replace("…", ".");
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
            usingCrewChief = UseCrewChief,
            sent = Sent,
            configPath = ConfigPath,
            configFound = File.Exists(ConfigPath),
            configured,
            configServer = server,
            configPort = port,
        };
    }

    public void Dispose() => Task.Run(StopAsync).Wait(2000);
}
