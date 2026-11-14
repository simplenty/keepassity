// SPDX-License-Identifier: GPL-3.0-or-later
// End-to-end check of the WebDAV open path with the real native core and a real
// (loopback) WebDAV server: the downloaded bytes must decrypt before they are
// allowed to replace the working copy.
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Keepassity.Core;

namespace Keepassity.CoreTests;

internal static class RemoteVaultOpenIntegrationTests
{
    public static void Run(TestCheck check, string dataDirectory)
    {
        if (TestEnvironment.FindFfiLibrary() is null)
        {
            Console.WriteLine("SKIP  remote open integration (native FFI not built)");
            return;
        }
        // Touch the resolver owner first so the assembly-wide DllImport resolver
        // is registered before this file's own P/Invokes are used.
        _ = CoreValidation.LibraryPath;

        string name = "remote-integration-" + Guid.NewGuid().ToString("N")[..8];
        string folder = Path.Combine(dataDirectory, name);
        Directory.CreateDirectory(folder);
        byte[] fixture = File.ReadAllBytes(TestEnvironment.Fixture("test_db_few_rounds.kdbx"));
        using var server = new WebDavMockServer();
        const string target = "/dav/open-integration.kdbx";
        server.PutResource(target, fixture, "\"e1\"", DateTimeOffset.UtcNow);
        string url = $"http://127.0.0.1:{server.Port}{target}";
        var states = new SyncStateStore(Path.Combine(folder, "state"));
        var opener = new RemoteVaultOpener(states);
        using var remote = new WebDavClient(url, "user", "pass");
        PendingRemoteDatabase pending = opener.DownloadAsync(remote, url, "user")
            .GetAwaiter().GetResult();
        check("remote open downloads the fixture", pending.Bytes.SequenceEqual(fixture));

        // Put a distinguishable previous working copy in place so "untouched"
        // means untouched, not "absent".
        string workingCopy = pending.WorkingCopyPath;
        byte[] oldBytes = { 1, 2, 3 };
        Directory.CreateDirectory(Path.GetDirectoryName(workingCopy)!);
        File.WriteAllBytes(workingCopy, oldBytes);

        using var core = new RealCoreClient();
        bool rejected = false;
        try
        {
            opener.OpenAsync(pending, core, "not-the-password").GetAwaiter().GetResult();
        }
        catch (CoreException ex)
        {
            rejected = ex.Code == "BadPassword";
        }
        check("remote open rejects a wrong master password",
            rejected && !core.IsOpen,
            rejected ? "BadPassword" : "no BadPassword");
        check("remote open leaves the working copy untouched after a wrong password",
            File.ReadAllBytes(workingCopy).SequenceEqual(oldBytes));

        opener.OpenAsync(pending, core, "demopass").GetAwaiter().GetResult();
        SyncState state = states.Load(url, "user", workingCopy);
        check("remote open promotes bytes that really decrypt with the password",
            core.IsOpen && File.ReadAllBytes(workingCopy).SequenceEqual(fixture));
        check("remote open records the real synced baseline",
            !state.LocalDirty && state.Etag == "\"e1\"" &&
            state.Sha256 == Sha(fixture) && state.Size == fixture.Length,
            state.Sha256 ?? "(none)");
        check("remote open backs up the previous working copy",
            BackupService.ListBackups(workingCopy).Count == 1 &&
            File.ReadAllBytes(BackupService.ListBackups(workingCopy)[0].Path)
                .SequenceEqual(oldBytes));
    }

    private static string Sha(byte[] bytes)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
            .ToLowerInvariant();

    /// <summary>Minimal live core handle for the integration check.</summary>
    private sealed class RealCoreClient : ICoreClient
    {
        private const string Library = "keepassity_ffi";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        private IntPtr _handle;

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint kp_api_version();

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr kp_open(byte[] kdbx, UIntPtr len, byte[] optsJson);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int kp_lock(IntPtr handle);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int kp_close(IntPtr handle);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr kp_last_error_json();

        public bool IsOpen => _handle != IntPtr.Zero;

        public string? DatabasePath { get; private set; }

        public Task OpenAsync(string path, string password)
        {
            byte[] bytes = File.ReadAllBytes(path);
            return OpenBytesAsync(bytes, password, path);
        }

        public Task OpenBytesAsync(byte[] kdbx, string password, string? databasePath = null)
        {
            if (kp_api_version() != 1)
            {
                throw new CoreException("VersionMismatch", "unsupported FFI version");
            }
            if (_handle != IntPtr.Zero)
            {
                _ = kp_close(_handle);
                _handle = IntPtr.Zero;
            }
            byte[] options = Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new { password }) + "\0");
            IntPtr handle = kp_open(kdbx, (UIntPtr)kdbx.LongLength, options);
            if (handle == IntPtr.Zero)
            {
                throw ParseError("open database");
            }
            _handle = handle;
            DatabasePath = databasePath;
            return Task.CompletedTask;
        }

        public Task LockAsync()
        {
            if (_handle != IntPtr.Zero && kp_lock(_handle) != 0)
            {
                throw ParseError("lock database");
            }
            return Task.CompletedTask;
        }

        public Task<byte[]> SaveBytesAsync() => throw new NotSupportedException();

        public Task SaveAsync(string path, int backupRetention = 20,
            Func<bool>? databaseChanged = null)
            => throw new NotSupportedException();

        public Task VerifyAsync(byte[] bytes, string password)
            => throw new NotSupportedException();

        public Task<SyncDecisionDto> SyncDecideAsync(string remoteMetaJson, bool localDirty)
            => throw new NotSupportedException();

        public Task SyncCommitAsync(string? etag, string? sha256)
            => throw new NotSupportedException();

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                _ = kp_close(_handle);
                _handle = IntPtr.Zero;
            }
        }

        private static CoreException ParseError(string operation)
        {
            try
            {
                IntPtr pointer = kp_last_error_json();
                string json = pointer == IntPtr.Zero
                    ? "{}"
                    : Marshal.PtrToStringUTF8(pointer) ?? "{}";
                ErrorDto? error = JsonSerializer.Deserialize<ErrorDto>(json, JsonOptions);
                return new CoreException(error?.Code ?? "Internal",
                    error?.Message ?? $"{operation} failed.");
            }
            catch (JsonException)
            {
                return new CoreException("Internal", $"{operation} failed.");
            }
        }
    }
}
