using System.Drawing;
using System.Runtime.InteropServices;

namespace Pulse;

/// <summary>
/// Draws the system-tray icons: either the static ↓/↑ arrows, or the live
/// speed numbers (NetSpeedMonitor style, download on top / upload below).
/// </summary>
public static class TrayIconRenderer
{
    private static readonly Color DownColor = Color.FromArgb(129, 199, 132); // green
    private static readonly Color UpColor = Color.FromArgb(186, 104, 200); // purple-magenta
    private static readonly SolidBrush DownBrush = new(DownColor);
    private static readonly SolidBrush UpBrush = new(UpColor);

    // Only three font sizes ever occur (by string length); cache instead of
    // allocating a Font per icon redraw (this runs up to 4x/sec).
    private static readonly Font FontLen1To2 = new("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel);
    private static readonly Font FontLen3 = new("Segoe UI", 12.5f, FontStyle.Bold, GraphicsUnit.Pixel);
    private static readonly Font FontLen4Plus = new("Segoe UI", 10.5f, FontStyle.Bold, GraphicsUnit.Pixel);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>Static up/down arrows icon.</summary>
    public static Icon CreateArrowsIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var downPen = new Pen(DownColor, 3f);
            using var upPen = new Pen(UpColor, 3f);

            // Down arrow (left)
            g.DrawLine(downPen, 9, 5, 9, 24);
            g.DrawLine(downPen, 9, 24, 4, 18);
            g.DrawLine(downPen, 9, 24, 14, 18);

            // Up arrow (right)
            g.DrawLine(upPen, 23, 27, 23, 8);
            g.DrawLine(upPen, 23, 8, 18, 14);
            g.DrawLine(upPen, 23, 8, 28, 14);
        }
        return ToIcon(bmp);
    }

    /// <summary>Live speed numbers: download value on top, upload below.</summary>
    public static Icon CreateSpeedIcon(double downBps, double upBps, SpeedUnit unit, bool hasNetwork)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            g.Clear(Color.Transparent);

            string down = hasNetwork ? SpeedMonitor.CompactValue(downBps, unit) : "—";
            string up = hasNetwork ? SpeedMonitor.CompactValue(upBps, unit) : "—";

            DrawCentered(g, down, DownBrush, 0);
            DrawCentered(g, up, UpBrush, 16);
        }
        return ToIcon(bmp);
    }

    private static void DrawCentered(Graphics g, string text, SolidBrush brush, int top)
    {
        Font font = text.Length <= 2 ? FontLen1To2 : text.Length == 3 ? FontLen3 : FontLen4Plus;
        var size = g.MeasureString(text, font);
        g.DrawString(text, font, brush, (32 - size.Width) / 2f, top + (16 - size.Height) / 2f);
    }

    private static Icon ToIcon(Bitmap bmp)
    {
        IntPtr hIcon = bmp.GetHicon();
        // Clone into a managed Icon so the GDI handle can be destroyed safely.
        using var tmp = Icon.FromHandle(hIcon);
        var icon = (Icon)tmp.Clone();
        DestroyIcon(hIcon);
        return icon;
    }
}
