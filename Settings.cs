using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pulse;

public enum SpeedUnit
{
    Mbps,   // megabits per second
    MBps,   // megabytes per second
    Auto    // auto-scale (Kbps / Mbps / Gbps for bit units)
}

public enum WidgetScale
{
    Small,
    Medium,
    Large
}

public enum WidgetTheme
{
    Dark,
    Light,
    Black
}

public enum DockCorner
{
    None,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

public enum GpuBackend
{
    Generic,   // "GPU Engine" performance counters, any vendor
    Nvidia     // nvidia-smi.exe, NVIDIA only but more precise
}

/// <summary>Display labels for <see cref="GpuBackend"/>, shared like
/// <see cref="DockCornerOptions"/> so UI and enum can't drift apart.</summary>
public static class GpuBackendOptions
{
    public static readonly (GpuBackend Backend, string Label)[] All =
    {
        (GpuBackend.Generic, "Generic (Windows counters)"),
        (GpuBackend.Nvidia, "NVIDIA (nvidia-smi)"),
    };
}

/// <summary>Display labels for <see cref="DockCorner"/>, shared by the widget's
/// dock menus and the settings window's combo box so they can't drift apart.</summary>
public static class DockCornerOptions
{
    public static readonly (DockCorner Corner, string Label)[] All =
    {
        (DockCorner.None, "None (free floating)"),
        (DockCorner.TopLeft, "Top Left"),
        (DockCorner.TopRight, "Top Right"),
        (DockCorner.BottomLeft, "Bottom Left"),
        (DockCorner.BottomRight, "Bottom Right"),
    };
}

/// <summary>
/// Application settings, persisted as JSON in %APPDATA%\Pulse\settings.json.
/// </summary>
public class Settings
{
    // Appearance
    public double Opacity { get; set; } = 0.9;          // 0.0 - 1.0
    public double FontSize { get; set; } = 14;          // base font size in px
    public WidgetScale Scale { get; set; } = WidgetScale.Medium;
    public WidgetTheme Theme { get; set; } = WidgetTheme.Dark;
    public bool CompactLayout { get; set; } = false;    // one-line ↓/↑ layout

    // Behaviour
    public double RefreshIntervalSeconds { get; set; } = 1.0; // 0.5, 1, 2, 5
    public SpeedUnit Unit { get; set; } = SpeedUnit.Mbps;
    public bool AlwaysOnTop { get; set; } = true;
    public bool ClickThrough { get; set; } = false;     // widget ignores the mouse
    public bool HotkeyEnabled { get; set; } = true;     // Ctrl+Alt+S show/hide
    public bool HideWhenFullscreen { get; set; } = true;

    // Extra info shown on the widget
    public bool ShowPing { get; set; } = true;
    public string PingHost { get; set; } = "8.8.8.8";
    public bool ShowUsage { get; set; } = true;         // data used today
    public bool ShowGraph { get; set; } = true;         // speed history sparkline
    public bool TrayNumbers { get; set; } = true;       // draw speeds in tray icon

    // AIO system monitor (all opt-in; master toggle keeps the default
    // widget network-only).
    public bool ShowSystemStats { get; set; } = false;  // CPU/RAM/GPU/Disk panel
    public bool ShowCpu { get; set; } = true;
    public bool ShowRam { get; set; } = true;
    public bool ShowGpu { get; set; } = true;
    public bool ShowDisk { get; set; } = true;
    public GpuBackend GpuBackend { get; set; } = GpuBackend.Generic;
    public bool ShowSystemGraphs { get; set; } = false; // CPU/RAM/GPU sparklines
    public bool SysSingleColumn { get; set; } = false; // seed layout: one column vs 2x2

    // Weather frame (independent of the AIO master toggle — it isn't a
    // system stat). Location is resolved from the public IP.
    public bool ShowWeather { get; set; } = true;
    public bool WeatherFahrenheit { get; set; } = false;

