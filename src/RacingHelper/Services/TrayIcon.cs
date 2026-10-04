using Forms = System.Windows.Forms;

namespace RacingHelper.App.Services;

/// <summary>Notification-area icon: the app keeps running (recording, overlays, voice) when the dashboard is closed.</summary>
public sealed class TrayIcon : IDisposable
{
    readonly Forms.NotifyIcon _icon;
    bool _tipShown;

    public TrayIcon(App app)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open dashboard", null, (_, _) => app.ShowDashboard());
        menu.Items.Add("Open dashboard in browser", null, (_, _) => app.OpenInBrowser());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Move / resize overlays  (Ctrl+Shift+F9)", null, (_, _) => app.Overlays.EditMode = !app.Overlays.EditMode);
        menu.Items.Add("Hide / show overlays  (Ctrl+Shift+F10)", null, (_, _) => app.Overlays.Hidden = !app.Overlays.Hidden);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit Racing Helper", null, (_, _) => app.Quit());

        _icon = new Forms.NotifyIcon
        {
            Icon = AppIcon.Icon,
            Text = "Racing Helper — personal race engineer",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => app.ShowDashboard();
        app.Exit += (_, _) => _icon.Visible = false;

        // remind once that closing the window doesn't stop the engineer
        app.Dispatcher.InvokeAsync(() =>
        {
            foreach (System.Windows.Window w in app.Windows)
                w.Closing += (_, _) =>
                {
                    if (_tipShown) return;
                    _tipShown = true;
                    _icon.ShowBalloonTip(4000, "Racing Helper is still running", "Recording, overlays and voice keep working. Use the tray icon to open the dashboard or exit.", Forms.ToolTipIcon.Info);
                };
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
