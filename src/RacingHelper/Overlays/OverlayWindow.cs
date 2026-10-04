using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace RacingHelper.App.Overlays;

/// <summary>Borderless, transparent, always-on-top window hosting one overlay. Click-through unless in edit mode.</summary>
public sealed class OverlayWindow : Window
{
    const int GWL_EXSTYLE = -20;
    const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
    static readonly IntPtr HWND_TOPMOST = new(-1);
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    public OverlayView View { get; }
    readonly ScaleTransform _scale = new(1, 1);
    IntPtr _hwnd;
    bool _clickThrough = true;

    public event Action<OverlayWindow>? Moved;
    public event Action<OverlayWindow, double>? ScaleChanged;

    public OverlayWindow(OverlayView view)
    {
        View = view;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = "Racing Helper overlay";
        view.Width = view.BaseWidth;
        view.Height = view.BaseHeight;
        view.LayoutTransform = _scale;
        Content = view;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        RenderOptions.SetEdgeMode(this, EdgeMode.Unspecified);

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            ApplyStyles();
        };
        MouseLeftButtonDown += (_, e) =>
        {
            if (_clickThrough) return;
            try { DragMove(); } catch { }
            Moved?.Invoke(this);
        };
        MouseWheel += (_, e) =>
        {
            if (_clickThrough) return;
            double s = Math.Clamp(_scale.ScaleX + (e.Delta > 0 ? 0.05 : -0.05), 0.5, 3);
            SetScale(s);
            ScaleChanged?.Invoke(this, s);
        };
    }

    public double Scale => _scale.ScaleX;

    public void SetScale(double s)
    {
        _scale.ScaleX = s;
        _scale.ScaleY = s;
    }

    public void SetClickThrough(bool on)
    {
        _clickThrough = on;
        Cursor = on ? null : Cursors.SizeAll;
        ApplyStyles();
    }

    void ApplyStyles()
    {
        if (_hwnd == IntPtr.Zero) return;
        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED;
        ex = _clickThrough ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex);
    }

    /// <summary>Some full-screen apps push themselves above other topmost windows; re-assert periodically.</summary>
    public void BringToTop()
    {
        if (_hwnd != IntPtr.Zero) SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }
}
