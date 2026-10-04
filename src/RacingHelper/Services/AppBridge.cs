using System.Diagnostics;
using RacingHelper.Web;

namespace RacingHelper.App.Services;

/// <summary>Lets the web API reach desktop-only features. Calls arrive on Kestrel threads → marshal to the UI thread.</summary>
public sealed class AppBridge : IAppBridge
{
    readonly App _app;
    public AppBridge(App app) { _app = app; }

    public IReadOnlyList<string> Voices() => _app.Voice.Voices();

    public void Speak(string text) => _app.Voice.Say(text, 2);

    public bool OverlayEditMode
    {
        get => _app.Dispatcher.Invoke(() => _app.Overlays.EditMode);
        set => _app.Dispatcher.Invoke(() => _app.Overlays.EditMode = value);
    }

    public void OverlaysChanged() => _app.Dispatcher.InvokeAsync(() => _app.Overlays.Sync());

    public void OpenFolder(string path) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });

    public IReadOnlyList<OverlayInfo> OverlayCatalog => Web.OverlayCatalog.All;
}
