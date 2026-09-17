using System.Diagnostics;
using System.Threading;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CallBridge.Desktop;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\ITHealthTech.CallBridge.Desktop";
    private const string ActivationEventName = @"Local\ITHealthTech.CallBridge.Desktop.Activate";
    private const int SwRestore = 9;
    private Mutex? _singleInstance;
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _activationWait;
    private bool _singleInstanceSmoke;
    private int _activationPending;
    private static string? _smokeResultPath;
    private static readonly string StartupLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IT Health Technologies", "CallBridge", "logs", "desktop-startup.log");

    internal static void LogStartup(string message)
    {
        var line = $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StartupLogPath)!);
            if (File.Exists(StartupLogPath) && new FileInfo(StartupLogPath).Length > 256 * 1024)
                File.WriteAllText(StartupLogPath, "");
            File.AppendAllText(StartupLogPath, line);
        }
        catch { }

        if (!string.IsNullOrWhiteSpace(_smokeResultPath))
        {
            try { File.AppendAllText(_smokeResultPath, line); }
            catch { }
        }
    }

    internal static IReadOnlyList<string> ReadStartupLogTail(int maximumLines = 200)
    {
        try
        {
            if (!File.Exists(StartupLogPath)) return [];
            return File.ReadLines(StartupLogPath).TakeLast(Math.Clamp(maximumLines, 1, 500)).ToArray();
        }
        catch
        {
            return [];
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        var startupSmoke = e.Args.Contains("--startup-smoke", StringComparer.OrdinalIgnoreCase);
        var recoverySmoke = e.Args.Contains("--service-recovery-smoke", StringComparer.OrdinalIgnoreCase);
        _singleInstanceSmoke = e.Args.Contains("--single-instance-smoke", StringComparer.OrdinalIgnoreCase);
        if (startupSmoke)
            _smokeResultPath = GetValidatedSmokeResultPath();
        LogStartup("Desktop startup entered.");
        _singleInstance = new Mutex(true, InstanceMutexName, out var created);
        if (!created)
        {
            SignalExistingInstance();
            Shutdown();
            return;
        }
        CreateActivationChannel();
        try
        {
            base.OnStartup(e);
            LogStartup("Constructing the main window.");
            MainWindow = new MainWindow();
            MainWindow.SourceInitialized += (_, _) =>
                LogStartup($"Main window source initialized (handle={new WindowInteropHelper(MainWindow).Handle != IntPtr.Zero}).");
            MainWindow.Loaded += (_, _) =>
                LogStartup($"Main window loaded (visible={MainWindow.IsVisible}, state={MainWindow.WindowState}).");
            MainWindow.ContentRendered += (_, _) =>
                LogStartup($"Main window content rendered (visible={MainWindow.IsVisible}, active={MainWindow.IsActive}).");
            MainWindow.IsVisibleChanged += (_, _) =>
                LogStartup($"Main window visibility changed (visible={MainWindow.IsVisible}).");
            MainWindow.Activated += (_, _) => LogStartup("Main window activated.");
            MainWindow.Closed += (_, _) => LogStartup("Main window closed.");
            if (startupSmoke && recoverySmoke)
            {
                EventHandler? recoveryHandler = null;
                recoveryHandler = async (_, _) =>
                {
                    MainWindow.ContentRendered -= recoveryHandler;
                    try
                    {
                        await ((CallBridge.Desktop.MainWindow)MainWindow).RunLocalServiceRecoverySmokeAsync();
                        LogStartup("Local service recovery smoke test passed.");
                        if (_singleInstanceSmoke)
                            LogStartup("Single-instance smoke primary ready.");
                        else
                        {
                            LogStartup("Startup smoke test rendered successfully; requesting clean shutdown.");
                            MainWindow.Close();
                        }
                    }
                    catch (Exception ex)
                    {
                        LogStartup($"Local service recovery smoke test failed: {ex.GetType().Name}");
                        MainWindow.Close();
                        Shutdown(1);
                    }
                };
                MainWindow.ContentRendered += recoveryHandler;
            }
            else if (startupSmoke && _singleInstanceSmoke)
            {
                MainWindow.ContentRendered += (_, _) => LogStartup("Single-instance smoke primary ready.");
            }
            else if (startupSmoke)
            {
                MainWindow.ContentRendered += (_, _) =>
                {
                    LogStartup("Startup smoke test rendered successfully; requesting clean shutdown.");
                    MainWindow.Close();
                };
            }
            LogStartup("Main window constructed.");
            var mainWindow = (CallBridge.Desktop.MainWindow)MainWindow;
            mainWindow.CloseToTray = !startupSmoke;
            SessionEnding += (_, _) => mainWindow.RequestExit();
            // Only the sign-in launch (--startup) starts hidden; a user opening CallBridge expects its window.
            var launchedAtSignIn = e.Args.Contains("--startup", StringComparer.OrdinalIgnoreCase);
            if (startupSmoke || !launchedAtSignIn || !mainWindow.IsPhoneConfigured)
            {
                MainWindow.Show();
                LogStartup("Main window shown.");
            }
            else
            {
                new WindowInteropHelper(MainWindow).EnsureHandle();
                mainWindow.StartInBackground();
                LogStartup("Main window started in the notification area.");
            }
            if (Interlocked.Exchange(ref _activationPending, 0) != 0) ActivateMainWindow();
        }
        catch (Exception ex)
        {
            LogStartup($"Desktop startup failed: {ex}");
            MessageBox.Show($"CallBridge could not start.\n\n{ex.Message}", "CallBridge", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        LogStartup("Desktop application exited.");
        _activationWait?.Unregister(null);
        _activationEvent?.Dispose();
        try { _singleInstance?.ReleaseMutex(); } catch (ApplicationException) { }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void CreateActivationChannel()
    {
        try
        {
            _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
            _activationWait = ThreadPool.RegisterWaitForSingleObject(
                _activationEvent,
                (_, timedOut) =>
                {
                    if (timedOut) return;
                    Interlocked.Exchange(ref _activationPending, 1);
                    if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(ActivateMainWindow);
                },
                null,
                Timeout.Infinite,
                false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            LogStartup($"Single-instance activation channel warning: {ex.GetType().Name}");
        }
    }

    private static void SignalExistingInstance()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var activationEvent = EventWaitHandle.OpenExisting(ActivationEventName);
                activationEvent.Set();
                break;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException)
            {
                break;
            }
        }

        using var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName(current.ProcessName))
        {
            using (process)
            {
                if (process.Id == current.Id || process.MainWindowHandle == IntPtr.Zero) continue;
                ShowWindowAsync(process.MainWindowHandle, SwRestore);
                SetForegroundWindow(process.MainWindowHandle);
                break;
            }
        }
    }

    private void ActivateMainWindow()
    {
        if (MainWindow is null || new WindowInteropHelper(MainWindow).Handle == IntPtr.Zero)
        {
            Interlocked.Exchange(ref _activationPending, 1);
            return;
        }

        Interlocked.Exchange(ref _activationPending, 0);
        if (MainWindow.WindowState == WindowState.Minimized) MainWindow.WindowState = WindowState.Normal;
        MainWindow.Show();
        var handle = new WindowInteropHelper(MainWindow).Handle;
        if (handle != IntPtr.Zero)
        {
            ShowWindowAsync(handle, SwRestore);
            SetForegroundWindow(handle);
        }
        MainWindow.Activate();
        LogStartup("Existing CallBridge window activation requested.");

        if (_singleInstanceSmoke)
        {
            LogStartup("Single-instance activation smoke test passed.");
            LogStartup("Startup smoke test rendered successfully; requesting clean shutdown.");
            MainWindow.Close();
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr windowHandle, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    private static string? GetValidatedSmokeResultPath()
    {
        var configured = Environment.GetEnvironmentVariable("CALLBRIDGE_STARTUP_SMOKE_RESULT");
        if (string.IsNullOrWhiteSpace(configured)) return null;

        try
        {
            var fullPath = Path.GetFullPath(configured);
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)) return null;
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, "");
            return fullPath;
        }
        catch
        {
            return null;
        }
    }
}
