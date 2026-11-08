// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Keepassity.CoreTests;

/// <summary>Result reporter shared by the console harness and the test batches.</summary>
internal delegate void TestCheck(string name, bool ok, string detail = "");

/// <summary>
/// Locates the repository fixtures and the native FFI library. Both are resolved
/// from environment variables first and then by walking up from the current and
/// assembly directories, so the harness works when `dotnet run` is launched from
/// the repository root (CI) and from the workspace root (local).
/// </summary>
internal static class TestEnvironment
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string FixtureDir { get; } = FindFixtureDir();

    public static string Fixture(string name) => Path.Combine(FixtureDir, name);

    private static string FindRepoRoot()
    {
        string? configured = Environment.GetEnvironmentVariable("KEEPASSITY_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            string? current = Path.GetFullPath(start);
            while (current is not null)
            {
                if (Directory.Exists(Path.Combine(current, "tests", "fixtures", "keepass-rs")) &&
                    Directory.Exists(Path.Combine(current, "crates", "ffi")))
                {
                    return current;
                }
                current = Path.GetDirectoryName(current);
            }
        }
        throw new DirectoryNotFoundException(
            "Repository root not found; set KEEPASSITY_REPO_ROOT to the repo/ checkout.");
    }

    private static string FindFixtureDir()
    {
        string? configured = Environment.GetEnvironmentVariable("KEEPASSITY_FIXTURES_DIR");
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }
        return Path.Combine(RepoRoot, "tests", "fixtures", "keepass-rs");
    }

    /// <summary>Finds keepassity_ffi.dll or libkeepassity_ffi.so if it was built.</summary>
    public static string? FindFfiLibrary()
    {
        string fileName = OperatingSystem.IsWindows()
            ? "keepassity_ffi.dll"
            : "libkeepassity_ffi.so";
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("KEEPASSITY_FFI_DLL"),
            Path.Combine(AppContext.BaseDirectory, fileName),
            Path.Combine(RepoRoot, "target", "release", fileName),
            Path.GetFullPath(Path.Combine(
                RepoRoot, "..", "out", "cargo-target", "release", fileName)),
        };
        foreach (string? candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }
        return null;
    }
}

/// <summary>
/// Minimal P/Invoke to the keepassity FFI library. The WinUI restore path
/// validates a backup with CoreClient.OpenAsync (kp_open + the current master
/// password) before calling BackupService.RestoreBytes; the headless harness
/// cannot reference the WinUI project, so it uses the same C ABI primitive to
/// reproduce that validation guard.
/// </summary>
internal static class CoreValidation
{
    private const string Library = "keepassity_ffi";

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint kp_api_version();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr kp_open(byte[] kdbx, UIntPtr len, byte[] optsJson);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int kp_close(IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr kp_last_error_json();

    private static readonly object Gate = new();
    private static bool _resolved;
    private static string? _loadedPath;

    static CoreValidation()
    {
        NativeLibrary.SetDllImportResolver(typeof(CoreValidation).Assembly, Resolve);
    }

    /// <summary>Path of the loaded native library, or null when it cannot be found.</summary>
    public static string? LibraryPath
    {
        get
        {
            lock (Gate)
            {
                if (!_resolved)
                {
                    _resolved = true;
                    _loadedPath = TestEnvironment.FindFfiLibrary();
                }
                return _loadedPath;
            }
        }
    }

    private static IntPtr Resolve(string name, System.Reflection.Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        if (name != Library)
        {
            return IntPtr.Zero;
        }
        string? path = TestEnvironment.FindFfiLibrary();
        if (path is null)
        {
            throw new DllNotFoundException(
                "keepassity_ffi was not found. Build it first: " +
                "cargo build -p keepassity-ffi --release");
        }
        return NativeLibrary.Load(path);
    }

    /// <summary>
    /// True when the KDBX bytes decrypt with the given password. This is the same
    /// check the app performs on a selected backup before restoring it.
    /// </summary>
    public static bool OpensWithPassword(byte[] kdbx, string password)
    {
        if (LibraryPath is null)
        {
            throw new DllNotFoundException(
                "keepassity_ffi was not found. Build it first: " +
                "cargo build -p keepassity-ffi --release");
        }
        if (kp_api_version() != 1)
        {
            throw new InvalidOperationException("Unsupported keepassity FFI API version.");
        }
        byte[] options = Utf8Z(JsonSerializer.Serialize(new { password }));
        IntPtr handle = kp_open(kdbx, (UIntPtr)kdbx.LongLength, options);
        if (handle == IntPtr.Zero)
        {
            return false;
        }
        _ = kp_close(handle);
        return true;
    }

    /// <summary>Last FFI error JSON; used only for diagnostic messages.</summary>
    public static string LastError()
    {
        IntPtr pointer = kp_last_error_json();
        return pointer == IntPtr.Zero ? "(no error)" : Marshal.PtrToStringUTF8(pointer) ?? "(null)";
    }

    private static byte[] Utf8Z(string value) => Encoding.UTF8.GetBytes(value + "\0");
}
