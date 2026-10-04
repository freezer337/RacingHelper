using RacingHelper.Web;

/// <summary>Stand-in for the desktop shell when serving the dashboard from the CLI.</summary>
sealed class HeadlessBridge : IAppBridge
{
    public IReadOnlyList<string> Voices() => new[] { "Test Voice" };
    public void Speak(string text) => Console.WriteLine("  (voice) " + text);
    public bool OverlayEditMode { get; set; }
    public void OverlaysChanged() => Console.WriteLine("  (overlays changed)");
    public void OpenFolder(string path) => Console.WriteLine("  (open) " + path);
    public IReadOnlyList<OverlayInfo> OverlayCatalog => RacingHelper.Web.OverlayCatalog.All;
}
