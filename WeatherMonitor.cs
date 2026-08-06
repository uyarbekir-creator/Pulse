using System.Globalization;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Pulse;

/// <summary>
/// Current temperature plus today's high/low for the machine's approximate
/// location. Location comes from IP geolocation (no GPS, no permission
/// prompt) and the forecast from Open-Meteo — both free and keyless.
///
/// Refreshes every 15 minutes; a failed fetch backs off to a short retry
/// instead of either hammering the API or going dark until the next quarter
/// hour. All network calls go through <see cref="Ipv4Http"/> because this
/// machine's IPv6 route is broken.
/// </summary>
public class WeatherMonitor : IDisposable
{
    private const int NormalMinutes = 15;
    private static readonly TimeSpan NormalInterval = TimeSpan.FromMinutes(NormalMinutes);

    // Failures retry quickly — the usual cause is the network not being up
    // yet (right after a resume), which clears within seconds, not minutes.
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    private static readonly HttpClient Http = Ipv4Http.Create(10);

    private readonly DispatcherTimer _timer = new() { Interval = NormalInterval };
    private bool _fetchInFlight;
    private double? _latitude;
    private double? _longitude;
    private bool _fahrenheit;
    private bool _hooked;

    /// <summary>False until a fetch has succeeded; the UI shows "—".</summary>
    public bool Available { get; private set; }

    public double CurrentTemp { get; private set; }
    public double HighTemp { get; private set; }
    public double LowTemp { get; private set; }
    public string City { get; private set; } = "";

    /// <summary>Unit switch. Changing it refetches rather than converting
    /// locally, so the numbers always come rounded the way the API means them.</summary>
    public bool Fahrenheit
    {
        get => _fahrenheit;
        set
        {
            if (_fahrenheit == value)
                return;
            _fahrenheit = value;
            if (_timer.IsEnabled)
                Refresh();
        }
    }

    public WeatherMonitor()
    {
        _timer.Tick += (_, _) => Refresh();
    }

    /// <summary>Only polls while the weather frame is actually shown.</summary>
    public bool Enabled
    {
        get => _timer.IsEnabled;
        set
        {
            if (value && !_timer.IsEnabled)
            {
                HookSystemEvents();
                _timer.Start();
                Refresh(); // don't make the user wait 15 minutes for the first reading
            }
            else if (!value && _timer.IsEnabled)
            {
                _timer.Stop();
            }
        }
    }

    /// <summary>
    /// Resuming from sleep, or reconnecting, leaves the last fetch stale and
    /// usually fails the one right after (the adapter isn't up yet). Both
    /// events re-arm a quick retry so the reading recovers on its own instead
    /// of sitting on "—" until the next quarter-hour tick.
    /// </summary>
    private void HookSystemEvents()
    {
        if (_hooked)
            return;
        _hooked = true;
        try
        {
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        }
        catch
        {
            // Non-fatal: without the hooks the timer still recovers, just slower.
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
            ScheduleWakeRetry();
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable)
            ScheduleWakeRetry();
    }

    /// <summary>Retry shortly after a resume — immediately is too early, the
    /// network stack is typically still coming up.</summary>
    private void ScheduleWakeRetry()
    {
        // These events arrive on a pool thread; the timer belongs to the UI one.
        _timer.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_timer.IsEnabled)
                return;
            SetInterval(RetryInterval);
            Refresh();
        }));
    }

    private async void Refresh()
    {
        if (_fetchInFlight)
            return;
        _fetchInFlight = true;
        try
        {
            await FetchAsync();
        }
        catch
        {
            // Offline, DNS failure, API hiccup — keep the last known values
            // on screen rather than blanking them, and try again shortly.
            Available = false;
            SetInterval(RetryInterval);
        }
        finally
        {
            _fetchInFlight = false;
        }
    }

    private async Task FetchAsync()
    {
        if (_latitude == null || _longitude == null)
            await GeolocateAsync();

        if (_latitude == null || _longitude == null)
        {
            Available = false;
            SetInterval(RetryInterval);
            return;
        }

        // Invariant culture matters: on a Turkish locale "41.02" would
        // otherwise format as "41,02" and break the query string.
        string lat = _latitude.Value.ToString("0.####", CultureInfo.InvariantCulture);
        string lon = _longitude.Value.ToString("0.####", CultureInfo.InvariantCulture);
        string unit = _fahrenheit ? "&temperature_unit=fahrenheit" : "";
        string url =
            $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}" +
            "&current=temperature_2m&daily=temperature_2m_max,temperature_2m_min" +
            "&timezone=auto&forecast_days=1" + unit;

        string json = await Http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        double current = root.GetProperty("current").GetProperty("temperature_2m").GetDouble();
        var daily = root.GetProperty("daily");
        double high = daily.GetProperty("temperature_2m_max")[0].GetDouble();
        double low = daily.GetProperty("temperature_2m_min")[0].GetDouble();

        CurrentTemp = current;
        HighTemp = high;
        LowTemp = low;
        Available = true;
        SetInterval(NormalInterval);
    }

    /// <summary>
    /// Approximate location from the public IP. Resolved once per app run —
    /// a machine rarely moves mid-session, and both services rate-limit.
    /// </summary>
    private async Task GeolocateAsync()
    {
        if (await TryGeolocateAsync("https://ipapi.co/json/", "latitude", "longitude"))
            return;
        await TryGeolocateAsync("http://ip-api.com/json/", "lat", "lon");
    }

    private async Task<bool> TryGeolocateAsync(string url, string latField, string lonField)
    {
        try
        {
            string json = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Rate-limit/error responses are still valid JSON but carry no
            // coordinates, so probe for the fields rather than trusting 200 OK.
            if (!root.TryGetProperty(latField, out var lat) ||
                !root.TryGetProperty(lonField, out var lon) ||
                lat.ValueKind != JsonValueKind.Number ||
                lon.ValueKind != JsonValueKind.Number)
                return false;

            _latitude = lat.GetDouble();
            _longitude = lon.GetDouble();
            if (root.TryGetProperty("city", out var city) && city.ValueKind == JsonValueKind.String)
                City = city.GetString() ?? "";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SetInterval(TimeSpan wanted)
    {
        if (_timer.Interval == wanted)
            return;
        _timer.Interval = wanted; // restarts the countdown, which is what we want
    }

    public void Dispose()
    {
        _timer.Stop();
        if (!_hooked)
            return;
        _hooked = false;
        try
        {
            // SystemEvents keeps a strong reference; leaving this attached
            // would keep the window alive after shutdown.
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        }
        catch
        {
            // Nothing useful to do while tearing down.
        }
    }
}
