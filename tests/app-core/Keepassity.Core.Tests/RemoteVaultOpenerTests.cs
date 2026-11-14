// SPDX-License-Identifier: GPL-3.0-or-later
using Keepassity.Core;

namespace Keepassity.CoreTests;

/// <summary>
/// The WebDAV open path must never let unverified bytes touch the working copy.
/// These checks pin the rule down: download only holds memory, the master
/// password opens the bytes first, and only then is the working copy written
/// durably with a backup and a clean sync baseline.
/// </summary>
internal static class RemoteVaultOpenerTests
{
    private const string User = "user";
    private const string Password = "master-password";

    private static string UrlFor(string name) => $"https://example.test/open-{name}.kdbx";
    public static void Run(TestCheck check, string dataDirectory)
    {
        DownloadHoldsBytesInMemory(check, dataDirectory);
        MissingRemoteIsReported(check, dataDirectory);
        OpenPromotesOnlyAfterDecrypting(check, dataDirectory);
        WrongPasswordLeavesDiskUntouched(check, dataDirectory);
        WriteFailureLocksTheCore(check, dataDirectory);
    }

    private static void DownloadHoldsBytesInMemory(TestCheck check, string dataDirectory)
    {
        (string folder, SyncStateStore states) = NewEnvironment(dataDirectory, "download");
        string url = UrlFor("download");
        var remote = new FakeRemote { Bytes = Bytes(1, 2, 3, 4) };
        var opener = new RemoteVaultOpener(states);

        PendingRemoteDatabase pending = opener.DownloadAsync(remote, url, User, 5)
            .GetAwaiter().GetResult();

        check("download keeps the remote bytes pending",
            pending.Bytes.SequenceEqual(Bytes(1, 2, 3, 4)) &&
            pending.Metadata.ETag == "\"e1\"" &&
            pending.Url == url && pending.Username == User &&
            pending.BackupRetention == 5);
        check("download does not write the working copy",
            pending.WorkingCopyPath == RemoteDatabaseStore.PathFor(url, User) &&
            !File.Exists(pending.WorkingCopyPath));
        check("download does not write a sync baseline",
            states.Load(url, User, pending.WorkingCopyPath).Sha256 is null);
        check("download leaves no temporary file behind",
            Directory.GetFiles(folder).Length == 0 &&
            Directory.GetDirectories(folder).Length == 1,
            string.Join("; ", Directory.GetFileSystemEntries(folder)));
    }

    private static void MissingRemoteIsReported(TestCheck check, string dataDirectory)
    {
        (_, SyncStateStore states) = NewEnvironment(dataDirectory, "missing");
        string url = UrlFor("missing");
        var remote = new FakeRemote { Missing = true };
        var opener = new RemoteVaultOpener(states);

        WebDavException? error = null;
        try
        {
            opener.DownloadAsync(remote, url, User).GetAwaiter().GetResult();
        }
        catch (WebDavException ex)
        {
            error = ex;
        }

        check("a missing remote file is reported as not found",
            error is not null && error.StatusCode == 404,
            error?.StatusCode.ToString() ?? "no exception");
    }

