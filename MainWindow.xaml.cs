using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Media = System.Windows.Media;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace Pulse;

public partial class MainWindow : Window
{
    private readonly Settings _settings;
    private readonly SpeedMonitor _monitor = new();
    private readonly SystemMonitor _sysMonitor = new();
    private readonly UsageTracker _usage = new();
    private readonly DispatcherTimer _timer = new();

    // Experimental subsystems.
    private readonly OutageMonitor _outage;
    private readonly HistoryRecorder _history = new();
    private readonly AppTrafficMonitor _perApp = new();
    private readonly GeigerPlayer _geiger = new();
    private bool _embedded;
    private DateTime _lastTopTake = DateTime.UtcNow;

    // Traffic particles.
    private sealed class Particle
    {
        public required System.Windows.Shapes.Ellipse Shape;
        public double Y;
        public double VelocityY;
    }
    private readonly DispatcherTimer _particleTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly List<Particle> _particles = new();
    private readonly Random _rng = new();
    private double _particleCreditDown, _particleCreditUp;

    // Only two particle colors ever occur; share one frozen brush each
    // instead of allocating a new SolidColorBrush per spawn (up to ~44/sec).
    private static readonly Media.SolidColorBrush ParticleDownBrush = FrozenBrush(0x90, 0x81, 0xC7, 0x84);
    private static readonly Media.SolidColorBrush ParticleUpBrush = FrozenBrush(0x90, 0x4F, 0xC3, 0xF7);

    private static Media.SolidColorBrush FrozenBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new Media.SolidColorBrush(Media.Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private WinForms.NotifyIcon? _trayIcon;
    private Drawing.Icon? _trayIconImage;
    private bool _trayIconIsStatic;

    // Speed history for the sparkline (fixed 60 samples, shifted left).
    private const int HistoryLength = 60;
    private readonly double[] _downHistory = new double[HistoryLength];
    private readonly double[] _upHistory = new double[HistoryLength];
    // System-stat histories (percent 0-100) share the network cursor pair so
    // every sparkline advances in lockstep.
    private readonly double[] _cpuHistory = new double[HistoryLength];
    private readonly double[] _ramHistory = new double[HistoryLength];
    private readonly double[] _gpuHistory = new double[HistoryLength];
    private int _historyHead;  // ring-buffer write cursor
    private int _historyCount; // samples held so far, caps at HistoryLength

    private long? _pingMs;
    private bool _pingInFlight;

    private bool _userHidden;          // hidden via tray/hotkey (vs fullscreen auto-hide)
    private bool _hiddenByFullscreen;
    private bool _hotkeyRegistered;

    // Win32 interop.
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_APPWINDOW = 0x00040000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WM_HOTKEY = 0x0312;
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_MINIMIZE = 0xF020;
    private const int HOTKEY_ID = 1;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint VK_S = 0x53;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // Pin-to-desktop: keep the window glued to the bottom of the z-order.
    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private static readonly IntPtr HWND_BOTTOM = new(1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter,
        int x, int y, int cx, int cy, uint flags);

    private const uint GW_HWNDLAST = 1;
    private const uint GW_HWNDPREV = 3;

    [DllImport("user32.dll")]
    private static extern IntPtr GetTopWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder sb, int max);

    private bool _allowZOrderChange;

    /// <summary>
    /// Walks the z-order from the bottom up and returns the lowest window
    /// that is not part of the desktop itself (Progman/WorkerW). Inserting
    /// the widget below that window pins it directly above the wallpaper —
    /// plain HWND_BOTTOM would put it UNDER the wallpaper window, invisible.
    /// </summary>
    private IntPtr FindLowestNonDesktopWindow(IntPtr own)
    {
        IntPtr w = GetWindow(GetTopWindow(IntPtr.Zero), GW_HWNDLAST);
        var sb = new System.Text.StringBuilder(64);
        int guard = 0;
        while (w != IntPtr.Zero && guard++ < 500)
        {
            if (w != own)
            {
                sb.Clear();
                GetClassName(w, sb, 64);
                string cls = sb.ToString();
                if (cls != "Progman" && cls != "WorkerW")
                    return w;
            }
            w = GetWindow(w, GW_HWNDPREV);
        }
        return IntPtr.Zero;
    }

