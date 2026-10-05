using System.Diagnostics;
using System.IO;
using System.Windows;
using RacingHelper.Analysis;
using RacingHelper.App.Overlays;
using RacingHelper.App.Services;
using RacingHelper.Live;
using RacingHelper.Storage;
using RacingHelper.Web;

namespace RacingHelper.App;

public partial class App : Application
{
    Mutex? _single;
    public SettingsStore Settings { get; private set; } = null!;
    public TelemetryHub Hub { get; private set; } = null!;
    public WebServer Web { get; private set; } = null!;
    public OverlayManager Overlays { get; private set; } = null!;
    public WheelButtons? Wheel { get; private set; }
    IbtWatcher? _watcher;
    TrayIcon? _tray;
    Hotkeys? _hotkeys;
    MainWindow? _main;
    public static new App Current => (App)Application.Current;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length >= 4 && e.Args[0] == "--render-overlays")
        {
            try { await OverlaySnapshot.Run(e.Args[1], e.Args[2], e.Args[3], e.Args.Length > 4 ? double.Parse(e.Args[4], System.Globalization.CultureInfo.InvariantCulture) : 30); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(e.Args[1], "error.txt"), ex.ToString()); }
            Shutdown();
            return;
        }
        _single = new Mutex(true, "RacingHelper.SingleInstance", out bool first);
        Settings = new SettingsStore();
        if (!first)
        {
            // already running → just open the dashboard of the running instance
            Process.Start(new ProcessStartInfo($"http://127.0.0.1:{Settings.Current.WebPort}/") { UseShellExecute = true });
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            Log("UI error: " + ex.Exception);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log("Fatal: " + ex.ExceptionObject);

        try
        {
            var s = Settings.Current;
            Directory.CreateDirectory(s.DataFolder);
            Directory.CreateDirectory(s.SetupLibraryFolder);
            if (!File.Exists(Settings.FilePath)) Settings.Save();

            var db = new Database(Path.Combine(s.DataFolder, "racinghelper.db"));
            var store = new SessionStore(db);
            var analysis = new AnalysisService(store);
            Hub = new TelemetryHub(Settings, store, analysis);
            _watcher = new IbtWatcher(new IbtImporter(store), db, () => Settings.Current);
            _watcher.Imported += r =>
            {
                if (r.Error != null) Hub.Engineer.Info($"Import failed for {Path.GetFileName(r.File)}: {r.Error}");
                else if (r.Laps > 0) Hub.Engineer.Info($"Imported {Path.GetFileName(r.File)}: {r.Laps} laps ({r.ValidLaps} clean) — racing lines and tyre data added.");
            };

            // CrewChief is the only voice (Jim's recordings where he has them); messages wait while it's not in a session
            Hub.Radio.ReleasedBatch += ms =>
            {
                if (Settings.Current.VoiceEnabled) Hub.CrewChief.TryHandle(ms);
            };

            Hub.Start();

            var bridge = new AppBridge(this);
            Web = new WebServer(Hub, _watcher, bridge);
            await StartWebAsync();

            Overlays = new OverlayManager(Hub, Settings);
            Overlays.Start();

            ApplyHotkeys();

            try
            {
                Wheel = new WheelButtons();
                Wheel.Pressed += p =>
                {
                    var b = Settings.Current.ButtonBindings.FirstOrDefault(x => x.Device == p.Device && x.Button == p.Button);
                    if (b != null) RunAction(b.Action);
                };
            }
            catch (Exception ex) { Log("Wheel buttons unavailable: " + ex.Message); }

            _tray = new TrayIcon(this);
            ShowDashboard();
        }
        catch (Exception ex)
        {
            Log("Startup failed: " + ex);
            MessageBox.Show("Racing Helper failed to start:\n\n" + ex.Message, "Racing Helper", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    async Task StartWebAsync()
    {
        // fall back to the next free port if the configured one is taken
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                await Web.StartAsync();
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Settings.Current.WebPort++;
            }
            catch (Exception ex) when (attempt < 5 && ex.InnerException is IOException)
            {
                Settings.Current.WebPort++;
            }
        }
    }

    void AskAsync(string question) => Task.Run(() => Hub.Ask(question));

    /// <summary>Runs a bound action (keyboard shortcut or wheel button): app actions here, everything else is a question.</summary>
    public void RunAction(string id)
    {
        switch (id)
        {
            case "overlays-edit": Overlays.EditMode = !Overlays.EditMode; break;
            case "overlays-toggle": Overlays.Hidden = !Overlays.Hidden; break;
            case "reference": CycleReference(); break;
            case "dashboard": ShowDashboard(); break;
            default: AskAsync(id); break;
        }
    }

    /// <summary>(Re)registers the keyboard shortcuts from settings. Returns the ones that couldn't be registered.</summary>
    public IReadOnlyList<string> ApplyHotkeys()
    {
        _hotkeys?.Dispose();
        _hotkeys = new Hotkeys();
        var failed = new List<string>();
        int id = 1;
        foreach (var k in Settings.Current.EffectiveKeys().Where(k => !string.IsNullOrWhiteSpace(k.Keys)))
        {
            string action = k.Action;
            if (!Hotkeys.TryParse(k.Keys, out uint mods, out uint vk)) { failed.Add($"{k.Keys} (unknown key)"); continue; }
            if (!_hotkeys.Register(id++, mods, vk, () => RunAction(action))) failed.Add($"{k.Keys} (in use by another program)");
        }
        return failed;
    }

    void CycleReference()
    {
        string next = Settings.Current.ReferenceMode switch { "pb" => "session", "session" => "last", _ => "pb" };
        Hub.SetReference(next, 0);
        Hub.Engineer.Say(next switch { "pb" => "Reference: personal best.", "session" => "Reference: session best.", _ => "Reference: last lap." }, "info", 1);
    }

    public void ShowDashboard()
    {
        if (_main == null)
        {
            _main = new MainWindow(Web.Url);
            _main.Closed += (_, _) => _main = null;
        }
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    public void OpenInBrowser() => Process.Start(new ProcessStartInfo(Web.Url) { UseShellExecute = true });

    public async void Quit()
    {
        try
        {
            _hotkeys?.Dispose();
            Wheel?.Dispose();
            _tray?.Dispose();
            Overlays?.Dispose();
            _watcher?.Dispose();
            Hub?.Dispose();
            if (Web != null) await Web.DisposeAsync();
        }
        catch (Exception ex) { Log("Shutdown: " + ex.Message); }
        Shutdown();
    }

    public static void Log(string text)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RacingHelper");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "log.txt"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}");
        }
        catch { }
    }
}

