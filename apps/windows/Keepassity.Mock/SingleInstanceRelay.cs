// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Keepassity.Mock;

/// <summary>
/// Minimal cross-process hand-off for "open this .kdbx" requests, used when a
/// second keepassity launch (Explorer double-click / "Open with") should switch
/// the already-running instance instead of doing nothing.
///
/// The running instance owns a tiny hidden top-level window whose title is
/// derived from the executable path; the second instance finds it with
/// FindWindow, verifies the owning process is the same application, and sends
/// the database path with WM_COPYDATA. The receiver validates the payload
/// (signature, size, sender process, existing .kdbx path) before handing the
/// path to the UI. This is the classic Win32 single-instance pattern and needs
/// no new dependency; an in-place activation (AppInstance redirection) would
/// require restructuring the app's launch path and is deliberately not used.
/// </summary>
internal sealed class SingleInstanceRelay : IDisposable
{
    private const int WmCopyData = 0x004A;
    private const int GwlWndProc = -4;
    private const uint Magic = 0x4B504458; // 'KPDX'
    private const uint WsPopup = 0x80000000;
    private const uint WsExToolWindow = 0x00000080;
    private const uint SmtoBlock = 0x0001;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint SendTimeoutMilliseconds = 2000;

    /// <summary>Generous upper bound for a Windows path in UTF-16 bytes.</summary>
    internal const int MaxPayloadBytes = 64 * 1024;

    /// <summary>Maximum path length Windows itself accepts (32767 characters).</summary>
    internal const int MaxPathCharacters = 32767;

    private static readonly Lazy<string> LazyRelayWindowTitle = new(() =>
    {
        string executable = Environment.ProcessPath ?? "keepassity";
        byte[] hash = SHA256.HashData(
            Encoding.UTF8.GetBytes(executable.ToLowerInvariant()));
        // Deliberately does not contain "keepassity": the helper windows must not
        // be mistaken for the main window by the activation fallback.
        return "kdbx-open-relay-" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
    });

