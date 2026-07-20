using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Pulse;

/// <summary>
/// Samples CPU / RAM / GPU / Disk load for the optional AIO system monitor
/// panel. Same shape as <see cref="SpeedMonitor"/>: plain read-only
/// properties updated by a parameterless Sample(). Internally throttled to
/// one real sample per second — the widget timer can tick as fast as 0.25s
/// and GPU Engine counter enumeration is too expensive for that rate.
/// </summary>
public class SystemMonitor : IDisposable
{
    private DateTime _lastSample = DateTime.MinValue;

    /// <summary>CPU load 0-100 across all cores.</summary>
    public double CpuPercent { get; private set; }

    public long RamUsedBytes { get; private set; }
    public long RamTotalBytes { get; private set; }

    /// <summary>Name of the process using the most CPU right now (empty until known).</summary>
    public string TopCpuProcessName { get; private set; } = "";
    public double TopCpuProcessPercent { get; private set; }

    /// <summary>Name of the process using the most RAM right now (empty until known).</summary>
    public string TopRamProcessName { get; private set; } = "";
    public long TopRamProcessBytes { get; private set; }

    /// <summary>GPU load 0-100 (3D engine). Meaningless when !GpuAvailable.</summary>
    public double GpuPercent { get; private set; }

    /// <summary>False when the selected GPU backend can't produce a value.</summary>
    public bool GpuAvailable { get; private set; }

    public double DiskReadBytesPerSec { get; private set; }
    public double DiskWriteBytesPerSec { get; private set; }

    public readonly record struct DriveSpace(string Letter, long UsedBytes, long TotalBytes);

    /// <summary>Every ready fixed drive (typically C:, D:, ...), sorted by letter.</summary>
    public IReadOnlyList<DriveSpace> DriveSpaces { get; private set; } = Array.Empty<DriveSpace>();

    private GpuBackend _backend = GpuBackend.Generic;

    /// <summary>GPU sampling method. Changing it re-probes availability.</summary>
    public GpuBackend Backend
    {
        get => _backend;
        set
        {
            if (_backend == value)
                return;
            _backend = value;
            _nvidiaSmiWorks = null;
            GpuAvailable = false;
        }
    }

    // ------------------------------------------------------------- Counters

    private bool _countersCreated;
    private PerformanceCounter? _cpuCounter;
    private PerformanceCounter? _diskReadCounter;
    private PerformanceCounter? _diskWriteCounter;

    /// <summary>
    /// Created lazily on the first Sample() so users who never enable the
    /// AIO panel pay no perf-counter cost at all. The counters are rate
    /// counters: the first NextValue() only establishes a baseline (returns
    /// zero), matching SpeedMonitor's first-sample idiom.
    /// </summary>
    private void EnsureCounters()
    {
        if (_countersCreated)
            return;
        _countersCreated = true;

        _cpuCounter = TryCreateCounter("Processor", "% Processor Time", "_Total");
        _diskReadCounter = TryCreateCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
        _diskWriteCounter = TryCreateCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
    }

    private static PerformanceCounter? TryCreateCounter(string category, string counter, string instance)
    {
        try
        {
            var c = new PerformanceCounter(category, counter, instance);
            c.NextValue(); // baseline
            return c;
        }
        catch
        {
            // Category missing/corrupt on this machine — metric shows 0.
            return null;
        }
    }

    private static double SafeNext(PerformanceCounter? counter)
    {
        try
        {
            return counter?.NextValue() ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    // --------------------------------------------------------------- Sample

    public void Sample()
    {
        var now = DateTime.UtcNow;
        if (_lastSample != DateTime.MinValue && (now - _lastSample).TotalMilliseconds < 1000)
            return;
        _lastSample = now;

        EnsureCounters();

        CpuPercent = Math.Clamp(SafeNext(_cpuCounter), 0, 100);
        DiskReadBytesPerSec = Math.Max(0, SafeNext(_diskReadCounter));
        DiskWriteBytesPerSec = Math.Max(0, SafeNext(_diskWriteCounter));

        SampleRam();
        SampleDiskSpace();
        SampleTopProcesses();

        if (_backend == GpuBackend.Nvidia)
            SampleGpuNvidia();
        else
            SampleGpuGeneric();
    }

    // ------------------------------------------------------------------ RAM

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    private void SampleRam()
    {
        var status = new MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>()
        };
        if (GlobalMemoryStatusEx(ref status))
        {
            RamTotalBytes = (long)status.ullTotalPhys;
            RamUsedBytes = (long)(status.ullTotalPhys - status.ullAvailPhys);
        }
    }

    // ------------------------------------------------------------ Top process

    private DateTime _lastProcessSample = DateTime.MinValue;
    private Dictionary<int, TimeSpan> _prevCpuTimes = new();

    /// <summary>
    /// Finds the busiest process by CPU (normalized to 0-100 across all
    /// cores, matching modern Task Manager) and the biggest by working set.
    /// CPU needs a delta between two samples, so the first call after
    /// startup (or after a >1s gap) reports nothing for CPU. Every
    /// enumerated Process must be disposed, not just the winners, or their
    /// handles leak.
    /// </summary>
    private void SampleTopProcesses()
    {
        var now = DateTime.UtcNow;
        double elapsedSec = _lastProcessSample == DateTime.MinValue
            ? 0
            : (now - _lastProcessSample).TotalSeconds;
        var newCpuTimes = new Dictionary<int, TimeSpan>();

        string bestCpuName = "";
        double bestCpuPct = 0;
        string bestRamName = "";
        long bestRamBytes = 0;

        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                if (proc.Id == 0) // System Idle Process: not a real app, always dominates
                    continue;

                TimeSpan cpuTime = proc.TotalProcessorTime;
                newCpuTimes[proc.Id] = cpuTime;

                if (elapsedSec > 0 && _prevCpuTimes.TryGetValue(proc.Id, out var prevTime))
                {
                    double pct = (cpuTime - prevTime).TotalMilliseconds
                        / (elapsedSec * 1000.0) / Environment.ProcessorCount * 100.0;
                    if (pct > bestCpuPct)
                    {
                        bestCpuPct = pct;
                        bestCpuName = proc.ProcessName;
                    }
                }

                long ram = proc.WorkingSet64;
                if (ram > bestRamBytes)
                {
                    bestRamBytes = ram;
                    bestRamName = proc.ProcessName;
                }
            }
            catch
            {
                // Access denied (protected/elevated processes) or the
                // process exited mid-enumeration — just skip it.
            }
            finally
            {
                proc.Dispose();
            }
        }