    // Arrangement of the draggable frames: their ids ("Network", "Cpu", "Ram",
    // "Gpu", "Disk", "Weather") in the order they're laid out, left to right
    // and top to bottom. Frames occupy generated slots rather than free
    // coordinates, so this order is the entire layout state. Empty on a fresh
    // install — MainWindow fills in the default.
    public List<string> FrameOrder { get; set; } = new();

    // Legacy free-form positions from before frames became slot-based. Only
    // read once, to recover an existing arrangement as an order, then cleared.
    public Dictionary<string, double[]> FramePositions { get; set; } = new();

    // Which adapter to monitor. Empty = all physical adapters summed.
    public string AdapterId { get; set; } = "";

    // Experimental features (all opt-in).
    public bool DesktopEmbedded { get; set; } = false;   // glue widget to the wallpaper layer
    public bool TrafficParticles { get; set; } = false;  // animated traffic particles
    public bool OutageLogging { get; set; } = false;     // outage detection + log + alerts
    public bool PerAppStats { get; set; } = false;       // ETW per-app top talker (needs admin)
    public bool GeigerMode { get; set; } = false;        // traffic click sounds

    // Window position (screen coordinates). NaN means "not set yet".
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;

    // Corner the widget is docked to (recomputed on startup/apply).
    public DockCorner DockCorner { get; set; } = DockCorner.None;

    // Legacy (pre-corner-choice) flag, migrated to DockCorner in Load().
    // Must stay a normal serialized property (not JsonIgnore) so Load() can
    // still read it from old settings.json files; always saved back as false.
    public bool DockedToCorner { get; set; } = false;

    [JsonIgnore]
    public static string AppDataDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Pulse");

    // Pre-rename folder (the app was called Internet Speed Widget). Migrated
    // into AppDataDir on first Load() after the rename so settings, usage,
    // history, and logs all carry over instead of silently resetting.
    [JsonIgnore]
    private static string LegacyAppDataDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "InternetSpeedWidget");

    [JsonIgnore]
    public static string SettingsPath => Path.Combine(AppDataDir, "settings.json");

    private static void MigrateAppDataDirIfNeeded()
    {
        try
        {
            if (!Directory.Exists(LegacyAppDataDir))
                return;

            // A merely-existing AppDataDir isn't proof the new folder is
            // already in use: CrashLogger.Log() calls Directory.CreateDirectory
            // on this same path, and its handlers are wired up before
            // Settings.Load() ever runs (see App.xaml.cs OnStartup), so an
            // early startup exception can pre-create an empty Pulse folder
            // and silently defeat a plain existence check, stranding the
            // user's real settings/history under the old folder name.
            if (Directory.Exists(AppDataDir))
            {
                if (Directory.EnumerateFileSystemEntries(AppDataDir).Any())
                    return; // already has real data under the new name; don't clobber it
                Directory.Delete(AppDataDir);
            }

            Directory.Move(LegacyAppDataDir, AppDataDir);
        }
        catch
        {
            // Best-effort; if this fails the app just starts fresh under the new folder.
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        // WindowLeft/Top default to NaN ("not set"); allow named float literals
        // so serialization doesn't throw on NaN.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() }
    };

    public static Settings Load()
    {
        MigrateAppDataDirIfNeeded();
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                var s = JsonSerializer.Deserialize<Settings>(json, JsonOpts);
                if (s != null)
                {
                    // Migrate the old bool dock flag to the corner enum.
                    if (s.DockedToCorner && s.DockCorner == DockCorner.None)
                        s.DockCorner = DockCorner.BottomRight;
                    s.DockedToCorner = false;
                    return s;
                }
            }
        }
        catch
        {
            // Corrupt or unreadable settings -> fall back to defaults.
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            string json = JsonSerializer.Serialize(this, JsonOpts);
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort; ignore write failures (e.g. locked file).
        }
    }

    /// <summary>Font size multiplier derived from the widget scale.</summary>
    [JsonIgnore]
    public double ScaleFactor => Scale switch
    {
        WidgetScale.Small => 0.8,
        WidgetScale.Large => 1.35,
        _ => 1.0
    };
}
