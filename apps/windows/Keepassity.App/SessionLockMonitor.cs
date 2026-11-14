// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;

namespace Keepassity.App;

/// <summary>
/// Watches the main window for the Windows session/power messages that must
/// lock the vault: workstation lock (Win+L), logoff, session end and system
/// suspend. Implemented with a plain window-procedure subclass so it works
/// without any extra NuGet package; the owner receives a short reason string
/// on the UI thread and is responsible for locking the vault.
/// </summary>
internal sealed class SessionLockMonitor : IDisposable
{
    private const int WM_ENDSESSION = 0x0016;
    private const int WM_POWERBROADCAST = 0x0218;
    private const int WM_WTSSESSION_CHANGE = 0x02B1;
    private const int PBT_APMSUSPEND = 0x4;
    private const int WTS_SESSION_LOGOFF = 0x6;
    private const int WTS_SESSION_LOCK = 0x7;
    private const uint NOTIFY_FOR_THIS_SESSION = 0;
    private const int GWLP_WNDPROC = -4;

    private readonly IntPtr _window;
    private readonly Action<string> _onLockRequested;
    // The native window procedure holds a raw pointer to this delegate, so it
    // must stay rooted for as long as the subclass is installed.
    private readonly SubclassProc _subclassProc;
    private IntPtr _previousProc;
    private bool _wtsRegistered;
    private bool _installed;
    private bool _disposed;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr SubclassProc(IntPtr hWnd, uint message, IntPtr wParam,
        IntPtr lParam);

    private SessionLockMonitor(IntPtr window, Action<string> onLockRequested)
    {
        _window = window;
        _onLockRequested = onLockRequested;
        _subclassProc = WindowProc;
    }

    /// <summary>
    /// Installs the monitor on the given window handle. Returns null (and logs)
    /// when the hook cannot be installed; callers must keep working unlocked
    /// rather than fail to start.
    /// </summary>
    public static SessionLockMonitor? TryInstall(IntPtr window, Action<string> onLockRequested)
    {
        if (window == IntPtr.Zero)
        {
            AppLog.Warn("session lock monitor: window handle is not available yet");
            return null;
        }
        try
        {
            var monitor = new SessionLockMonitor(window, onLockRequested);
            monitor.Install();
            return monitor;
        }
        catch (Exception ex)
        {
            AppLog.Error("session lock monitor could not be installed", ex);
            return null;
        }
    }

    private void Install()
    {
        Marshal.SetLastPInvokeError(0);
        IntPtr proc = Marshal.GetFunctionPointerForDelegate(_subclassProc);
        _previousProc = SetWindowLongPtr(_window, GWLP_WNDPROC, proc);
        int error = Marshal.GetLastPInvokeError();
        if (_previousProc == IntPtr.Zero && error != 0)
        {
            AppLog.Warn($"session lock monitor: window procedure hook failed (error {error})");
            return;
        }
        _installed = true;

        // WM_* messages are only delivered for WTS session changes after this
        // registration. Failure is not fatal: suspend/session-end still arrive.
        try
        {
            _wtsRegistered = WTSRegisterSessionNotification(_window, NOTIFY_FOR_THIS_SESSION);
            if (!_wtsRegistered)
            {
                AppLog.Warn("session lock monitor: WTS session notification registration failed " +
                            $"(error {Marshal.GetLastPInvokeError()}); only sleep/session-end " +
                            "events will lock the vault");
            }
        }
        catch (Exception ex)
        {
            _wtsRegistered = false;
            AppLog.Error("session lock monitor: WTS session notification registration failed", ex);
        }
    }

    private IntPtr WindowProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (!_disposed)
            {
                switch (message)
                {
                    case WM_WTSSESSION_CHANGE:
                        int change = wParam.ToInt32();
                        if (change == WTS_SESSION_LOCK)
                        {
                            _onLockRequested("Windows session locked");
                        }
                        else if (change == WTS_SESSION_LOGOFF)
                        {
                            _onLockRequested("Windows session logged off");
                        }
                        break;
                    case WM_POWERBROADCAST:
                        if (wParam.ToInt32() == PBT_APMSUSPEND)
                        {
                            _onLockRequested("System is suspending");
                        }
                        break;
                    case WM_ENDSESSION:
                        if (wParam != IntPtr.Zero)
                        {
                            _onLockRequested("Windows session ended");
                        }
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            // A throwing window procedure would take down the process.
            AppLog.Error("session lock monitor: event handling failed", ex);
        }
        return CallWindowProc(_previousProc, hWnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            if (_wtsRegistered)
            {
                _wtsRegistered = false;
                if (!WTSUnRegisterSessionNotification(_window))
                {
                    AppLog.Warn("session lock monitor: WTS unregister failed (error " +
                                $"{Marshal.GetLastPInvokeError()})");
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("session lock monitor: WTS unregister failed", ex);
        }
        try
        {
            if (_installed && _previousProc != IntPtr.Zero)
            {
                _installed = false;
                Marshal.SetLastPInvokeError(0);
                SetWindowLongPtr(_window, GWLP_WNDPROC, _previousProc);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("session lock monitor: window procedure unhook failed", ex);
        }
        AppLog.Info("session lock monitor removed");
    }

    private static IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value)
        => IntPtr.Size == 8
            ? SetWindowLongPtr64(window, index, value)
            : new IntPtr(SetWindowLong32(window, index, value.ToInt32()));

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr window, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallWindowProc(IntPtr previousProc, IntPtr window,
        uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("Wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSRegisterSessionNotification(IntPtr window, uint flags);

    [DllImport("Wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr window);
}
