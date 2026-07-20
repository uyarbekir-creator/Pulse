using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Pulse;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;
    private const string MutexName = "Pulse_SingleInstance_{4F2A1C6E-9B3D-4E7A-8C1F-2A5B6C7D8E9F}";

    protected override void OnStartup(StartupEventArgs e)
    {
        // Log unhandled exceptions and keep the app alive on UI-thread ones,
        // so a transient error doesn't silently kill the widget and tray icon.
        DispatcherUnhandledException += (_, args) =>
        {
            CrashLogger.Log("DispatcherUnhandledException", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            CrashLogger.Log("AppDomain.UnhandledException", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLogger.Log("UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        // Single-instance guard. If another instance holds the mutex, wait
        // briefly — a "restart as administrator" handoff starts the new
        // process moments before the old one finishes exiting.
        _singleInstanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            bool acquired = false;
            try { acquired = _singleInstanceMutex.WaitOne(TimeSpan.FromSeconds(4)); }
            catch (AbandonedMutexException) { acquired = true; }

            if (!acquired)
            {
                System.Windows.MessageBox.Show(
                    "Pulse is already running (check the system tray).",
                    "Pulse",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Shutdown();
                return;
            }
        }

        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        // The window shows itself only if settings say it was visible.
        window.InitializeFromSettings();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }
        catch { }
        base.OnExit(e);
    }
}
