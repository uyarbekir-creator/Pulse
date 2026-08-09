using System.Windows;
using System.Windows.Controls;

namespace Pulse;

public partial class SettingsWindow : Window
{
    private readonly Settings _settings;
    private readonly UsageTracker _usage;
    private bool _loading;

    /// <summary>Raised whenever a setting changes so the widget can re-apply live.</summary>
    public event Action? SettingsChanged;

    /// <summary>Raised when the user clicks the Speed Test / History buttons.</summary>
    public event Action? SpeedTestRequested;
    public event Action? HistoryRequested;

    /// <summary>Raised when the user asks to relaunch the app elevated.</summary>
    public event Action? ElevateRequested;

    private record RefreshOption(string Label, double Seconds);
    private record AdapterOption(string Label, string Id);

    public SettingsWindow(Settings settings, UsageTracker usage)
    {
        InitializeComponent();
        _settings = settings;
        _usage = usage;
        PopulateAndBind();
    }

    private void PopulateAndBind()
    {
        _loading = true;

        // Font size
        FontSlider.Value = Math.Clamp(_settings.FontSize, FontSlider.Minimum, FontSlider.Maximum);
        FontValue.Text = $"{FontSlider.Value:0}";

        // Refresh intervals
        RefreshCombo.ItemsSource = new[]
        {
            new RefreshOption("0.5 seconds", 0.5),
            new RefreshOption("1 second", 1.0),
            new RefreshOption("2 seconds", 2.0),
            new RefreshOption("5 seconds", 5.0),
        };
        RefreshCombo.DisplayMemberPath = nameof(RefreshOption.Label);
        RefreshCombo.SelectedItem =
            ((IEnumerable<RefreshOption>)RefreshCombo.ItemsSource)
            .OrderBy(r => Math.Abs(r.Seconds - _settings.RefreshIntervalSeconds)).First();

        // Units
        UnitCombo.ItemsSource = new[]
        {
            "Mbps (megabits/sec)",
            "MB/s (megabytes/sec)",
            "Auto (bits, auto-scale)"
        };
        UnitCombo.SelectedIndex = _settings.Unit switch
        {
            SpeedUnit.MBps => 1,
            SpeedUnit.Auto => 2,
            _ => 0
        };

        // Scale
        ScaleCombo.ItemsSource = new[] { "Small", "Medium", "Large" };
        ScaleCombo.SelectedIndex = (int)_settings.Scale;

        // Adapter
        var adapters = new List<AdapterOption> { new("All adapters (automatic)", "") };
        adapters.AddRange(SpeedMonitor.GetAdapters()
            .Select(a => new AdapterOption($"{a.Name} — {a.Description}", a.Id)));
        AdapterCombo.ItemsSource = adapters;
        AdapterCombo.DisplayMemberPath = nameof(AdapterOption.Label);
        AdapterCombo.SelectedItem =
            adapters.FirstOrDefault(a => a.Id == _settings.AdapterId) ?? adapters[0];

        // Dock corner
        DockCombo.ItemsSource = DockCornerOptions.All.Select(o => o.Label).ToArray();
        DockCombo.SelectedIndex = (int)_settings.DockCorner;

        // GPU backend
        GpuBackendCombo.ItemsSource = GpuBackendOptions.All.Select(o => o.Label).ToArray();
        GpuBackendCombo.SelectedIndex = (int)_settings.GpuBackend;

        // Ping host + toggles
        PingHostBox.Text = _settings.PingHost;
        CompactCheck.IsChecked = _settings.CompactLayout;
        PingCheck.IsChecked = _settings.ShowPing;
        UsageCheck.IsChecked = _settings.ShowUsage;
        GraphCheck.IsChecked = _settings.ShowGraph;
        TopmostCheck.IsChecked = _settings.AlwaysOnTop;
        ClickThroughCheck.IsChecked = _settings.ClickThrough;
        TrayCheck.IsChecked = _settings.TrayNumbers;
        HotkeyCheck.IsChecked = _settings.HotkeyEnabled;
        FullscreenCheck.IsChecked = _settings.HideWhenFullscreen;
        StartupCheck.IsChecked = StartupManager.IsEnabled();

        // System monitor
        SysStatsCheck.IsChecked = _settings.ShowSystemStats;
        SysCpuCheck.IsChecked = _settings.ShowCpu;
        SysRamCheck.IsChecked = _settings.ShowRam;
        SysGpuCheck.IsChecked = _settings.ShowGpu;
        SysDiskCheck.IsChecked = _settings.ShowDisk;
        SysGraphsCheck.IsChecked = _settings.ShowSystemGraphs;
        SysSingleColumnCheck.IsChecked = _settings.SysSingleColumn;
        WeatherCheck.IsChecked = _settings.ShowWeather;
        WeatherFahrenheitCheck.IsChecked = _settings.WeatherFahrenheit;

        // Experimental
        EmbedCheck.IsChecked = _settings.DesktopEmbedded;
        ParticleCheck.IsChecked = _settings.TrafficParticles;
        GeigerCheck.IsChecked = _settings.GeigerMode;
        OutageCheck.IsChecked = _settings.OutageLogging;
        PerAppCheck.IsChecked = _settings.PerAppStats;

        UsageInfo.Text =
            $"Used today: {UsageTracker.FormatBytes(_usage.TodayBytes)} · " +
            $"this month: {UsageTracker.FormatBytes(_usage.MonthBytes)}";

        _loading = false;

        // Wire events
        OpacitySlider.Value = _settings.Opacity;
        OpacityValue.Text = $"{_settings.Opacity * 100:0}%";
        OpacitySlider.ValueChanged += OnOpacityChanged;

        FontSlider.ValueChanged += OnFontChanged;
        RefreshCombo.SelectionChanged += OnRefreshChanged;
        UnitCombo.SelectionChanged += OnUnitChanged;
        ScaleCombo.SelectionChanged += OnScaleChanged;
        AdapterCombo.SelectionChanged += OnAdapterChanged;
        DockCombo.SelectionChanged += OnDockChanged;
        GpuBackendCombo.SelectionChanged += OnGpuBackendChanged;
        PingHostBox.LostFocus += OnPingHostChanged;

        WireCheck(CompactCheck, v => _settings.CompactLayout = v);
        WireCheck(PingCheck, v => _settings.ShowPing = v);
        WireCheck(UsageCheck, v => _settings.ShowUsage = v);
        WireCheck(GraphCheck, v => _settings.ShowGraph = v);
        WireCheck(TopmostCheck, v => _settings.AlwaysOnTop = v);
        WireCheck(ClickThroughCheck, v => _settings.ClickThrough = v);
        WireCheck(TrayCheck, v => _settings.TrayNumbers = v);
        WireCheck(HotkeyCheck, v => _settings.HotkeyEnabled = v);
        WireCheck(FullscreenCheck, v => _settings.HideWhenFullscreen = v);

        WireCheck(SysStatsCheck, v => _settings.ShowSystemStats = v);
        WireCheck(SysCpuCheck, v => _settings.ShowCpu = v);
        WireCheck(SysRamCheck, v => _settings.ShowRam = v);
        WireCheck(SysGpuCheck, v => _settings.ShowGpu = v);
        WireCheck(SysDiskCheck, v => _settings.ShowDisk = v);
        WireCheck(SysGraphsCheck, v => _settings.ShowSystemGraphs = v);
        WireCheck(SysSingleColumnCheck, v => _settings.SysSingleColumn = v);
        WireCheck(WeatherCheck, v => _settings.ShowWeather = v);
        WireCheck(WeatherFahrenheitCheck, v => _settings.WeatherFahrenheit = v);

        WireCheck(EmbedCheck, v => _settings.DesktopEmbedded = v);
        WireCheck(ParticleCheck, v => _settings.TrafficParticles = v);
        WireCheck(GeigerCheck, v => _settings.GeigerMode = v);
        WireCheck(OutageCheck, v => _settings.OutageLogging = v);
        WireCheck(PerAppCheck, v => _settings.PerAppStats = v);

        StartupCheck.Checked += OnStartupChanged;
        StartupCheck.Unchecked += OnStartupChanged;
        SpeedTestButton.Click += (_, _) => SpeedTestRequested?.Invoke();
        HistoryButton.Click += (_, _) => HistoryRequested?.Invoke();
        ElevateButton.Visibility = AppTrafficMonitor.IsElevated
            ? Visibility.Collapsed : Visibility.Visible;
        ElevateButton.Click += (_, _) => ElevateRequested?.Invoke();
        CloseButton.Click += (_, _) => Close();
    }

