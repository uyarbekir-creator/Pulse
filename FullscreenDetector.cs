using System.Runtime.InteropServices;
using System.Text;

namespace InternetSpeedWidget;

/// <summary>
/// Detects whether the foreground window is running fullscreen on the same
/// monitor as the widget (used to auto-hide during games/videos).
/// </summary>
public static class FullscreenDetector
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint GA_ROOT = 2;

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT pt);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);

    private static bool IsDesktopClass(string cls) =>
        cls is "Progman" or "WorkerW" or "SHELLDLL_DefView" or "SysListView32"
            or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";

    /// <summary>True when <paramref name="r"/> covers the whole of <paramref name="mi"/>'s monitor.</summary>
    private static bool CoversMonitor(RECT r, MONITORINFO mi) =>
        r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top &&
        r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;

    /// <summary>
    /// True when the foreground window covers the entire monitor that
    /// <paramref name="ownHwnd"/> (the widget) is on.
    /// </summary>
    public static bool IsForegroundFullscreen(IntPtr ownHwnd)
    {
        try
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == ownHwnd || fg == GetShellWindow())
                return false;

            // The desktop/taskbar report monitor-sized rects; ignore them.
            var sb = new StringBuilder(64);
            GetClassName(fg, sb, 64);
            if (IsDesktopClass(sb.ToString()))
                return false;

            IntPtr fgMonitor = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
            if (ownHwnd != IntPtr.Zero &&
                MonitorFromWindow(ownHwnd, MONITOR_DEFAULTTONEAREST) != fgMonitor)
                return false; // fullscreen app is on a different monitor

            if (!GetWindowRect(fg, out var r))
                return false;

            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(fgMonitor, ref mi))
                return false;

            return CoversMonitor(r, mi);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// While the widget is hidden: is its spot on screen still covered by a
    /// fullscreen window? Some games keep foreground ownership even after
    /// Win+D shows the desktop, so instead of trusting GetForegroundWindow
    /// we probe what actually occupies the widget's own position.
    /// </summary>
    public static bool IsSpotStillCovered(IntPtr ownHwnd)
    {
        try
        {
            if (ownHwnd == IntPtr.Zero || !GetWindowRect(ownHwnd, out var own))
                return false;

            var pt = new POINT { X = (own.Left + own.Right) / 2, Y = (own.Top + own.Bottom) / 2 };
            IntPtr at = WindowFromPoint(pt);
            if (at == IntPtr.Zero)
                return false;

            IntPtr root = GetAncestor(at, GA_ROOT);
            if (root == IntPtr.Zero)
                root = at;
            if (root == ownHwnd)
                return false;

            var sb = new StringBuilder(64);
            GetClassName(root, sb, 64);
            if (IsDesktopClass(sb.ToString()))
                return false; // the desktop is showing at our spot

            // Covered only if that window is fullscreen on its monitor.
            IntPtr monitor = MonitorFromWindow(root, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref mi))
                return false;
            if (!GetWindowRect(root, out var r))
                return false;

            return CoversMonitor(r, mi);
        }
        catch
        {
            return false;
        }
    }
}
