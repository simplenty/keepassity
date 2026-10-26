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
        InitializeComponent();
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
                ?? Path.Combine(Path.GetTempPath(), "keepassity-selftest.log");
            ThreadPool.QueueUserWorkItem(_ =>
                Environment.Exit(AppSelfTest.Run(database, report)));
            return;
        }

        bool allowMultiple = commandLine
            .Any(argument => argument.Equals("--allow-multiple", StringComparison.OrdinalIgnoreCase));
        if (!allowMultiple)
        {
            bool createdNew;
            _instanceMutex = new Mutex(true, @"Local\keepassity-windows-app", out createdNew);
            if (!createdNew)
            {
                ActivateExistingInstance();
                Environment.Exit(0);
                return;
            }
        }

        _window = new MainWindow();
        _window.Activate();
    }

    private static void ActivateExistingInstance()
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
                return;
            }
            Thread.Sleep(200);
        }
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

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
}
