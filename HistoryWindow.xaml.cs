using System.Windows;
using Media = System.Windows.Media;

namespace Pulse;

public partial class HistoryWindow : Window
{
    private readonly HistoryRecorder _history;
    private readonly UsageTracker _usage;
    private readonly OutageMonitor _outage;

    public HistoryWindow(HistoryRecorder history, UsageTracker usage, OutageMonitor outage)
    {
        InitializeComponent();
        _history = history;
        _usage = usage;
        _outage = outage;

        RefreshButton.Click += (_, _) => Draw();
        CloseButton.Click += (_, _) => Close();
        Loaded += (_, _) => Draw();
    }

    private void Draw()
    {
        var entries = _history.Snapshot();

        string outageInfo;
        if (_outage.LastOutageEnd.HasValue && _outage.LastOutageDuration.HasValue)
        {
            outageInfo = $"Outages today: {_outage.OutagesToday} " +
                         $"({OutageMonitor.FormatDuration(_outage.OutageTimeToday)}) · " +
                         $"last: {_outage.LastOutageEnd:HH:mm} " +
                         $"({OutageMonitor.FormatDuration(_outage.LastOutageDuration.Value)})";
        }
        else
        {
            outageInfo = _outage.Enabled ? "No outages recorded today." : "Outage logging is off.";
        }

        SummaryText.Text =
            $"Data used — today: {UsageTracker.FormatBytes(_usage.TodayBytes)} · " +
            $"this month: {UsageTracker.FormatBytes(_usage.MonthBytes)}\n{outageInfo}";

        var now = DateTime.Now;
        var windowStart = now.AddHours(-24);
        TimeAxisText.Text = $"{windowStart:HH:mm}  ─────────────  {now.AddHours(-12):HH:mm}  ─────────────  {now:HH:mm}";

        if (entries.Count < 2)
        {
            SpeedScaleText.Text = "collecting data — check back in a few minutes";
            PingScaleText.Text = "";
            DownSeries.Points = new Media.PointCollection();
            UpSeries.Points = new Media.PointCollection();
            PingSeries.Points = new Media.PointCollection();
            return;
        }

        double maxSpeed = Math.Max(10 * 1024, entries.Max(e => Math.Max(e.Down, e.Up)));
        double maxPing = Math.Max(20, entries.Max(e => e.Ping));

        SpeedScaleText.Text = $"peak: {SpeedMonitor.Format(maxSpeed, SpeedUnit.Mbps)}";
        PingScaleText.Text = $"peak: {maxPing:0} ms";

        DownSeries.Points = BuildSeries(entries, e => e.Down, maxSpeed, windowStart,
            SpeedCanvas.Width, SpeedCanvas.Height);
        UpSeries.Points = BuildSeries(entries, e => e.Up, maxSpeed, windowStart,
            SpeedCanvas.Width, SpeedCanvas.Height);
        PingSeries.Points = BuildSeries(entries, e => e.Ping, maxPing, windowStart,
            PingCanvas.Width, PingCanvas.Height);
    }

    private static Media.PointCollection BuildSeries(
        List<HistoryRecorder.Entry> entries,
        Func<HistoryRecorder.Entry, double> selector,
        double max, DateTime windowStart, double width, double height)
    {
        var pts = new Media.PointCollection();
        double windowSeconds = 24 * 3600;

        foreach (var e in entries)
        {
            double x = (e.Time - windowStart).TotalSeconds / windowSeconds * width;
            if (x < 0)
                continue;
            double y = height - 2 - Math.Min(1.0, selector(e) / max) * (height - 6);
            pts.Add(new System.Windows.Point(x, y));
        }
        return pts;
    }
}