    private void WireCheck(System.Windows.Controls.CheckBox box, Action<bool> apply)
    {
        RoutedEventHandler handler = (_, _) =>
        {
            if (_loading) return;
            apply(box.IsChecked == true);
            Notify();
        };
        box.Checked += handler;
        box.Unchecked += handler;
    }

    private void Notify()
    {
        if (!_loading)
            SettingsChanged?.Invoke();
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _settings.Opacity = OpacitySlider.Value;
        OpacityValue.Text = $"{_settings.Opacity * 100:0}%";
        Notify();
    }

    private void OnFontChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _settings.FontSize = Math.Round(FontSlider.Value);
        FontValue.Text = $"{_settings.FontSize:0}";
        Notify();
    }

    private void OnRefreshChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (RefreshCombo.SelectedItem is RefreshOption ro)
        {
            _settings.RefreshIntervalSeconds = ro.Seconds;
            Notify();
        }
    }

    private void OnUnitChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _settings.Unit = UnitCombo.SelectedIndex switch
        {
            1 => SpeedUnit.MBps,
            2 => SpeedUnit.Auto,
            _ => SpeedUnit.Mbps
        };
        Notify();
    }

    private void OnScaleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _settings.Scale = (WidgetScale)ScaleCombo.SelectedIndex;
        Notify();
    }

    private void OnAdapterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (AdapterCombo.SelectedItem is AdapterOption ao)
        {
            _settings.AdapterId = ao.Id;
            Notify();
        }
    }

    private void OnDockChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _settings.DockCorner = (DockCorner)DockCombo.SelectedIndex;
        Notify();
    }

    private void OnGpuBackendChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _settings.GpuBackend = GpuBackendOptions.All[GpuBackendCombo.SelectedIndex].Backend;
        Notify();
    }

    private void OnPingHostChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        string host = PingHostBox.Text.Trim();
        if (host.Length == 0)
        {
            host = "8.8.8.8";
            PingHostBox.Text = host;
        }
        if (host != _settings.PingHost)
        {
            _settings.PingHost = host;
            Notify();
        }
    }

    private void OnStartupChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        StartupManager.SetEnabled(StartupCheck.IsChecked == true);
    }
}
