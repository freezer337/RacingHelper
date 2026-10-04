using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace RacingHelper.App;

/// <summary>App icon drawn in code (a speed-chevron on a rounded square) so there are no binary assets to ship.</summary>
public static class AppIcon
{
    static Drawing.Icon? _icon;
    static ImageSource? _image;

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    public static Drawing.Icon Icon => _icon ??= Create();

    public static ImageSource ImageSource => _image ??= Imaging.CreateBitmapSourceFromHIcon(Icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

    static Drawing.Icon Create()
    {
        using var bmp = new Drawing.Bitmap(64, 64);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            using var bg = new Drawing.Drawing2D.GraphicsPath();
            int r = 14;
            bg.AddArc(2, 2, r * 2, r * 2, 180, 90); bg.AddArc(62 - r * 2, 2, r * 2, r * 2, 270, 90);
            bg.AddArc(62 - r * 2, 62 - r * 2, r * 2, r * 2, 0, 90); bg.AddArc(2, 62 - r * 2, r * 2, r * 2, 90, 90);
            bg.CloseFigure();
            using var fill = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 14, 18, 24));
            g.FillPath(fill, bg);
            using var pen = new Drawing.Pen(Drawing.Color.FromArgb(255, 34, 211, 126), 7) { StartCap = Drawing.Drawing2D.LineCap.Round, EndCap = Drawing.Drawing2D.LineCap.Round, LineJoin = Drawing.Drawing2D.LineJoin.Round };
            g.DrawLines(pen, new[] { new Drawing.Point(16, 18), new Drawing.Point(30, 32), new Drawing.Point(16, 46) });
            g.DrawLines(pen, new[] { new Drawing.Point(32, 18), new Drawing.Point(46, 32), new Drawing.Point(32, 46) });
        }
        var h = bmp.GetHicon();
        var icon = (Drawing.Icon)Drawing.Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        return icon;
    }
}
