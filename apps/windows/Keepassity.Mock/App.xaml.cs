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
        bool allowMultiple = Environment.GetCommandLineArgs()
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
                process.Refresh();
                IntPtr handle = process.MainWindowHandle;
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

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
}
