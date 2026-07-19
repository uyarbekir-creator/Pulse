using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace InternetSpeedWidget;

/// <summary>
/// Experimental: uses ETW (the Microsoft-Windows-Kernel-Network provider,
/// same data Task Manager uses) to attribute network bytes to processes and
/// report the current top talker. Starting an ETW session requires the app
/// to run elevated (as Administrator).
/// </summary>
public class AppTrafficMonitor : IDisposable
{
    private const string SessionName = "InternetSpeedWidget-NetTrace";

    // Kernel-Network event ids: TCPv4/v6 send+recv, UDPv4/v6 send+recv.
    private static readonly HashSet<int> DataEventIds = new() { 10, 11, 26, 27, 42, 43, 58, 59 };

    private TraceEventSession? _session;
    private readonly ConcurrentDictionary<int, long> _bytesByPid = new();
    private readonly Dictionary<int, string> _nameCache = new();

    public bool IsRunning => _session != null;

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Starts the ETW session. Returns false when unavailable (not admin).</summary>
    public bool Start()
    {
        if (_session != null)
            return true;
        if (!IsElevated)
            return false;

        try
        {
            // Creating a session with an existing name takes it over, so a
            // leftover session from a crashed run is cleaned up automatically.
            _session = new TraceEventSession(SessionName);
            _session.EnableProvider("Microsoft-Windows-Kernel-Network");

            var registered = new RegisteredTraceEventParser(_session.Source);
            registered.All += OnEvent;
            _session.Source.Dynamic.All += OnEvent;

            Task.Run(() =>
            {
                try { _session?.Source.Process(); }
                catch (Exception ex) { CrashLogger.Log("AppTrafficMonitor.Process", ex); }
            });
            return true;
        }
        catch (Exception ex)
        {
            CrashLogger.Log("AppTrafficMonitor.Start", ex);
            Stop();
            return false;
        }
    }

    private void OnEvent(TraceEvent ev)
    {
        if (!DataEventIds.Contains((int)ev.ID))
            return;

        try
        {
            int pid = ToInt(ev.PayloadByName("PID"));
            long size = ToInt(ev.PayloadByName("size"));
            if (pid <= 0 || size <= 0)
                return;
            _bytesByPid.AddOrUpdate(pid, size, (_, old) => old + size);
        }
        catch
        {
            // Ignore undecodable events.
        }
    }

    private static int ToInt(object? value) =>
        value == null ? 0 : Convert.ToInt32(value);

    /// <summary>
    /// Returns the process with the most bytes since the previous call and
    /// resets the counters. Null when there was no measurable traffic.
    /// </summary>
    public (string Name, long Bytes)? TakeTop()
    {
        if (_bytesByPid.IsEmpty)
            return null;

        int topPid = 0;
        long topBytes = 0;
        foreach (var kv in _bytesByPid)
        {
            if (kv.Value > topBytes)
            {
                topBytes = kv.Value;
                topPid = kv.Key;
            }
        }
        _bytesByPid.Clear();

        if (topPid == 0 || topBytes <= 0)
            return null;
        return (ResolveName(topPid), topBytes);
    }

    private string ResolveName(int pid)
    {
        if (_nameCache.TryGetValue(pid, out var cached))
            return cached;

        string name;
        try { name = Process.GetProcessById(pid).ProcessName; }
        catch { name = $"pid {pid}"; }

        if (_nameCache.Count > 256)
            _nameCache.Clear();
        _nameCache[pid] = name;
        return name;
    }

    public void Stop()
    {
        try { _session?.Dispose(); } catch { }
        _session = null;
        _bytesByPid.Clear();
    }

    public void Dispose() => Stop();
}