    public MainWindow()
    {
        InitializeComponent();
        _settings = Settings.Load();
        _monitor.AdapterFilterId = _settings.AdapterId;

        _outage = new OutageMonitor(() => _settings.PingHost);
        _outage.Recovered += duration =>
            _trayIcon?.ShowBalloonTip(4000, "Internet restored",
                $"Connection was down for {OutageMonitor.FormatDuration(duration)}.",
                WinForms.ToolTipIcon.Info);

        _timer.Tick += (_, _) => OnTick();
        _particleTimer.Tick += (_, _) => ParticleTick();

        // Win+D (show desktop) minimizes every window, including tool windows.
        // A widget should survive it: restore immediately without activating.
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
                if (_embedded)
                    ApplyEmbedded(); // re-assert bottom z-order
            }
        };
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnClosing;
        MouseLeftButtonDown += OnMouseLeftButtonDown;

        BuildContextMenu();
    }

    /// <summary>Called by App after construction.</summary>
    public void InitializeFromSettings()
    {
        // Upgrade a legacy Run-key autostart entry (which Windows ignores for
        // elevated apps) to a working logon Scheduled Task.
        StartupManager.MigrateIfNeeded();

        ApplySettings();
        SetupTray();

        // Start sampling immediately (independent of visibility so the tray
        // tooltip stays live even when the widget is hidden).
        _monitor.Sample();
        RestartTimer();

        // Always show on launch. Hiding is per-session only — persisting a
        // hidden state made the widget seem to vanish after a reboot.
        ShowWidget();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Apply tool-window extended style so we don't appear in Alt-Tab.
        var helper = new WindowInteropHelper(this);
        int exStyle = GetWindowLong(helper.Handle, GWL_EXSTYLE);
        exStyle |= WS_EX_TOOLWINDOW;
        exStyle &= ~WS_EX_APPWINDOW;
        SetWindowLong(helper.Handle, GWL_EXSTYLE, exStyle);

        HwndSource.FromHwnd(helper.Handle)?.AddHook(WndProc);
        ApplyClickThrough();
        ApplyHotkey();
        ApplyEmbedded();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            if (_userHidden) ShowWidget(); else HideWidget();
            handled = true;
        }
        else if (msg == WM_SYSCOMMAND && (wParam.ToInt32() & 0xFFF0) == SC_MINIMIZE)
        {
            // Swallow minimize requests (Win+D "show desktop" and the like) —
            // the StateChanged handler catches whatever bypasses this message.
            handled = true;
        }
        else if (msg == WM_WINDOWPOSCHANGING && _embedded && !_allowZOrderChange)
        {
            // Pinned to desktop: freeze the z-order so clicks can't raise the
            // widget above other windows (its position above the wallpaper is
            // set once in ApplyEmbedded).
            var wp = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            wp.flags |= SWP_NOZORDER;
            Marshal.StructureToPtr(wp, lParam, false);
        }
        return IntPtr.Zero;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // Position after layout so ActualWidth/Height are known.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ApplyPosition));
    }

    // ---------------------------------------------------------------- Timer

    private void RestartTimer()
    {
        _timer.Stop();
        _timer.Interval = TimeSpan.FromSeconds(Math.Max(0.25, _settings.RefreshIntervalSeconds));
        _timer.Start();
    }

    private void OnTick()
    {
        _monitor.Sample();
        // Only sample system counters when the AIO panel is on, so the
        // default network-only widget never touches PerformanceCounter.
        if (_settings.ShowSystemStats)
            _sysMonitor.Sample();
        _usage.Add(_monitor.LastDeltaBytes);

        string down;
        string up;
        if (!_monitor.HasNetwork)
        {
            down = "—";
            up = "—";
        }
        else
        {
            down = SpeedMonitor.Format(_monitor.DownloadBytesPerSec, _settings.Unit);
            up = SpeedMonitor.Format(_monitor.UploadBytesPerSec, _settings.Unit);
        }

        DownText.Text = down;
        UpText.Text = up;
        CDownText.Text = down;
        CUpText.Text = up;

        _history.Record(_monitor.DownloadBytesPerSec, _monitor.UploadBytesPerSec, _pingMs);
        _geiger.SetRate(_monitor.DownloadBytesPerSec + _monitor.UploadBytesPerSec);

        UpdateInfoLine();
        UpdateSystemStats(); // writes text + raw history samples, before the cursor advances
        UpdateGraph();       // writes down/up history and advances the shared ring cursor
        UpdateSystemGraphs();// redraws CPU/RAM/GPU sparklines from the now-advanced cursor
        UpdateTopApp();
        UpdateTray(down, up);
        UpdatePing();
        UpdateFullscreenHide();
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max);

    // ------------------------------------------------------------ Ping/info

    private async void UpdatePing()
    {
        if (!_settings.ShowPing || _pingInFlight)
            return;

        _pingInFlight = true;
        _pingMs = await NetworkPing.PingAsync(_settings.PingHost, 1000);
        _pingInFlight = false;
    }

    private void UpdateInfoLine()
    {
        var pingVis = _settings.ShowPing ? Visibility.Visible : Visibility.Collapsed;
        PingPanel.Visibility = pingVis;
        CPingPanel.Visibility = pingVis;
        if (_settings.ShowPing)
        {
            string ping = _pingMs.HasValue ? $"{_pingMs} ms" : "— ms";
            PingText.Text = ping;
            CPingText.Text = ping;
        }

        var parts = new List<string>();
        if (_settings.ShowUsage)
            parts.Add($"{UsageTracker.FormatBytes(_usage.TodayBytes)} today");
        if (_settings.OutageLogging && _outage.IsDown)
            parts.Add("⚠ offline");

        if (parts.Count == 0)
        {
            InfoText.Visibility = Visibility.Collapsed;
            return;
        }
        InfoText.Visibility = Visibility.Visible;
        InfoText.Text = string.Join("  ·  ", parts);
    }

    // ---------------------------------------------------------------- Graph

    private void UpdateGraph()
    {
        _downHistory[_historyHead] = _monitor.DownloadBytesPerSec;
        _upHistory[_historyHead] = _monitor.UploadBytesPerSec;
        _historyHead = (_historyHead + 1) % HistoryLength;
        if (_historyCount < HistoryLength)
            _historyCount++;

        if (!_settings.ShowGraph)
            return;

        double w = GraphCanvas.Width;
        double h = GraphCanvas.Height;

        // Floor of 10 KB/s so idle noise doesn't fill the whole graph.
        double max = 10 * 1024;
        for (int i = 0; i < HistoryLength; i++)
        {
            if (_downHistory[i] > max) max = _downHistory[i];
            if (_upHistory[i] > max) max = _upHistory[i];
        }

        DownLine.Points = BuildPoints(_downHistory, max, w, h);
        UpLine.Points = BuildPoints(_upHistory, max, w, h);
    }

    private Media.PointCollection BuildPoints(double[] data, double max, double w, double h)
    {
        // Oldest slot: index 0 while still filling, else the ring's write
        // cursor (about to be overwritten, so it holds the oldest sample).
        int oldest = _historyCount < HistoryLength ? 0 : _historyHead;

        var pts = new Media.PointCollection();
        for (int k = 0; k < _historyCount; k++)
        {
            // Same temporal-position formula the old shifting-array version
            // used, so newest is always pinned to the right edge (x = w).
            int i = HistoryLength - _historyCount + k;
            int idx = (oldest + k) % HistoryLength;
            double x = (double)i / (HistoryLength - 1) * w;
            double y = h - 1 - Math.Min(1.0, data[idx] / max) * (h - 2);
            pts.Add(new System.Windows.Point(x, y));
        }
        return pts;
    }

    // ---------------------------------------------------------- AIO monitor

    private void UpdateSystemStats()
    {
        if (!_settings.ShowSystemStats)
        {
            SysPanel.Visibility = Visibility.Collapsed;
            return;
        }
        SysPanel.Visibility = Visibility.Visible;

        CpuPanel.Visibility = _settings.ShowCpu ? Visibility.Visible : Visibility.Collapsed;
        RamPanel.Visibility = _settings.ShowRam ? Visibility.Visible : Visibility.Collapsed;
        GpuPanel.Visibility = _settings.ShowGpu ? Visibility.Visible : Visibility.Collapsed;
        DiskPanel.Visibility = _settings.ShowDisk ? Visibility.Visible : Visibility.Collapsed;

        double ramPct = _sysMonitor.RamTotalBytes > 0
            ? 100.0 * _sysMonitor.RamUsedBytes / _sysMonitor.RamTotalBytes
            : 0;
        double gpuPct = _sysMonitor.GpuAvailable ? _sysMonitor.GpuPercent : 0;

        if (_settings.ShowCpu)
        {
            CpuText.Text = $"{_sysMonitor.CpuPercent:0}%";
            CpuTopText.Text = _sysMonitor.TopCpuProcessName.Length > 0
                ? $"{Truncate(_sysMonitor.TopCpuProcessName, 12)} {_sysMonitor.TopCpuProcessPercent:0}%"
                : "—";
        }

        if (_settings.ShowRam)
        {
            RamText.Text = $"{UsageTracker.FormatBytes(_sysMonitor.RamUsedBytes)} / " +
                            $"{UsageTracker.FormatBytes(_sysMonitor.RamTotalBytes)}";
            RamTopText.Text = _sysMonitor.TopRamProcessName.Length > 0
                ? $"{Truncate(_sysMonitor.TopRamProcessName, 12)} {UsageTracker.FormatBytes(_sysMonitor.TopRamProcessBytes)}"
                : "—";
        }

        if (_settings.ShowGpu)
            GpuText.Text = _sysMonitor.GpuAvailable ? $"{gpuPct:0}%" : "—";

        if (_settings.ShowDisk)
        {
            DiskReadText.Text = $"Read  {UsageTracker.FormatBytes((long)_sysMonitor.DiskReadBytesPerSec)}/s";
            DiskWriteText.Text = $"Write {UsageTracker.FormatBytes((long)_sysMonitor.DiskWriteBytesPerSec)}/s";
            DiskSpaceText.Text = string.Join("\n", _sysMonitor.DriveSpaces.Select(d =>
                $"{d.Letter}  {UsageTracker.FormatBytes(d.UsedBytes)} / {UsageTracker.FormatBytes(d.TotalBytes)}"));
        }

        // Record raw samples at the not-yet-advanced cursor, same slot
        // UpdateGraph is about to write the network samples into.
        _cpuHistory[_historyHead] = _sysMonitor.CpuPercent;
        _ramHistory[_historyHead] = ramPct;
        _gpuHistory[_historyHead] = gpuPct;
    }

    private void UpdateSystemGraphs()
    {
        if (!_settings.ShowSystemStats || !_settings.ShowSystemGraphs)
            return;

        if (_settings.ShowCpu)
            CpuLine.Points = BuildPoints(_cpuHistory, 100, CpuGraph.Width, CpuGraph.Height);
        if (_settings.ShowRam)
            RamLine.Points = BuildPoints(_ramHistory, 100, RamGraph.Width, RamGraph.Height);
        if (_settings.ShowGpu)
            GpuLine.Points = BuildPoints(_gpuHistory, 100, GpuGraph.Width, GpuGraph.Height);
    }

    /// <summary>
    /// Rebuilds SysPanel's row/column structure and repositions the four
    /// metric boxes: a 2x2 grid by default, or one box per row when
    /// SysSingleColumn is on. Cheap and only runs on settings changes, so
    /// it's simplest to fully rebuild rather than patch the existing grid.
    /// </summary>
    private void ApplySysPanelLayout()
    {
        SysPanel.RowDefinitions.Clear();
        SysPanel.ColumnDefinitions.Clear();
        var boxes = new[] { CpuPanel, RamPanel, GpuPanel, DiskPanel };

        if (_settings.SysSingleColumn)
        {
            SysPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (int i = 0; i < boxes.Length; i++)
            {
                if (i > 0)
                    SysPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
                SysPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(boxes[i], SysPanel.RowDefinitions.Count - 1);
                Grid.SetColumn(boxes[i], 0);
            }
        }
        else
        {
            // Same SharedSizeGroup on both columns forces every box to the
            // same width: whichever box's content is widest, not just
            // star-proportional space.
            SysPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "SysCol" });
            SysPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            SysPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "SysCol" });
            SysPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            SysPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
            SysPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            (int Row, int Col)[] positions = { (0, 0), (0, 2), (2, 0), (2, 2) };
            for (int i = 0; i < boxes.Length; i++)
            {
                Grid.SetRow(boxes[i], positions[i].Row);
                Grid.SetColumn(boxes[i], positions[i].Col);
            }
        }
    }

    // ------------------------------------------------- Experimental features

    private void UpdateTopApp()
    {
        if (!_settings.PerAppStats)
        {
            TopAppText.Visibility = Visibility.Collapsed;
            return;
        }

        TopAppText.Visibility = Visibility.Visible;
        if (!_perApp.IsRunning)
        {
            TopAppText.Text = AppTrafficMonitor.IsElevated
                ? "per-app: unavailable"
                : "per-app: run as administrator";
            return;
        }

        var now = DateTime.UtcNow;
        double elapsed = (now - _lastTopTake).TotalSeconds;
        if (elapsed < 0.9)
            return;
        _lastTopTake = now;

        var top = _perApp.TakeTop();
        if (top == null || top.Value.Bytes < 2048)
        {
            TopAppText.Text = "top: —";
            return;
        }
        TopAppText.Text =
            $"{top.Value.Name}  {SpeedMonitor.Format(top.Value.Bytes / elapsed, _settings.Unit)}";
    }

    private void ParticleTick()
    {
        const double dt = 0.033;
        double w = ParticleCanvas.ActualWidth;
        double h = ParticleCanvas.ActualHeight;
        if (w <= 0 || h <= 0 || !IsVisible)
            return;

        static double SpawnRate(double bps) =>
            bps < 300 ? 0 : Math.Min(22, 3.0 * Math.Log10(bps / 100.0));

        _particleCreditDown += SpawnRate(_monitor.DownloadBytesPerSec) * dt;
        _particleCreditUp += SpawnRate(_monitor.UploadBytesPerSec) * dt;

        while (_particleCreditDown >= 1 && _particles.Count < 70)
        {
            _particleCreditDown -= 1;
            SpawnParticle(down: true, w, h);
        }
        while (_particleCreditUp >= 1 && _particles.Count < 70)
        {
            _particleCreditUp -= 1;
            SpawnParticle(down: false, w, h);
        }

        for (int i = _particles.Count - 1; i >= 0; i--)
        {
            var p = _particles[i];
            p.Y += p.VelocityY * dt;
            System.Windows.Controls.Canvas.SetTop(p.Shape, p.Y);
            if (p.Y < -6 || p.Y > h + 6)
            {
                ParticleCanvas.Children.Remove(p.Shape);
                _particles.RemoveAt(i);
            }
        }
    }

    private void SpawnParticle(bool down, double w, double h)
    {
        double size = 2 + _rng.NextDouble() * 2;
        var shape = new System.Windows.Shapes.Ellipse
        {
            Width = size,
            Height = size,
            Fill = down ? ParticleDownBrush : ParticleUpBrush
        };

        double y = down ? -4 : h + 4;
        double vy = (28 + _rng.NextDouble() * 45) * (down ? 1 : -1);

        System.Windows.Controls.Canvas.SetLeft(shape, _rng.NextDouble() * Math.Max(1, w - size));
        System.Windows.Controls.Canvas.SetTop(shape, y);
        ParticleCanvas.Children.Add(shape);
        _particles.Add(new Particle { Shape = shape, Y = y, VelocityY = vy });
    }

    private void ClearParticles()
    {
        foreach (var p in _particles)
            ParticleCanvas.Children.Remove(p.Shape);
        _particles.Clear();
        _particleCreditDown = _particleCreditUp = 0;
    }

    private void ApplyEmbedded()
    {
        _embedded = _settings.DesktopEmbedded;
        ApplyTopmost();

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;

        if (_embedded)
        {
            // Pin just above the desktop/wallpaper windows (NOT absolute
            // bottom — that hides the widget behind the wallpaper).
            IntPtr above = FindLowestNonDesktopWindow(handle);
            _allowZOrderChange = true;
            SetWindowPos(handle, above != IntPtr.Zero ? above : HWND_BOTTOM,
                0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            _allowZOrderChange = false;
        }
    }

    private void ApplyTopmost() => Topmost = !_embedded && _settings.AlwaysOnTop;

    /// <summary>Longest string SpeedMonitor.Format can plausibly produce for a unit.</summary>
    private static string WidestSpeedSample(SpeedUnit unit) => unit switch
    {
        SpeedUnit.MBps => "999.9 MB/s",
        SpeedUnit.Auto => "999.99 Gbps",
        _ => "9999 Mbps",
    };

    // ------------------------------------------------------------- Settings

    private void ApplySettings()
    {
        // Opacity only fades the card's background fill (applied in
        // ApplyTheme), not the window itself — text/arrows/numbers must stay
        // fully readable even when the background is slid down to 0%.
        ApplyTopmost();

        bool compact = _settings.CompactLayout;
        StackedPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CompactPanel.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;

        double baseSize = _settings.FontSize * _settings.ScaleFactor;
        DownText.FontSize = baseSize;
        UpText.FontSize = baseSize;
        CDownText.FontSize = baseSize;
        CUpText.FontSize = baseSize;
        // Ping header/number match the download/upload numbers exactly.
        PingText.FontSize = baseSize;
        CPingText.FontSize = baseSize;
        PingHeader.FontSize = baseSize;
        CPingHeader.FontSize = baseSize;

        // Arrows are filled Paths; scale them with the font.
        double arrowW = baseSize * 0.95;
        double arrowH = baseSize;
        DownArrow.Width = UpArrow.Width = CDownArrow.Width = CUpArrow.Width = arrowW;
        DownArrow.Height = UpArrow.Height = CDownArrow.Height = CUpArrow.Height = arrowH;
        // All secondary text matches the main speed numbers — one font size
        // for the whole widget, per user preference.
        InfoText.FontSize = baseSize;
        TopAppText.FontSize = baseSize;

        // Reserve enough width for the longest string this unit can produce
        // (both fonts are monospace) so the widget doesn't resize every time
        // a digit is added or dropped as the speed value changes.
        double numberWidth = WidestSpeedSample(_settings.Unit).Length * baseSize * 0.62 + 2;
        DownText.MinWidth = UpText.MinWidth = CDownText.MinWidth = CUpText.MinWidth = numberWidth;

        ApplyTheme();

        double pad = 7 * _settings.ScaleFactor;
        RootBorder.Padding = new Thickness(pad + 3, pad, pad + 3, pad);
        RootBorder.CornerRadius = new CornerRadius(10 * _settings.ScaleFactor);

        GraphCanvas.Width = 130 * _settings.ScaleFactor;
        GraphCanvas.Height = 26 * _settings.ScaleFactor;
        GraphCanvas.Visibility = _settings.ShowGraph ? Visibility.Visible : Visibility.Collapsed;
        PingPanel.Visibility = _settings.ShowPing ? Visibility.Visible : Visibility.Collapsed;
        CPingPanel.Visibility = PingPanel.Visibility;
        InfoText.Visibility = _settings.ShowUsage ? Visibility.Visible : Visibility.Collapsed;

        // AIO system monitor. The panel's own visibility (and its per-metric
        // sub-visibility) is refreshed every tick in UpdateSystemStats, this
        // only handles sizing/theming/backend.
        _sysMonitor.Backend = _settings.GpuBackend;
        SysPanel.Visibility = _settings.ShowSystemStats ? Visibility.Visible : Visibility.Collapsed;
        ApplySysPanelLayout();
        foreach (var tb in new[]
                 {
                     CpuText, CpuTopText, RamText, RamTopText, GpuText,
                     DiskReadText, DiskWriteText, DiskSpaceText,
                     CpuHeader, RamHeader, GpuHeader, DiskHeader
                 })
            tb.FontSize = baseSize;

        // Reserve width for the longest plausible "Write 999.9 MB/s" string
        // so the Disk box doesn't resize every tick as the digit count
        // fluctuates with read/write throughput.
        double diskRateWidth = "Write 999.9 MB/s".Length * baseSize * 0.62 + 2;
        DiskReadText.MinWidth = DiskWriteText.MinWidth = diskRateWidth;

        double graphW = 56 * _settings.ScaleFactor;
        double graphH = 16 * _settings.ScaleFactor;
        bool showGraphs = _settings.ShowSystemStats && _settings.ShowSystemGraphs;
        foreach (var c in new[] { CpuGraph, RamGraph, GpuGraph })
        {
            c.Width = graphW;
            c.Height = graphH;
            c.Visibility = showGraphs ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_monitor.AdapterFilterId != _settings.AdapterId)
        {
            _monitor.AdapterFilterId = _settings.AdapterId;
            _monitor.Reset();
        }

        // Experimental subsystems.
        _outage.Enabled = _settings.OutageLogging;
        bool geigerWasEnabled = _geiger.Enabled;
        _geiger.Enabled = _settings.GeigerMode;
        if (_settings.GeigerMode && !geigerWasEnabled)
            _geiger.ClickOnce(); // audible feedback that the mode is on
        if (_settings.PerAppStats)
            _perApp.Start();
        else
            _perApp.Stop();
        TopAppText.Visibility = _settings.PerAppStats ? Visibility.Visible : Visibility.Collapsed;
        if (_settings.TrafficParticles)
        {
            _particleTimer.Start();
        }
        else
        {
            _particleTimer.Stop();
            ClearParticles();
        }
        ApplyEmbedded();

        ApplyClickThrough();
        ApplyHotkey();
        UpdateMenuChecks();

        RestartTimer();
        // Re-apply position after size may have changed.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ApplyPosition));
    }

    private void ApplyTheme()
    {
        // Speed numbers/arrows keep their fixed green/blue and ping its
        // magenta; themes only change the card background and the info line.
        byte bgAlpha, r, g, b;
        Media.Brush info;
        switch (_settings.Theme)
        {
            case WidgetTheme.Light:
                (bgAlpha, r, g, b) = (0xE6, 0xF5, 0xF5, 0xF5);
                info = new Media.SolidColorBrush(Media.Color.FromRgb(0x55, 0x55, 0x55));
                break;
            case WidgetTheme.Black:
                (bgAlpha, r, g, b) = (0xF2, 0x00, 0x00, 0x00);
                info = new Media.SolidColorBrush(Media.Color.FromRgb(0x99, 0x99, 0x99));
                break;
            default: // Dark
                (bgAlpha, r, g, b) = (0xCC, 0x1E, 0x1E, 0x1E);
                info = new Media.SolidColorBrush(Media.Color.FromRgb(0xAA, 0xAA, 0xAA));
                break;
        }

        // Scale the background's own alpha by the opacity setting so text
        // stays fully opaque while the card fill fades to fully transparent.
        byte scaledAlpha = (byte)Math.Round(bgAlpha * Math.Clamp(_settings.Opacity, 0.0, 1.0));
        RootBorder.Background = new Media.SolidColorBrush(Media.Color.FromArgb(scaledAlpha, r, g, b));
        InfoText.Foreground = info;
    }

    private void ApplyClickThrough()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;

        int exStyle = GetWindowLong(handle, GWL_EXSTYLE);
        if (_settings.ClickThrough)
            exStyle |= WS_EX_TRANSPARENT;
        else
            exStyle &= ~WS_EX_TRANSPARENT;
        SetWindowLong(handle, GWL_EXSTYLE, exStyle);
    }

    private void ApplyHotkey()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;

        if (_settings.HotkeyEnabled && !_hotkeyRegistered)
        {
            _hotkeyRegistered = RegisterHotKey(handle, HOTKEY_ID, MOD_CONTROL | MOD_ALT, VK_S);
        }
        else if (!_settings.HotkeyEnabled && _hotkeyRegistered)
        {
            UnregisterHotKey(handle, HOTKEY_ID);
            _hotkeyRegistered = false;
        }
    }

    // ------------------------------------------------------------- Position

    private void ApplyPosition()
    {
        if (_settings.DockCorner != DockCorner.None)
        {
            DockTo(_settings.DockCorner);
            return;
        }

        if (!double.IsNaN(_settings.WindowLeft) && !double.IsNaN(_settings.WindowTop))
        {
            Left = _settings.WindowLeft;
            Top = _settings.WindowTop;
            EnsureOnScreen();
        }
        else
        {
            DockTo(DockCorner.BottomRight); // sensible first-run default
        }
    }

    private void DockTo(DockCorner corner)
    {
        var wa = SystemParameters.WorkArea; // DIPs, excludes taskbar
        const double margin = 8;

        Left = corner is DockCorner.TopLeft or DockCorner.BottomLeft
            ? wa.Left + margin
            : wa.Right - ActualWidth - margin;
        Top = corner is DockCorner.TopLeft or DockCorner.TopRight
            ? wa.Top + margin
            : wa.Bottom - ActualHeight - margin;
    }

    private void EnsureOnScreen()
    {
        var wa = SystemParameters.WorkArea;
        if (Left + ActualWidth > wa.Right) Left = wa.Right - ActualWidth;
        if (Top + ActualHeight > wa.Bottom) Top = wa.Bottom - ActualHeight;
        if (Left < wa.Left) Left = wa.Left;
        if (Top < wa.Top) Top = wa.Top;
    }

    // ------------------------------------------------------------- Dragging

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }

            // Dragging cancels docking and stores the new position.
            _settings.DockCorner = DockCorner.None;
            _settings.WindowLeft = Left;
            _settings.WindowTop = Top;
            _settings.Save();
            UpdateMenuChecks();
        }
    }

    // --------------------------------------------------------- Context menus

    private static (DockCorner Corner, string Label)[] DockOptions => DockCornerOptions.All;

    private MenuItem? _wpfDockMenu;
    private MenuItem? _wpfTopItem;
    private readonly Dictionary<DockCorner, WinForms.ToolStripMenuItem> _trayDockItems = new();
    private WinForms.ToolStripMenuItem? _topMenuItem;
    private WinForms.ToolStripMenuItem? _clickThroughMenuItem;
    private WinForms.ToolStripMenuItem? _startupMenuItem;

    private void BuildContextMenu()
    {
        var menu = new ContextMenu();

        var showHide = new MenuItem { Header = "Hide Widget" };
        showHide.Click += (_, _) =>
        {
            if (_userHidden) ShowWidget(); else HideWidget();
        };
        menu.Opened += (_, _) => showHide.Header = _userHidden ? "Show Widget" : "Hide Widget";

        var settingsItem = new MenuItem { Header = "Settings…" };
        settingsItem.Click += (_, _) => OpenSettings();

        var speedTestItem = new MenuItem { Header = "Speed Test…" };
        speedTestItem.Click += (_, _) => OpenSpeedTest();

        var historyItem = new MenuItem { Header = "History (24 h)…" };
        historyItem.Click += (_, _) => OpenHistory();

        _wpfDockMenu = new MenuItem { Header = "Dock to Corner" };
        foreach (var (corner, label) in DockOptions)
        {
            var item = new MenuItem { Header = label, IsCheckable = true, Tag = corner };
            item.Click += (_, _) => SetDock(corner);
            _wpfDockMenu.Items.Add(item);
        }

        _wpfTopItem = new MenuItem { Header = "Always on Top", IsCheckable = true };
        _wpfTopItem.IsChecked = _settings.AlwaysOnTop;
        _wpfTopItem.Click += (_, _) => ToggleAlwaysOnTop(_wpfTopItem.IsChecked);

        var startupItem = new MenuItem { Header = "Start with Windows", IsCheckable = true };
        startupItem.IsChecked = StartupManager.IsEnabled();
        startupItem.Click += (_, _) => ToggleStartup(startupItem.IsChecked);
        menu.Opened += (_, _) =>
        {
            startupItem.IsChecked = StartupManager.IsEnabled();
            UpdateMenuChecks();
        };

        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => ExitApp();

        menu.Items.Add(showHide);
        menu.Items.Add(settingsItem);
        menu.Items.Add(speedTestItem);
        menu.Items.Add(historyItem);
        menu.Items.Add(_wpfDockMenu);
        menu.Items.Add(_wpfTopItem);
        menu.Items.Add(startupItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);

        RootBorder.ContextMenu = menu;
    }

    // ------------------------------------------------------------- Tray icon

    private void SetupTray()
    {
        _trayIconImage = TrayIconRenderer.CreateArrowsIcon();
        _trayIconIsStatic = true;
        _trayIcon = new WinForms.NotifyIcon
        {
            Icon = _trayIconImage,
            Visible = true,
            Text = "Pulse"
        };

        var menu = new WinForms.ContextMenuStrip();

        var showHide = new WinForms.ToolStripMenuItem("Show/Hide Widget");
        showHide.Click += (_, _) => { if (_userHidden) ShowWidget(); else HideWidget(); };

        var settingsItem = new WinForms.ToolStripMenuItem("Settings…");
        settingsItem.Click += (_, _) => OpenSettings();

        var speedTestItem = new WinForms.ToolStripMenuItem("Speed Test…");
        speedTestItem.Click += (_, _) => OpenSpeedTest();

        var historyItem = new WinForms.ToolStripMenuItem("History (24 h)…");
        historyItem.Click += (_, _) => OpenHistory();

        var dockMenu = new WinForms.ToolStripMenuItem("Dock to Corner");
        foreach (var (corner, label) in DockOptions)
        {
            var item = new WinForms.ToolStripMenuItem(label) { Checked = corner == _settings.DockCorner };
            item.Click += (_, _) => SetDock(corner);
            dockMenu.DropDownItems.Add(item);
            _trayDockItems[corner] = item;
        }

        _topMenuItem = new WinForms.ToolStripMenuItem("Always on Top")
        {
            CheckOnClick = true,
            Checked = _settings.AlwaysOnTop
        };
        _topMenuItem.Click += (_, _) => ToggleAlwaysOnTop(_topMenuItem!.Checked);

        _clickThroughMenuItem = new WinForms.ToolStripMenuItem("Click-Through (ignore mouse)")
        {
            CheckOnClick = true,
            Checked = _settings.ClickThrough
        };
        _clickThroughMenuItem.Click += (_, _) =>
        {
            _settings.ClickThrough = _clickThroughMenuItem!.Checked;
            _settings.Save();
            ApplyClickThrough();
        };

        _startupMenuItem = new WinForms.ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = StartupManager.IsEnabled()
        };
        _startupMenuItem.Click += (_, _) => ToggleStartup(_startupMenuItem!.Checked);

        var exitItem = new WinForms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitApp();

        menu.Items.Add(showHide);
        menu.Items.Add(settingsItem);
        menu.Items.Add(speedTestItem);
        menu.Items.Add(historyItem);
        menu.Items.Add(dockMenu);
        menu.Items.Add(_topMenuItem);
        menu.Items.Add(_clickThroughMenuItem);
        menu.Items.Add(_startupMenuItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => { if (_userHidden) ShowWidget(); else HideWidget(); };
    }

    private void UpdateTray(string down, string up)
    {
        if (_trayIcon == null)
            return;

        string tip = $"↓ {down}  ↑ {up}";
        if (_settings.ShowUsage)
            tip += $" | {UsageTracker.FormatBytes(_usage.TodayBytes)} today";
        _trayIcon.Text = Truncate(tip, 63);

        if (_settings.TrayNumbers)
        {
            SetTrayIcon(TrayIconRenderer.CreateSpeedIcon(
                _monitor.DownloadBytesPerSec, _monitor.UploadBytesPerSec,
                _settings.Unit, _monitor.HasNetwork), isStatic: false);
        }
        else if (!_trayIconIsStatic)
        {
            SetTrayIcon(TrayIconRenderer.CreateArrowsIcon(), isStatic: true);
        }
    }

    private void SetTrayIcon(Drawing.Icon icon, bool isStatic)
    {
        var old = _trayIconImage;
        _trayIconImage = icon;
        _trayIconIsStatic = isStatic;
        if (_trayIcon != null)
            _trayIcon.Icon = icon;
        old?.Dispose();
    }

    private void UpdateMenuChecks()
    {
        foreach (var (corner, item) in _trayDockItems)
            item.Checked = corner == _settings.DockCorner;
        if (_wpfDockMenu != null)
        {
            foreach (var obj in _wpfDockMenu.Items)
            {
                if (obj is MenuItem mi && mi.Tag is DockCorner c)
                    mi.IsChecked = c == _settings.DockCorner;
            }
        }
        if (_topMenuItem != null) _topMenuItem.Checked = _settings.AlwaysOnTop;
        if (_wpfTopItem != null) _wpfTopItem.IsChecked = _settings.AlwaysOnTop;
        if (_clickThroughMenuItem != null) _clickThroughMenuItem.Checked = _settings.ClickThrough;
    }

    // ------------------------------------------------------------- Actions

    private void ShowWidget()
    {
        _userHidden = false;
        _hiddenByFullscreen = false;
        Show();
        Visibility = Visibility.Visible;
        ApplyTopmost();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ApplyPosition));
    }

    private void HideWidget()
    {
        _userHidden = true;
        Hide();
    }

    private void UpdateFullscreenHide()
    {
        if (_userHidden || _embedded)
            return;

        if (!_settings.HideWhenFullscreen)
        {
            if (_hiddenByFullscreen)
            {
                _hiddenByFullscreen = false;
                Visibility = Visibility.Visible;
            }
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;

        if (_hiddenByFullscreen)
        {
            // Come back as soon as our spot is no longer covered — e.g. after
            // Win+D shows the desktop while a game still owns the foreground.
            if (!FullscreenDetector.IsSpotStillCovered(handle))
            {
                _hiddenByFullscreen = false;
                Visibility = Visibility.Visible;
            }
            return;
        }

        if (FullscreenDetector.IsForegroundFullscreen(handle))
        {
            _hiddenByFullscreen = true;
            Visibility = Visibility.Hidden;
        }
    }

    private void SetDock(DockCorner corner)
    {
        _settings.DockCorner = corner;
        _settings.Save();
        UpdateMenuChecks();
        if (corner != DockCorner.None)
            ApplyPosition();
    }

    private void ToggleAlwaysOnTop(bool enabled)
    {
        _settings.AlwaysOnTop = enabled;
        _settings.Save();
        ApplyTopmost();
        UpdateMenuChecks();
    }

    private void ToggleStartup(bool enabled)
    {
        StartupManager.SetEnabled(enabled);
        if (_startupMenuItem != null)
            _startupMenuItem.Checked = StartupManager.IsEnabled();
    }

    private SettingsWindow? _settingsWindow;

    private void OpenSettings()
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_settings, _usage);
        _settingsWindow.SettingsChanged += () =>
        {
            _settings.Save();
            ApplySettings();
        };
        _settingsWindow.SpeedTestRequested += OpenSpeedTest;
        _settingsWindow.HistoryRequested += OpenHistory;
        _settingsWindow.ElevateRequested += RestartElevated;
        _settingsWindow.Closed += (_, _) =>
        {
            _settings.Save();
            _settingsWindow = null;
        };
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private SpeedTestWindow? _speedTestWindow;

    private void OpenSpeedTest()
    {
        if (_speedTestWindow != null)
        {
            _speedTestWindow.Activate();
            return;
        }
        _speedTestWindow = new SpeedTestWindow();
        _speedTestWindow.Closed += (_, _) => _speedTestWindow = null;
        _speedTestWindow.Show();
        _speedTestWindow.Activate();
    }

    private HistoryWindow? _historyWindow;

    private void OpenHistory()
    {
        if (_historyWindow != null)
        {
            _historyWindow.Activate();
            return;
        }
        _historyWindow = new HistoryWindow(_history, _usage, _outage);
        _historyWindow.Closed += (_, _) => _historyWindow = null;
        _historyWindow.Show();
        _historyWindow.Activate();
    }

    /// <summary>
    /// Relaunches the app elevated (one UAC prompt) so ETW per-app stats can
    /// run, then exits this instance. No-ops if the user declines the prompt.
    /// </summary>
    private void RestartElevated()
    {
        if (AppTrafficMonitor.IsElevated)
            return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                UseShellExecute = true,
                Verb = "runas"
            });
        }
        catch
        {
            return; // UAC declined or launch failed — keep running as-is
        }
        ExitApp();
    }

    private void ExitApp()
    {
        _timer.Stop();
        _particleTimer.Stop();
        _settings.Save();
        _usage.Save();
        _history.Save();
        _outage.Dispose();
        _perApp.Dispose();
        _sysMonitor.Dispose();
        _geiger.Enabled = false;
        _embedded = false;

        if (_hotkeyRegistered)
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
                UnregisterHotKey(handle, HOTKEY_ID);
            _hotkeyRegistered = false;
        }

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        _trayIconImage?.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // The window's own close (e.g. Alt+F4) hides to tray instead of exiting.
        // Real exit goes through ExitApp which calls Application.Shutdown.
        if (System.Windows.Application.Current.ShutdownMode == ShutdownMode.OnExplicitShutdown
            && _trayIcon != null && _trayIcon.Visible)
        {
            e.Cancel = true;
            HideWidget();
        }
    }
}
