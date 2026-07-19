using System.IO;
using System.Windows.Threading;

namespace InternetSpeedWidget;

/// <summary>
/// Experimental: pings every 5 seconds and detects internet outages
/// (3 consecutive failures). Outages are appended to
/// %APPDATA%\InternetSpeedWidget\outages.log and a Recovered event fires
/// when the connection comes back.
/// </summary>
public class OutageMonitor : IDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly Func<string> _hostGetter;
    private bool _pingInFlight;
    private int _failStreak;
    private DateTime? _outageStart;
    private string _statsDay = DateTime.Now.ToString("yyyy-MM-dd");

    /// <summary>Fired (on the UI thread) when the connection recovers.</summary>
    public event Action<TimeSpan>? Recovered;

    public bool IsDown => _outageStart != null;
    public DateTime? LastOutageEnd { get; private set; }
    public TimeSpan? LastOutageDuration { get; private set; }
    public int OutagesToday { get; private set; }
    public TimeSpan OutageTimeToday { get; private set; }

    public static string LogPath => Path.Combine(Settings.AppDataDir, "outages.log");

    public OutageMonitor(Func<string> hostGetter)
    {
        _hostGetter = hostGetter;
        _timer.Tick += (_, _) => Check();
    }

    public bool Enabled
    {
        get => _timer.IsEnabled;
        set
        {
            if (value && !_timer.IsEnabled) _timer.Start();
            else if (!value && _timer.IsEnabled)
            {
                _timer.Stop();
                _failStreak = 0;
                _outageStart = null;
            }
        }
    }

    private async void Check()
    {
        if (_pingInFlight)
            return;
        _pingInFlight = true;
        bool ok = await NetworkPing.PingAsync(_hostGetter(), 2000) != null;
        _pingInFlight = false;

        RolloverDay();

        if (!ok)
        {
            _failStreak++;
            if (_failStreak >= 3 && _outageStart == null)
                _outageStart = DateTime.Now.AddSeconds(-10); // roughly when failures began
            return;
        }

        _failStreak = 0;
        if (_outageStart != null)
        {
            var start = _outageStart.Value;
            var end = DateTime.Now;
            var duration = end - start;
            _outageStart = null;

            LastOutageEnd = end;
            LastOutageDuration = duration;
            OutagesToday++;
            OutageTimeToday += duration;

            Log(start, end, duration);
            Recovered?.Invoke(duration);
        }
    }

    private void RolloverDay()
    {
        string day = DateTime.Now.ToString("yyyy-MM-dd");
        if (day != _statsDay)
        {
            _statsDay = day;
            OutagesToday = 0;
            OutageTimeToday = TimeSpan.Zero;
        }
    }

    private static void Log(DateTime start, DateTime end, TimeSpan duration)
    {
        try
        {
            Directory.CreateDirectory(Settings.AppDataDir);
            File.AppendAllText(LogPath,
                $"{start:yyyy-MM-dd HH:mm:ss} -> {end:HH:mm:ss}  ({FormatDuration(duration)}){Environment.NewLine}");
        }
        catch
        {
            // Best-effort logging.
        }
    }

    public static string FormatDuration(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} h {t.Minutes} min";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes} min {t.Seconds} s";
        return $"{Math.Max(1, (int)t.TotalSeconds)} s";
    }

    public void Dispose() => _timer.Stop();
}
