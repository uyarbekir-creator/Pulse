using System.Net.NetworkInformation;

namespace Pulse;

/// <summary>
/// Samples the byte counters of the active physical network interface(s) and
/// reports throughput (bytes/sec) for download and upload.
/// This is NOT a speed test - it measures live adapter throughput.
/// </summary>
public class SpeedMonitor
{
    private long _lastBytesReceived;
    private long _lastBytesSent;
    private DateTime _lastSample = DateTime.MinValue;

    /// <summary>Latest download speed in bytes per second.</summary>
    public double DownloadBytesPerSec { get; private set; }

    /// <summary>Latest upload speed in bytes per second.</summary>
    public double UploadBytesPerSec { get; private set; }

    /// <summary>Total bytes (down + up) transferred since the previous sample.</summary>
    public long LastDeltaBytes { get; private set; }

    /// <summary>True when at least one usable network interface is available.</summary>
    public bool HasNetwork { get; private set; }

    /// <summary>
    /// Interface Id to monitor exclusively. Empty = sum all physical adapters.
    /// </summary>
    public string AdapterFilterId { get; set; } = "";

    /// <summary>Forget the counter baseline (call after changing the adapter filter).</summary>
    public void Reset() => _lastSample = DateTime.MinValue;

    /// <summary>A monitorable adapter, for the settings UI.</summary>
    public record AdapterInfo(string Id, string Name, string Description);

    /// <summary>Currently-up, non-loopback adapters the user can choose from.</summary>
    public static List<AdapterInfo> GetAdapters()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                             ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(ni => new AdapterInfo(ni.Id, ni.Name, ni.Description))
                .ToList();
        }
        catch
        {
            return new List<AdapterInfo>();
        }
    }

    /// <summary>
    /// Reads current counters and updates the speed properties based on the
    /// elapsed time since the previous sample.
    /// </summary>
    public void Sample()
    {
        long totalReceived = 0;
        long totalSent = 0;
        bool any = false;

        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!IsMonitored(ni))
                    continue;

                var stats = ni.GetIPv4Statistics();
                totalReceived += stats.BytesReceived;
                totalSent += stats.BytesSent;
                any = true;
            }
        }
        catch
        {
            any = false;
        }

        HasNetwork = any;
        var now = DateTime.UtcNow;

        if (!any)
        {
            DownloadBytesPerSec = 0;
            UploadBytesPerSec = 0;
            LastDeltaBytes = 0;
            _lastSample = now;
            _lastBytesReceived = 0;
            _lastBytesSent = 0;
            return;
        }

        if (_lastSample == DateTime.MinValue)
        {
            // First sample - establish baseline, report zero.
            _lastBytesReceived = totalReceived;
            _lastBytesSent = totalSent;
            _lastSample = now;
            DownloadBytesPerSec = 0;
            UploadBytesPerSec = 0;
            LastDeltaBytes = 0;
            return;
        }

        double elapsed = (now - _lastSample).TotalSeconds;
        if (elapsed <= 0)
            elapsed = 0.0001;

        // Guard against counter resets (adapter change) producing negatives.
        long deltaDown = Math.Max(0, totalReceived - _lastBytesReceived);
        long deltaUp = Math.Max(0, totalSent - _lastBytesSent);

        DownloadBytesPerSec = deltaDown / elapsed;
        UploadBytesPerSec = deltaUp / elapsed;
        LastDeltaBytes = deltaDown + deltaUp;

        _lastBytesReceived = totalReceived;
        _lastBytesSent = totalSent;
        _lastSample = now;
    }

    private bool IsMonitored(NetworkInterface ni)
    {
        if (ni.OperationalStatus != OperationalStatus.Up)
            return false;

        // Explicit adapter choice wins over the physical-adapter heuristics.
        if (!string.IsNullOrEmpty(AdapterFilterId))
            return ni.Id == AdapterFilterId;

        if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
            ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
            return false;

        // Filter out common virtual / non-physical adapters by description/name.
        string desc = (ni.Description ?? string.Empty).ToLowerInvariant();
        string name = (ni.Name ?? string.Empty).ToLowerInvariant();
        string[] virtualHints =
        {
            "virtual", "vmware", "hyper-v", "vethernet", "loopback",
            "pseudo", "tap", "tunnel", "vpn", "vbox", "docker", "bluetooth",
            "wan miniport", "microsoft wi-fi direct", "teredo", "isatap"
        };
        foreach (var hint in virtualHints)
        {
            if (desc.Contains(hint) || name.Contains(hint))
                return false;
        }

        return true;
    }

    /// <summary>Formats a bytes/sec value according to the chosen unit.</summary>
    public static string Format(double bytesPerSec, SpeedUnit unit)
    {
        if (bytesPerSec < 0)
            bytesPerSec = 0;

        switch (unit)
        {
            case SpeedUnit.MBps:
            {
                double mbps = bytesPerSec / (1024.0 * 1024.0);
                if (mbps >= 100) return $"{mbps:0} MB/s";
                if (mbps >= 10) return $"{mbps:0.0} MB/s";
                double kbps = bytesPerSec / 1024.0;
                if (kbps < 1000 && mbps < 1) return $"{kbps:0.0} KB/s";
                return $"{mbps:0.00} MB/s";
            }
            case SpeedUnit.Auto:
            {
                double bits = bytesPerSec * 8.0;
                if (bits >= 1_000_000_000)
                    return $"{bits / 1_000_000_000:0.00} Gbps";
                if (bits >= 1_000_000)
                    return $"{bits / 1_000_000:0.0} Mbps";
                if (bits >= 1_000)
                    return $"{bits / 1_000:0.0} Kbps";
                return $"{bits:0} bps";
            }
            case SpeedUnit.Mbps:
            default:
            {
                double mbit = (bytesPerSec * 8.0) / 1_000_000.0;
                if (mbit >= 100) return $"{mbit:0} Mbps";
                if (mbit >= 10) return $"{mbit:0.0} Mbps";
                return $"{mbit:0.00} Mbps";
            }
        }
    }

    /// <summary>
    /// Very short value (no unit suffix) for drawing inside the 16x16 tray icon,
    /// e.g. "0.4", "24", "150".
    /// </summary>
    public static string CompactValue(double bytesPerSec, SpeedUnit unit)
    {
        double v = unit == SpeedUnit.MBps
            ? bytesPerSec / (1024.0 * 1024.0)
            : bytesPerSec * 8.0 / 1_000_000.0;

        if (v >= 1000) return "1k+";
        if (v >= 100) return ((int)v).ToString();
        if (v >= 10) return v.ToString("0");
        return v.ToString("0.0");
    }
}
