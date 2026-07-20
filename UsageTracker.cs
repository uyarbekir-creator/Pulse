using System.IO;
using System.Text.Json;

namespace Pulse;

/// <summary>
/// Accumulates total bytes transferred today and this month, persisted to
/// %APPDATA%\Pulse\usage.json so totals survive restarts.
/// </summary>
public class UsageTracker
{
    private class UsageData
    {
        public string Day { get; set; } = "";
        public long DayBytes { get; set; }
        public string Month { get; set; } = "";
        public long MonthBytes { get; set; }
    }

    private UsageData _data = new();
    private DateTime _lastSave = DateTime.MinValue;

    public long TodayBytes => _data.DayBytes;
    public long MonthBytes => _data.MonthBytes;

    public static string UsagePath => Path.Combine(Settings.AppDataDir, "usage.json");

    public UsageTracker()
    {
        try
        {
            if (File.Exists(UsagePath))
                _data = JsonSerializer.Deserialize<UsageData>(File.ReadAllText(UsagePath)) ?? new UsageData();
        }
        catch
        {
            _data = new UsageData();
        }
        Rollover();
    }

    /// <summary>Adds bytes to the running totals; autosaves every ~30 seconds.</summary>
    public void Add(long bytes)
    {
        Rollover();
        if (bytes > 0)
        {
            _data.DayBytes += bytes;
            _data.MonthBytes += bytes;
        }
        if ((DateTime.UtcNow - _lastSave).TotalSeconds > 30)
            Save();
    }

    private void Rollover()
    {
        string day = DateTime.Now.ToString("yyyy-MM-dd");
        string month = DateTime.Now.ToString("yyyy-MM");
        if (_data.Day != day) { _data.Day = day; _data.DayBytes = 0; }
        if (_data.Month != month) { _data.Month = month; _data.MonthBytes = 0; }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Settings.AppDataDir);
            File.WriteAllText(UsagePath, JsonSerializer.Serialize(_data));
            _lastSave = DateTime.UtcNow;
        }
        catch
        {
            // Best-effort persistence.
        }
    }

    public static string FormatBytes(long b)
    {
        if (b >= 1L << 30) return $"{b / (double)(1L << 30):0.00} GB";
        if (b >= 1L << 20) return $"{b / (double)(1L << 20):0.0} MB";
        if (b >= 1L << 10) return $"{b / (double)(1L << 10):0} KB";
        return $"{b} B";
    }
}