    private static void OpenPromotesOnlyAfterDecrypting(TestCheck check, string dataDirectory)
    {
        (_, SyncStateStore states) = NewEnvironment(dataDirectory, "promote");
        string url = UrlFor("promote");
        byte[] oldBytes = Bytes(9, 9, 9);
        byte[] remoteBytes = Bytes(5, 6, 7, 8);
        string workingCopy = RemoteDatabaseStore.PathFor(url, User);
        Directory.CreateDirectory(Path.GetDirectoryName(workingCopy)!);
        File.WriteAllBytes(workingCopy, oldBytes);
        states.Save(url, User, workingCopy, new SyncState
        {
            Etag = "\"old\"",
            Sha256 = new string('a', 64),
            LocalDirty = true,
        });
        var remote = new FakeRemote { Bytes = remoteBytes };
        var core = new FakeCore();
        // Capture what is on disk at the moment the bytes are opened: the open
        // must come first, the working-copy write second.
        byte[]? onDiskDuringOpen = null;
        core.OnOpenBytes = (_, _, _) =>
        {
            onDiskDuringOpen = File.ReadAllBytes(workingCopy);
            return Task.CompletedTask;
        };
        PendingRemoteDatabase pending = new RemoteVaultOpener(states)
            .DownloadAsync(remote, url, User).GetAwaiter().GetResult();
        var opener = new RemoteVaultOpener(states);

        opener.OpenAsync(pending, core, Password).GetAwaiter().GetResult();

        check("the downloaded bytes are opened before the working copy changes",
            onDiskDuringOpen is not null && onDiskDuringOpen.SequenceEqual(oldBytes),
            onDiskDuringOpen is null
                ? "open not called"
                : Convert.ToHexString(onDiskDuringOpen));
        check("promotion passes the bytes, password and path to the core",
            core.OpenBytesCalls == 1 &&
            core.OpenedBytes?.SequenceEqual(remoteBytes) == true &&
            core.OpenedPassword == Password &&
            core.OpenedPath == workingCopy);
        check("promotion replaces the working copy with the verified bytes",
            File.ReadAllBytes(workingCopy).SequenceEqual(remoteBytes));
        SyncState state = states.Load(url, User, workingCopy);
        check("promotion records a clean synced baseline",
            !state.LocalDirty &&
            state.Etag == "\"e1\"" &&
            state.Sha256 == Sha(remoteBytes) &&
            state.Size == remoteBytes.Length &&
            state.RemoteModifiedUnix == 1234);
        List<BackupEntry> backups = BackupService.ListBackups(workingCopy);
        check("promotion backs up the previous working copy",
            backups.Count == 1 && File.ReadAllBytes(backups[0].Path).SequenceEqual(oldBytes),
            backups.Count.ToString());
        check("promotion leaves no temporary file behind",
            Directory.GetFiles(Path.GetDirectoryName(workingCopy)!,
                "*keepassity-tmp*").Length == 0);
    }

    private static void WrongPasswordLeavesDiskUntouched(TestCheck check, string dataDirectory)
    {
        (_, SyncStateStore states) = NewEnvironment(dataDirectory, "wrong-password");
        string url = UrlFor("wrong-password");
        byte[] oldBytes = Bytes(4, 4, 4);
        string workingCopy = RemoteDatabaseStore.PathFor(url, User);
        Directory.CreateDirectory(Path.GetDirectoryName(workingCopy)!);
        File.WriteAllBytes(workingCopy, oldBytes);
        states.Save(url, User, workingCopy, new SyncState { LocalDirty = true });
        var remote = new FakeRemote { Bytes = Bytes(1, 2) };
        var core = new FakeCore
        {
            OnOpenBytes = (_, _, _) => Task.FromException(
                new CoreException("BadPassword", "incorrect master password")),
        };
        PendingRemoteDatabase pending = new RemoteVaultOpener(states)
            .DownloadAsync(remote, url, User).GetAwaiter().GetResult();

        bool threw = false;
        try
        {
            new RemoteVaultOpener(states).OpenAsync(pending, core, "wrong")
                .GetAwaiter().GetResult();
        }
        catch (CoreException)
        {
            threw = true;
        }

        check("a wrong password fails the open", threw);
        check("a failed open leaves the working copy untouched",
            File.ReadAllBytes(workingCopy).SequenceEqual(oldBytes));
        check("a failed open leaves the previous baseline dirty",
            states.Load(url, User, workingCopy).LocalDirty);
        check("a failed open creates no backup and locks nothing new",
            BackupService.ListBackups(workingCopy).Count == 0 && core.LockCalls == 0);
    }