    private readonly Action<string> _onDatabasePath;
    // The window procedure holds a raw pointer to this delegate; keep it rooted.
    private readonly SubclassProc _windowProc;
    private IntPtr _window;
    private IntPtr _previousProc;
    private bool _disposed;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr SubclassProc(IntPtr hWnd, uint message, IntPtr wParam,
        IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyDataStruct
    {
        public IntPtr Signature;
        public int Size;
        public IntPtr Data;
    }

    private SingleInstanceRelay(Action<string> onDatabasePath)
    {
        _onDatabasePath = onDatabasePath;
        _windowProc = WindowProc;
    }

    /// <summary>Window title the receiving instance registers for discovery.</summary>
    internal static string RelayWindowTitle => LazyRelayWindowTitle.Value;

    /// <summary>
    /// Creates the hidden relay window for this process. Returns null (and logs)
    /// when the window cannot be created; the app keeps working without IPC.
    /// </summary>
    public static SingleInstanceRelay? TryStart(Action<string> onDatabasePath)
    {
        var relay = new SingleInstanceRelay(onDatabasePath);
        try
        {
            if (relay.CreateWindow())
            {
                AppLog.Info("single-instance relay window ready");
                return relay;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("single-instance relay could not be started", ex);
        }
        relay.Dispose();
        return null;
    }

    /// <summary>
    /// Sends an open request to the running instance for the same executable.
    /// Returns true only when the request was delivered and accepted.
    /// </summary>
    internal static bool TryForward(string databasePath, out string outcome)
    {
        outcome = "not sent";
        if (!TryNormalizeDatabasePath(databasePath, out string fullPath))
        {
            outcome = "the database path is invalid";
            return false;
        }
        IntPtr target = FindWindow(null, RelayWindowTitle);
        if (target == IntPtr.Zero)
        {
            outcome = "no running instance answered";
            return false;
        }
        _ = GetWindowThreadProcessId(target, out uint targetProcessId);
        if (targetProcessId == 0 || targetProcessId == (uint)Environment.ProcessId)
        {
            outcome = "the relay window has an unexpected owner";
            return false;
        }
        if (!IsSameApplication(targetProcessId))
        {
            outcome = "the relay window belongs to another application";
            return false;
        }
        byte[]? payload = EncodePath(fullPath);
        if (payload is null)
        {
            outcome = "the database path is too long";
            return false;
        }
        IntPtr buffer = Marshal.AllocHGlobal(payload.Length);
        IntPtr senderWindow = IntPtr.Zero;
        try
        {
            Marshal.Copy(payload, 0, buffer, payload.Length);
            // WM_COPYDATA expects wParam to identify a window owned by the
            // sending process. A hidden STATIC window is enough for that; the
            // receiver only uses it to check the sender process.
            senderWindow = CreateWindowEx(WsExToolWindow, "STATIC",
                "kdbx-open-sender", WsPopup, 0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
            if (senderWindow == IntPtr.Zero)
            {
                outcome = "the sender window could not be created";
                return false;
            }
            var data = new CopyDataStruct
            {
                Signature = new IntPtr(Magic),
                Size = payload.Length,
                Data = buffer,
            };
            IntPtr structPointer = Marshal.AllocHGlobal(Marshal.SizeOf<CopyDataStruct>());
            try
            {
                Marshal.StructureToPtr(data, structPointer, false);
                IntPtr sent = SendMessageTimeout(target, WmCopyData, senderWindow,
                    structPointer, SmtoBlock | SmtoAbortIfHung, SendTimeoutMilliseconds,
                    out IntPtr messageResult);
                if (sent == IntPtr.Zero)
                {
                    outcome = $"the receiver did not answer (error {Marshal.GetLastPInvokeError()})";
                    return false;
                }
                if (messageResult == IntPtr.Zero)
                {
                    outcome = "the running instance rejected the request";
                    return false;
                }
                outcome = "the running instance received the path";
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(structPointer);
            }
        }
        finally
        {
            if (senderWindow != IntPtr.Zero)
            {
                _ = DestroyWindow(senderWindow);
            }
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Encodes a path as UTF-16LE bytes, or null when it is unusable.</summary>
    internal static byte[]? EncodePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
        if (fullPath.Length > MaxPathCharacters)
        {
            return null;
        }
        byte[] payload = Encoding.Unicode.GetBytes(fullPath);
        return payload.Length is > 0 and <= MaxPayloadBytes ? payload : null;
    }

    /// <summary>Decodes a UTF-16LE payload, or null when it is malformed.</summary>
    internal static string? DecodePath(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0 || payload.Length > MaxPayloadBytes ||
            payload.Length % 2 != 0)
        {
            return null;
        }
        string decoded = Encoding.Unicode.GetString(payload);
        return decoded.IndexOf('\0') >= 0 ? null : decoded;
    }

    /// <summary>
    /// Validates a candidate path for the open hand-off: absolute path, at most
    /// the Windows path limit, an existing .kdbx file.
    /// </summary>
    internal static bool TryNormalizeDatabasePath(string? candidate, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate) ||
            candidate.Length > MaxPathCharacters ||
            candidate.IndexOf('\0') >= 0)
        {
            return false;
        }
        try
        {
            fullPath = Path.GetFullPath(candidate);
        }
        catch
        {
            return false;
        }
        return fullPath.EndsWith(".kdbx", StringComparison.OrdinalIgnoreCase) &&
               File.Exists(fullPath);
    }

    private bool CreateWindow()
    {
        _window = CreateWindowEx(WsExToolWindow, "STATIC", RelayWindowTitle, WsPopup,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (_window == IntPtr.Zero)
        {
            AppLog.Warn("single-instance relay: hidden window could not be created " +
                        $"(error {Marshal.GetLastPInvokeError()})");
            return false;
        }
        Marshal.SetLastPInvokeError(0);
        _previousProc = SetWindowLongPtr(_window, GwlWndProc,
            Marshal.GetFunctionPointerForDelegate(_windowProc));
        int error = Marshal.GetLastPInvokeError();
        if (_previousProc == IntPtr.Zero && error != 0)
        {
            AppLog.Warn($"single-instance relay: window procedure hook failed (error {error})");
            _ = DestroyWindow(_window);
            _window = IntPtr.Zero;
            return false;
        }
        return true;
    }

    private IntPtr WindowProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (!_disposed && message == WmCopyData)
            {
                if (TryReadOpenRequest(wParam, lParam, out string? databasePath) &&
                    databasePath is not null)
                {
                    AppLog.Info("open request received from another instance");
                    _onDatabasePath(databasePath);
                    return new IntPtr(1);
                }
                return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            // A throwing window procedure would take down the process.
            AppLog.Error("single-instance relay: request handling failed", ex);
        }
        return CallWindowProc(_previousProc, hWnd, message, wParam, lParam);
    }

    private static bool TryReadOpenRequest(IntPtr senderWindow, IntPtr lParam,
        out string? databasePath)
    {
        databasePath = null;
        if (senderWindow == IntPtr.Zero || lParam == IntPtr.Zero)
        {
            Reject("the sender window is missing");
            return false;
        }
        _ = GetWindowThreadProcessId(senderWindow, out uint senderProcessId);
        if (senderProcessId == 0 || senderProcessId == (uint)Environment.ProcessId)
        {
            Reject("the sender process is invalid");
            return false;
        }
        if (!IsSameApplication(senderProcessId))
        {
            Reject("the sender process is not keepassity");
            return false;
        }
        CopyDataStruct data = Marshal.PtrToStructure<CopyDataStruct>(lParam);
        if (data.Signature != new IntPtr(Magic))
        {
            Reject("the payload signature is wrong");
            return false;
        }
        if (data.Data == IntPtr.Zero || data.Size <= 0 ||
            data.Size > MaxPayloadBytes || data.Size % 2 != 0)
        {
            Reject("the payload size is invalid");
            return false;
        }
        byte[] payload = new byte[data.Size];
        Marshal.Copy(data.Data, payload, 0, payload.Length);
        string? decoded = DecodePath(payload);
        if (decoded is null ||
            !TryNormalizeDatabasePath(decoded, out string fullPath))
        {
            Reject("the payload is not an existing .kdbx path");
            return false;
        }
        databasePath = fullPath;
        return true;
    }

    private static void Reject(string reason)
    {
        AppLog.Warn($"single-instance relay: ignored an open request ({reason})");
    }

    private static bool IsSameApplication(uint processId)
    {
        string? expectedName = Path.GetFileName(Environment.ProcessPath);
        if (string.IsNullOrEmpty(expectedName))
        {
            return true;
        }
        try
        {
            using Process process = Process.GetProcessById((int)processId);
            string actualName = process.ProcessName + ".exe";
            return string.Equals(actualName, expectedName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // The process may have exited or the sandbox may deny the query; the
            // payload itself is still validated, so do not drop a valid request.
            AppLog.Warn("single-instance relay: sender process name could not be checked");
            return true;
        }
    }

    private static IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value)
        => IntPtr.Size == 8
            ? SetWindowLongPtr64(window, index, value)
            : new IntPtr(SetWindowLong32(window, index, value.ToInt32()));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            if (_window != IntPtr.Zero)
            {
                if (_previousProc != IntPtr.Zero)
                {
                    _ = SetWindowLongPtr(_window, GwlWndProc, _previousProc);
                    _previousProc = IntPtr.Zero;
                }
                _ = DestroyWindow(_window);
                _window = IntPtr.Zero;
                AppLog.Info("single-instance relay window closed");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("single-instance relay cleanup failed", ex);
        }
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr window, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallWindowProc(IntPtr previousProc, IntPtr window,
        uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className,
        string windowName, uint style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message,
        IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
