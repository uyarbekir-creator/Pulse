using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace Pulse;

/// <summary>
/// Manages "Start with Windows".
///
/// Windows silently ignores HKCU Run-key entries for programs that require
/// elevation (this exe carries the user's RUNASADMIN compatibility flag), so
/// when running elevated we register a logon Scheduled Task with
/// RunLevel=HighestAvailable instead — it starts the app elevated at logon
/// with no UAC prompt. The Run key remains as a fallback for non-elevated runs.
/// </summary>
public static class StartupManager
{
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Pulse";
    private const string TaskName = "Pulse";

    // Pre-rename names (the app was called Internet Speed Widget). Cleaned
    // up by MigrateRenameIfNeeded() so the rename doesn't leave a stale
    // duplicate autostart entry or silently drop the user's preference.
    private const string LegacyValueName = "InternetSpeedWidget";
    private const string LegacyTaskName = "InternetSpeedWidget";

    /// <summary>Full path to the running executable.</summary>
    public static string? ExePath => Environment.ProcessPath;

    private static bool IsElevated
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

    // Querying schtasks spawns a process (~100ms); cache briefly so opening
    // a context menu (which refreshes the checkbox) stays snappy.
    private static bool _cachedEnabled;
    private static DateTime _cacheTime = DateTime.MinValue;

    public static bool IsEnabled()
    {
        if ((DateTime.UtcNow - _cacheTime).TotalSeconds < 5)
            return _cachedEnabled;
        _cachedEnabled = TaskExists() || RunKeyExists();
        _cacheTime = DateTime.UtcNow;
        return _cachedEnabled;
    }

    public static void SetEnabled(bool enabled)
    {
        _cacheTime = DateTime.MinValue; // force re-query after a change
        if (enabled)
        {
            if (IsElevated && CreateTask())
            {
                RemoveRunKey(); // task supersedes the (blocked) Run entry
                return;
            }
            AddRunKey(); // non-elevated fallback
        }
        else
        {
            if (IsElevated)
                DeleteTask();
            RemoveRunKey();
        }
    }

    /// <summary>
    /// Called once at app start: if autostart was registered via the Run key
    /// (which Windows ignores for elevated apps) and we now run elevated,
    /// silently upgrade it to the working Scheduled Task form.
    /// </summary>
    public static void MigrateIfNeeded()
    {
        MigrateRenameIfNeeded();
        RefreshTaskPathIfStale();
        if (RunKeyExists() && !TaskExists() && IsElevated && CreateTask())
            RemoveRunKey();
    }

    /// <summary>
    /// If the registered Scheduled Task's target exe path no longer matches
    /// where this process is actually running from (the app's folder was
    /// renamed or moved), silently recreate it — otherwise "Start with
    /// Windows" keeps pointing at a dead path and just fails at next logon.
    /// </summary>
    private static void RefreshTaskPathIfStale()
    {
        if (!IsElevated || !TaskExists())
            return;
        string? exe = ExePath;
        if (string.IsNullOrEmpty(exe))
            return;
        string? registered = GetTaskCommand();
        if (registered != null && !string.Equals(registered, exe, StringComparison.OrdinalIgnoreCase))
            CreateTask(); // /Create ... /F overwrites the existing task in place
    }

    private static string? GetTaskCommand()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Query /TN \"{TaskName}\" /XML",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (process == null)
                return null;
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10000);

            int start = output.IndexOf("<Command>", StringComparison.Ordinal);
            int end = output.IndexOf("</Command>", StringComparison.Ordinal);
            if (start < 0 || end < 0 || end <= start)
                return null;
            start += "<Command>".Length;
            return output.Substring(start, end - start).Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// One-time migration from the app's old name (InternetSpeedWidget) to
    /// Pulse: re-registers autostart under the new task/value name (carrying
    /// the user's existing preference over) and removes the old one, so the
    /// rename doesn't silently disable "Start with Windows" or leave a
    /// stale duplicate entry pointing at a since-deleted exe.
    /// </summary>
    private static void MigrateRenameIfNeeded()
    {
        bool legacyTask = LegacyTaskExists();
        bool legacyRunKey = LegacyRunKeyExists();
        if (!legacyTask && !legacyRunKey)
            return;

        if (!TaskExists() && !RunKeyExists())
            SetEnabled(true);

        if (legacyTask)
            RunSchtasks($"/Delete /TN \"{LegacyTaskName}\" /F");
        if (legacyRunKey)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
                key?.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            }
            catch
            {
                // Ignore registry access failures.
            }
        }
    }

    private static bool LegacyTaskExists() =>
        RunSchtasks($"/Query /TN \"{LegacyTaskName}\"") == 0;

    private static bool LegacyRunKeyExists()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(LegacyValueName) is string s && !string.IsNullOrEmpty(s);
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------ Run key

    private static bool RunKeyExists()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string s && !string.IsNullOrEmpty(s);
        }
        catch
        {
            return false;
        }
    }

    private static void AddRunKey()
    {
        try
        {
            string? exe = ExePath;
            if (string.IsNullOrEmpty(exe))
                return;
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            key?.SetValue(ValueName, $"\"{exe}\"");
        }
        catch
        {
            // Ignore registry access failures.
        }
    }

    private static void RemoveRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
            // Ignore registry access failures.
        }
    }

    // ----------------------------------------------------- Scheduled task

    private static bool TaskExists() =>
        RunSchtasks($"/Query /TN \"{TaskName}\"") == 0;

    private static void DeleteTask() =>
        RunSchtasks($"/Delete /TN \"{TaskName}\" /F");

    private static bool CreateTask()
    {
        try
        {
            string? exe = ExePath;
            if (string.IsNullOrEmpty(exe))
                return false;

            string userId;
            using (var identity = WindowsIdentity.GetCurrent())
                userId = identity.Name;

            // schtasks' simple flags can't disable the default 72-hour
            // execution time limit (which would kill the widget after 3 days
            // of uptime), so the task is defined via full XML.
            string xml = $"""
                <?xml version="1.0" encoding="UTF-16"?>
                <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
                  <Triggers>
                    <LogonTrigger>
                      <Enabled>true</Enabled>
                      <UserId>{Escape(userId)}</UserId>
                      <Delay>PT3S</Delay>
                    </LogonTrigger>
                  </Triggers>
                  <Principals>
                    <Principal id="Author">
                      <UserId>{Escape(userId)}</UserId>
                      <LogonType>InteractiveToken</LogonType>
                      <RunLevel>HighestAvailable</RunLevel>
                    </Principal>
                  </Principals>
                  <Settings>
                    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                    <AllowHardTerminate>false</AllowHardTerminate>
                    <StartWhenAvailable>false</StartWhenAvailable>
                    <AllowStartOnDemand>true</AllowStartOnDemand>
                    <Enabled>true</Enabled>
                    <Hidden>false</Hidden>
                    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                    <Priority>7</Priority>
                  </Settings>
                  <Actions Context="Author">
                    <Exec>
                      <Command>{Escape(exe)}</Command>
                    </Exec>
                  </Actions>
                </Task>
                """;

            string tempPath = Path.Combine(Path.GetTempPath(), "Pulse-task.xml");
            File.WriteAllText(tempPath, xml, Encoding.Unicode);
            try
            {
                return RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{tempPath}\" /F") == 0;
            }
            finally
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
        catch (Exception ex)
        {
            CrashLogger.Log("StartupManager.CreateTask", ex);
            return false;
        }
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static int RunSchtasks(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (process == null)
                return -1;
            process.WaitForExit(10000);
            return process.HasExited ? process.ExitCode : -1;
        }
        catch
        {
            return -1;
        }
    }
}