    private static void WriteFailureLocksTheCore(TestCheck check, string dataDirectory)
    {
        (string folder, SyncStateStore states) = NewEnvironment(dataDirectory, "write-failure");
        // A file where the working copy's parent directory should be makes the
        // durable write fail after the bytes were opened.
        string blockedParent = Path.Combine(folder, "blocked");
        File.WriteAllText(blockedParent, "not a directory");
        string workingCopy = Path.Combine(blockedParent, "vault.kdbx");
        PendingRemoteDatabase pending = new()
        {
            Url = UrlFor("write-failure"),
            Username = User,
            WorkingCopyPath = workingCopy,
            Metadata = new WebDavFileInfo { ETag = "\"e1\"", Size = 2, ModifiedUnix = 1 },
            Bytes = Bytes(1, 2),
        };
        var core = new FakeCore();

        bool threw = false;
        try
        {
            new RemoteVaultOpener(states).OpenAsync(pending, core, Password)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            threw = true;
        }

        check("a failed promotion write is reported", threw);
        check("a failed promotion write locks the opened core again",
            core.OpenBytesCalls == 1 && core.LockCalls == 1,
            $"open={core.OpenBytesCalls} lock={core.LockCalls}");
        check("a failed promotion write leaves no baseline",
            states.Load(UrlFor("write-failure"), User, workingCopy).Sha256 is null);
    }

    private static (string Folder, SyncStateStore States) NewEnvironment(
        string dataDirectory, string name)
    {
        string folder = Path.Combine(dataDirectory,
            "remote-open-" + name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        return (folder, new SyncStateStore(Path.Combine(folder, "state")));
    }

    private static byte[] Bytes(params byte[] values) => values;

    private static string Sha(byte[] bytes)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
            .ToLowerInvariant();

    private sealed class FakeRemote : ISyncRemote
    {
        public WebDavFileInfo? Metadata { get; set; } = new()
        {
            ETag = "\"e1\"",
            Size = 4,
            ModifiedUnix = 1234,
        };

        public byte[] Bytes { get; set; } = new byte[] { 1, 2, 3, 4 };

        public bool Missing { get; set; }

        public Task<WebDavFileInfo?> GetMetadataAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult(Missing ? null : Metadata);

        public Task<byte[]> DownloadAsync(CancellationToken cancellationToken = default)
            => Missing
                ? throw new WebDavException("missing", 404)
                : Task.FromResult(Bytes);

        public Task<string?> UploadAsync(byte[] bytes, string? ifMatch,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeCore : ICoreClient
    {
        public Func<byte[], string, string?, Task>? OnOpenBytes { get; set; }

        public int OpenBytesCalls { get; private set; }

        public byte[]? OpenedBytes { get; private set; }

        public string? OpenedPassword { get; private set; }

        public string? OpenedPath { get; private set; }

        public int LockCalls { get; private set; }

        public bool IsOpen => false;

        public string? DatabasePath { get; private set; }

        public Task OpenAsync(string path, string password)
            => throw new NotSupportedException();

        public Task OpenBytesAsync(byte[] kdbx, string password, string? databasePath = null)
        {
            OpenBytesCalls++;
            OpenedBytes = kdbx;
            OpenedPassword = password;
            OpenedPath = databasePath;
            DatabasePath = databasePath;
            return OnOpenBytes?.Invoke(kdbx, password, databasePath) ?? Task.CompletedTask;
        }

        public Task LockAsync()
        {
            LockCalls++;
            return Task.CompletedTask;
        }

        public Task<byte[]> SaveBytesAsync() => throw new NotSupportedException();

        public Task VerifyAsync(byte[] bytes, string password) => throw new NotSupportedException();

        public Task<SyncDecisionDto> SyncDecideAsync(string remoteMetaJson, bool localDirty)
            => throw new NotSupportedException();

        public Task SyncCommitAsync(string? etag, string? sha256)
            => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
