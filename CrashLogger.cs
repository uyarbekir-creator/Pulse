using System.IO;

namespace InternetSpeedWidget;

/// <summary>
/// Appends unhandled-exception details to %APPDATA%\InternetSpeedWidget\error.log
/// so unexpected exits can be diagnosed after the fact.
/// </summary>
public static class CrashLogger
{
    public static string LogPath => Path.Combine(Settings.AppDataDir, "error.log");

    public static void Log(string source, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Settings.AppDataDir);
            File.AppendAllText(
                LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
