using System.IO;
using System.Text.Json;

namespace InternetSpeedWidget;

/// <summary>
/// Records per-minute averages of speed and ping for the last 24 hours,
/// persisted to %APPDATA%\InternetSpeedWidget\history.json so the chart
/// survives restarts.
/// </summary>
public class HistoryRecorder
{
    public record Entry(DateTime Time, double Down, double Up, double Ping);

    private const int MaxEntries = 24 * 60;
    private readonly List<Entry> _entries = new();
    private readonly object _lock = new();

    private DateTime _currentMinute;
    private double _sumDown, _sumUp, _sumPing;
    private int _samples, _pingSamples;
    private DateTime _lastSave = DateTime.MinValue;

    public static string HistoryPath => Path.Combine(Settings.AppDataDir, "history.json");

    public HistoryRecorder()
    {
        _currentMinute = MinuteFloor(DateTime.Now);
        try
        {
            if (File.Exists(HistoryPath))
            {
                var loaded = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(HistoryPath));
                if (loaded != null)
                {
                    var cutoff = DateTime.Now.AddHours(-24);
                    _entries.AddRange(loaded.Where(e => e.Time >= cutoff));
                }
            }
        }
        catch
        {
            // Corrupt history -> start fresh.
        }
    }

    /// <summary>Feed one sample (called on every widget tick).</summary>
    public void Record(double downBps, double upBps, long? pingMs)
    {
        var minute = MinuteFloor(DateTime.Now);
        if (minute != _currentMinute)
        {
            Flush();
            _currentMinute = minute;
        }

        _sumDown += downBps;
        _sumUp += upBps;
        _samples++;
        if (pingMs.HasValue)
        {
            _sumPing += pingMs.Value;
            _pingSamples++;
        }
    }

    private void Flush()
    {
        if (_samples > 0)
        {
            var entry = new Entry(
                _currentMinute,
                _sumDown / _samples,
                _sumUp / _samples,
                _pingSamples > 0 ? _sumPing / _pingSamples : 0);

            lock (_lock)
            {
                _entries.Add(entry);
                var cutoff = DateTime.Now.AddHours(-24);
                _entries.RemoveAll(e => e.Time < cutoff);
                while (_entries.Count > MaxEntries)
                    _entries.RemoveAt(0);
            }
        }

        _sumDown = _sumUp = _sumPing = 0;
        _samples = _pingSamples = 0;

        if ((DateTime.UtcNow - _lastSave).TotalMinutes >= 5)
            Save();
    }

    public List<Entry> Snapshot()
    {
        lock (_lock)
            return new List<Entry>(_entries);
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Settings.AppDataDir);
            lock (_lock)
                File.WriteAllText(HistoryPath, JsonSerializer.Serialize(_entries));
            _lastSave = DateTime.UtcNow;
        }
        catch
        {
            // Best-effort persistence.
        }
    }

    private static DateTime MinuteFloor(DateTime t) =>
        new(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, t.Kind);
}
