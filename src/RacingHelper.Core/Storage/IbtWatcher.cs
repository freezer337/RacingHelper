using System.Globalization;

namespace RacingHelper.Storage;

/// <summary>
/// Imports finished iRacing .ibt files in the background. Files still being written by the sim are skipped
/// until iRacing releases them. On first run only recent files are imported; older history can be imported
/// on demand from the dashboard.
/// </summary>
public sealed class IbtWatcher : IDisposable
{
    readonly IbtImporter _importer;
    readonly Database _db;
    readonly Func<AppSettings> _settings;
    readonly Timer _timer;
    readonly object _lock = new();
    bool _busy;
    CancellationTokenSource _cts = new();

    public event Action<ImportResult>? Imported;
    public string Status { get; private set; } = "idle";
    public int QueueLength { get; private set; }

    public IbtWatcher(IbtImporter importer, Database db, Func<AppSettings> settings)
    {
        _importer = importer;
        _db = db;
        _settings = settings;
        if (_db.GetKv("autoimport_since") == null)
            _db.SetKv("autoimport_since", DateTime.Now.AddDays(-3).ToString("o", CultureInfo.InvariantCulture));
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20));
    }

    DateTime Since => DateTime.TryParse(_db.GetKv("autoimport_since"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : DateTime.Now.AddDays(-3);

    public void Poke() => ThreadPool.QueueUserWorkItem(_ => Tick());

    void Tick()
    {
        if (!_settings().AutoImportIbt) return;
        var folder = _settings().TelemetryFolder;
        if (!Directory.Exists(folder)) return;
        var files = new DirectoryInfo(folder).GetFiles("*.ibt")
            .Where(f => f.LastWriteTime > Since && (DateTime.Now - f.LastWriteTime).TotalSeconds > 15 && f.Length > 200_000)
            .OrderBy(f => f.LastWriteTime)
            .Select(f => f.FullName)
            .ToList();
        ImportFiles(files, force: false);
    }

    /// <summary>Imports the given files sequentially on the calling thread (skips ones already imported unless forced).</summary>
    public List<ImportResult> ImportFiles(IReadOnlyList<string> files, bool force)
    {
        var results = new List<ImportResult>();
        lock (_lock)
        {
            if (_busy) return results;
            _busy = true;
        }
        try
        {
            var todo = files.Where(f => force || !_db.IsFileImported(f, new FileInfo(f).Length)).ToList();
            QueueLength = todo.Count;
            foreach (var f in todo)
            {
                if (_cts.IsCancellationRequested) break;
                if (IsBeingWritten(f)) { QueueLength--; continue; }
                Status = "importing " + Path.GetFileName(f);
                var r = _importer.Import(f, force, _cts.Token);
                results.Add(r);
                QueueLength--;
                if (!r.Skipped) Imported?.Invoke(r);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            Status = "idle";
            QueueLength = 0;
            lock (_lock) _busy = false;
        }
        return results;
    }

    public bool Busy { get { lock (_lock) return _busy; } }

    /// <summary>iRacing keeps the current .ibt open for writing; opening it without write-sharing fails until it's done.</summary>
    static bool IsBeingWritten(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return false;
        }
        catch (IOException) { return true; }
        catch { return false; }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _timer.Dispose();
    }
}
