using RacingHelper.Web;

/// <summary>Stand-in for the desktop shell when serving the dashboard from the CLI.</summary>
sealed class HeadlessBridge : IAppBridge
{
    public bool OverlayEditMode { get; set; }
    public void OverlaysChanged() => Console.WriteLine("  (overlays changed)");
    public void OpenFolder(string path) => Console.WriteLine("  (open) " + path);
    public IReadOnlyList<OverlayInfo> OverlayCatalog => RacingHelper.Web.OverlayCatalog.All;
    public async Task<ButtonPress?> LearnButton(int timeoutMs) { await Task.Delay(300); return new ButtonPress("VID_346E&PID_0006", "Test Wheel", 7); }
    public IReadOnlyList<string> Controllers() => new[] { "Test Wheel" };
    public IReadOnlyList<string> ApplyHotkeys() => Array.Empty<string>();
}
