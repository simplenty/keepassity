// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Keepassity.Mock;

public partial class App : Application
{
    private static Mutex? _instanceMutex;
    private Window? _window;

    public App()
    {
        AppLog.Initialize();
        HookCrashHandlers();
        SetAppUserModelId();
        InitializeComponent();
    }

    /// <summary>
    /// Explicit AppUserModelID so the taskbar groups the window, pinned shortcuts
    /// and jump lists under the product instead of the host process. Microsoft
    /// recommends setting it for every desktop app that ships shortcuts
    /// (learn.microsoft.com/windows/win32/shell/appids).
    /// </summary>
    private static void SetAppUserModelId()
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"could not set the AppUserModelID: {ex.Message}");
        }
    }

    /// <summary>Shared with the installer/shortcuts; keep both sides in sync.</summary>
    internal const string AppUserModelId = "simplenty.keepassity";

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appUserModelId);

    private void HookCrashHandlers()
    {
        UnhandledException += (_, args) =>
        {
            AppLog.Error("unhandled XAML exception", args.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            AppLog.Error("unhandled application exception", args.ExceptionObject as Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("unobserved task exception", args.Exception);
            args.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        string[] commandLine = Environment.GetCommandLineArgs();
        int selfTestIndex = Array.IndexOf(commandLine, "--selftest");
        if (selfTestIndex >= 0)
        {
            string database = selfTestIndex + 1 < commandLine.Length
                ? commandLine[selfTestIndex + 1]
                : string.Empty;
            string report = Environment.GetEnvironmentVariable("KEEPASSITY_SELFTEST_REPORT")
                ?? Path.Combine(ApplicationPaths.Root, "selftest", "selftest.log");
            AppLog.Info($"selftest start: {database}");
            int exitCode = Task.Run(() => AppSelfTest.Run(database, report))
                .GetAwaiter().GetResult();
            AppLog.Info($"selftest end: exit {exitCode}");
            Environment.Exit(exitCode);
            return;
        }

        string? requestedDatabase = LaunchArguments.FindDatabasePath(commandLine, File.Exists);
        bool allowMultiple = TestHooks.HasSwitch("--allow-multiple");
        if (!allowMultiple)
        {
            bool createdNew = true;
            try
            {
                _instanceMutex = new Mutex(true, @"Local\keepassity-windows-app", out createdNew);
            }
            catch (Exception ex)
            {
                // A broken named-mutex object must not stop the app from
                // opening. The per-vault file lock still prevents two windows
                // from writing the same database.
                AppLog.Warn($"single-instance mutex unavailable: {ex.Message}");
            }
            if (!createdNew)
            {
                // A running instance exists. Explorer "Open with keepassity" and
                // a double-clicked .kdbx arrive as a bare path on this second
                // process: hand it to the running instance with WM_COPYDATA so
                // the database switches there instead of the launch doing
                // nothing. If the running build has no relay window, fall back to
                // just restoring its window.
                if (requestedDatabase is not null)
                {
                    if (SingleInstanceRelay.TryForward(requestedDatabase, out string outcome))
                    {
                        AppLog.Info($"forwarded an open-database request ({outcome})");
                        Environment.Exit(0);
                        return;
                    }
                    AppLog.Warn($"could not forward the open-database request ({outcome})");
                }
                if (ActivateExistingInstance())
                {
                    Environment.Exit(0);
                    return;
                }
                // The mutex belongs to a process that has no window yet (or is
                // shutting down). Starting a normal window is more useful than
                // exiting as if the launch succeeded.
                AppLog.Warn("an instance mutex exists but no window was found; " +
                            "starting a normal window");
            }
        }

        _window = new MainWindow();
        _window.Activate();
    }

    private static bool ActivateExistingInstance()
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            foreach (Process process in Process.GetProcessesByName(
                         Process.GetCurrentProcess().ProcessName))
            {
                if (process.Id == Environment.ProcessId)
                {
                    continue;
                }
                IntPtr handle = FindMainWindow(process.Id);
                if (handle == IntPtr.Zero)
                {
                    continue;
                }
                ShowWindow(handle, 9 /* SW_RESTORE */);
                SetForegroundWindow(handle);
                return true;
            }
            Thread.Sleep(200);
        }
        return false;
    }

    private static IntPtr FindMainWindow(int processId)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint owner);
            if (owner != processId || GetWindow(window, 4 /* GW_OWNER */) != IntPtr.Zero)
            {
                return true;
            }
            // Skip the hidden STATIC helper windows used for the single-instance
            // WM_COPYDATA relay/activation path; they are not the main window.
            var className = new System.Text.StringBuilder(64);
            _ = GetClassName(window, className, className.Capacity);
            if (className.ToString().Equals("STATIC", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            var title = new System.Text.StringBuilder(256);
            _ = GetWindowText(window, title, title.Capacity);
            if (title.ToString().Contains("keepassity", StringComparison.OrdinalIgnoreCase))
            {
                found = window;
                return false;
            }
            if (found == IntPtr.Zero)
            {
                found = window;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, System.Text.StringBuilder className,
        int count);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
}
