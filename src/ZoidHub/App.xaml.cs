using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using ZoidHub.Services;

namespace ZoidHub;

public partial class App : Application
{
    // Global, not per-user-session, on purpose - "Global\" prefixed mutex names are visible across
    // all sessions on the machine (RDP/fast-user-switching), which is the stricter, safer default
    // for "is another copy of this already running" - a per-session-only check could still let two
    // instances race for the same %LocalAppData%\ZoidHub\Payload folder.
    private const string SingleInstanceMutexName = "Global\\ZoidHub_SingleInstance_9F3A2C7E";
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppLogger.Log("ZoidHub started");

        DispatcherUnhandledException += (_, args) =>
        {
            AppLogger.Log($"UNHANDLED UI EXCEPTION: {args.Exception}");
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            AppLogger.Log($"UNHANDLED EXCEPTION: {args.ExceptionObject}");
        };

        // Confirmed hit for real during dev testing (not hypothetical) - launching a second
        // instance while one was already running raced both over %LocalAppData%\ZoidHub\Payload
        // and surfaced as a confusing "WebView2 couldn't start... Runtime isn't installed" error,
        // which has nothing to do with the actual cause. Catch it here, before any of that setup
        // even runs, and hand off to the real instance instead.
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            AppLogger.Log("ZoidHub: another instance is already running - activating it and exiting.");
            ActivateExistingInstance();
            Shutdown();
        }
    }

    private static void ActivateExistingInstance()
    {
        var currentPid = Environment.ProcessId;
        foreach (var proc in Process.GetProcessesByName("ZoidHub"))
        {
            if (proc.Id == currentPid) continue;
            var hwnd = proc.MainWindowHandle;
            if (hwnd == IntPtr.Zero) continue;

            if (NativeMethods.IsIconic(hwnd)) NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
            NativeMethods.SetForegroundWindow(hwnd);
            return;
        }

        // Couldn't find/activate a window (e.g. the other instance is still starting up and
        // hasn't created its main window yet) - a message beats silently doing nothing, since
        // otherwise this launch just vanishes with no visible feedback at all.
        MessageBox.Show(
            "ZoidHub is already running.",
            "ZoidHub", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // ReleaseMutex throws if this instance never actually owned it - true whenever this was
        // the "another instance is already running" branch above (opening an existing mutex never
        // grants ownership, regardless of the initiallyOwned constructor argument). Not worth a
        // dedicated bool field just to avoid a harmless-on-exit exception; try/catch is simpler.
        try { _singleInstanceMutex?.ReleaseMutex(); } catch (ApplicationException) { /* never owned it */ }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static class NativeMethods
    {
        public const int SW_RESTORE = 9;

        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