        _prevCpuTimes = newCpuTimes;
        _lastProcessSample = now;

        if (bestCpuName.Length > 0)
        {
            TopCpuProcessName = bestCpuName;
            TopCpuProcessPercent = Math.Clamp(bestCpuPct, 0, 100);
        }
        if (bestRamName.Length > 0)
        {
            TopRamProcessName = bestRamName;
            TopRamProcessBytes = bestRamBytes;
        }
    }

    // ----------------------------------------------------------- Disk space

    private void SampleDiskSpace()
    {
        try
        {
            var drives = DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .OrderBy(d => d.Name)
                .Select(d => new DriveSpace(
                    d.Name.TrimEnd('\\'),
                    d.TotalSize - d.AvailableFreeSpace,
                    d.TotalSize))
                .ToList();
            if (drives.Count > 0)
                DriveSpaces = drives;
        }
        catch
        {
            // Drives momentarily unavailable — keep the previous numbers.
        }
    }

    // ---------------------------------------------------------- GPU generic

    private PerformanceCounterCategory? _gpuCategory;
    private bool _gpuCategoryBroken;

    // "Utilization Percentage" is a rate counter: a freshly created counter
    // always reads 0. Counters must therefore live across samples, keyed by
    // instance name; the instance list itself (one instance per process) is
    // re-enumerated every sample because instances come and go with processes.
    private readonly Dictionary<string, PerformanceCounter> _gpuCounters = new();

    private void SampleGpuGeneric()
    {
        if (_gpuCategoryBroken)
        {
            GpuAvailable = false;
            return;
        }

        try
        {
            _gpuCategory ??= new PerformanceCounterCategory("GPU Engine");
            string[] names = _gpuCategory.GetInstanceNames();

            // Same filter Task Manager uses for its headline GPU number.
            var live = new HashSet<string>();
            double total = 0;
            foreach (string name in names)
            {
                if (!name.Contains("engtype_3D"))
                    continue;

                if (!_gpuCounters.TryGetValue(name, out var counter))
                {
                    try
                    {
                        counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", name);
                        _gpuCounters[name] = counter;
                    }
                    catch
                    {
                        continue; // process exited between enumerate and create
                    }
                }

                try
                {
                    total += counter.NextValue();
                    live.Add(name);
                }
                catch
                {
                    counter.Dispose();
                    _gpuCounters.Remove(name);
                }
            }

            // Drop counters whose processes are gone.
            foreach (string stale in _gpuCounters.Keys.Where(k => !live.Contains(k)).ToList())
            {
                _gpuCounters[stale].Dispose();
                _gpuCounters.Remove(stale);
            }

            GpuPercent = Math.Clamp(total, 0, 100);
            GpuAvailable = true;
        }
        catch
        {
            // "GPU Engine" category doesn't exist on this machine; don't
            // retry every second.
            _gpuCategoryBroken = true;
            GpuAvailable = false;
        }
    }

    // ----------------------------------------------------------- GPU nvidia

    private bool? _nvidiaSmiWorks; // null = not probed yet
    private volatile bool _nvidiaQueryInFlight;

    private void SampleGpuNvidia()
    {
        if (_nvidiaSmiWorks == false)
        {
            GpuAvailable = false;
            return;
        }
        if (_nvidiaQueryInFlight)
            return;
        _nvidiaQueryInFlight = true;

        // Run off the UI thread — Process.Start + WaitForExit can take
        // hundreds of ms and must never stall the widget timer.
        Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    Arguments = "--query-gpu=utilization.gpu --format=csv,noheader,nounits",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    _nvidiaSmiWorks = false;
                    GpuAvailable = false;
                    return;
                }

                string line = proc.StandardOutput.ReadLine() ?? "";
                if (!proc.WaitForExit(2000))
                {
                    try { proc.Kill(); } catch { }
                    _nvidiaSmiWorks = false;
                    GpuAvailable = false;
                    return;
                }

                if (proc.ExitCode == 0 &&
                    double.TryParse(line.Trim(), System.Globalization.CultureInfo.InvariantCulture, out double util))
                {
                    GpuPercent = Math.Clamp(util, 0, 100);
                    GpuAvailable = true;
                    _nvidiaSmiWorks = true;
                }
                else
                {
                    _nvidiaSmiWorks = false;
                    GpuAvailable = false;
                }
            }
            catch
            {
                // nvidia-smi not installed (Win32Exception) or failed.
                _nvidiaSmiWorks = false;
                GpuAvailable = false;
            }
            finally
            {
                _nvidiaQueryInFlight = false;
            }
        });
    }

    // -------------------------------------------------------------- Dispose

    public void Dispose()
    {
        _cpuCounter?.Dispose();
        _diskReadCounter?.Dispose();
        _diskWriteCounter?.Dispose();
        foreach (var counter in _gpuCounters.Values)
            counter.Dispose();
        _gpuCounters.Clear();
    }
}
