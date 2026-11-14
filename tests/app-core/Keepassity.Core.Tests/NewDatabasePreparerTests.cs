// SPDX-License-Identifier: GPL-3.0-or-later
using Keepassity.Core;

namespace Keepassity.CoreTests;

/// <summary>
/// Creating a database must use the same durability rules as a save: a local
/// create is flushed and create-only, and a WebDAV create claims the local
/// working copy and its clean baseline only after the remote upload succeeded.
/// </summary>
internal static class NewDatabasePreparerTests
{
    public static void Run(TestCheck check, string dataDirectory)
    {
        LocalCreateIsDurable(check, dataDirectory);
        LocalCreateNeverOverwrites(check, dataDirectory);
        RemoteCommitWritesWorkingCopyAndBaseline(check, dataDirectory);
        RemoteCommitBacksUpAStaleWorkingCopy(check, dataDirectory);
        RemoteCommitFailureLeavesNoBaseline(check, dataDirectory);
    }

    private static void LocalCreateIsDurable(TestCheck check, string dataDirectory)
    {
        (string folder, SyncStateStore states) = NewEnvironment(dataDirectory, "local");
        // One level below the environment folder does not exist yet.
        string path = Path.Combine(folder, "nested", "vault.kdbx");
        var preparer = new NewDatabasePreparer(states);
        byte[] bytes = Bytes(1, 2, 3);

        preparer.CreateLocal(path, bytes);

        check("a new local database is written",
            File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(bytes));
        check("a new local database leaves no temporary file",
            Directory.GetFiles(Path.GetDirectoryName(path)!, "*keepassity-tmp*").Length == 0);
        check("a new local database creates no backup",
            BackupService.ListBackups(path).Count == 0);

        bool emptyRejected = false;
        try
        {
            preparer.CreateLocal(Path.Combine(folder, "empty.kdbx"), Array.Empty<byte>());
        }
        catch (ArgumentException)
        {
            emptyRejected = true;
        }
        check("an empty new database is refused",
            emptyRejected && !File.Exists(Path.Combine(folder, "empty.kdbx")));
    }

    private static void LocalCreateNeverOverwrites(TestCheck check, string dataDirectory)
    {
        (string folder, SyncStateStore states) = NewEnvironment(dataDirectory, "no-overwrite");
        string path = Path.Combine(folder, "vault.kdbx");
        File.WriteAllBytes(path, Bytes(9, 9));
        var preparer = new NewDatabasePreparer(states);

        bool refused = false;
        try
        {
            preparer.CreateLocal(path, Bytes(1, 1, 1));
        }
        catch (IOException)
        {
            refused = true;
        }

        check("creating a database refuses an existing file", refused);
        check("a refused create keeps the existing database",
            File.ReadAllBytes(path).SequenceEqual(Bytes(9, 9)));
        check("a refused create leaves no temporary file",
            Directory.GetFiles(folder, "*keepassity-tmp*").Length == 0);
    }

    private static void RemoteCommitWritesWorkingCopyAndBaseline(TestCheck check,
        string dataDirectory)
    {
        (_, SyncStateStore states) = NewEnvironment(dataDirectory, "remote");
        string url = "https://example.test/new-remote.kdbx";
        const string user = "user";
        byte[] bytes = Bytes(4, 5, 6);
        var preparer = new NewDatabasePreparer(states);

        string workingCopy = preparer.CommitRemoteAsync(url, user, bytes, "\"etag-new\"",
            backupRetention: 5).GetAwaiter().GetResult();
        SyncState state = states.Load(url, user, workingCopy);

        check("a remote create claims the deterministic working copy",
            workingCopy == RemoteDatabaseStore.PathFor(url, user) &&
            File.ReadAllBytes(workingCopy).SequenceEqual(bytes));
        check("a remote create records a clean baseline",
            !state.LocalDirty &&
            state.Etag == "\"etag-new\"" &&
            state.Sha256 == Sha(bytes) &&
            state.Size == bytes.Length &&
            state.RemoteModifiedUnix is null &&
            state.LastSyncUnix > 0,
            state.Sha256 ?? "(none)");
        check("a remote create leaves no temporary file in the working-copy folder",
            Directory.GetFiles(Path.GetDirectoryName(workingCopy)!, "*keepassity-tmp*")
                .Length == 0);
    }

    private static void RemoteCommitBacksUpAStaleWorkingCopy(TestCheck check,
        string dataDirectory)
    {
        (_, SyncStateStore states) = NewEnvironment(dataDirectory, "remote-stale");
        string url = "https://example.test/stale-remote.kdbx";
        const string user = "user";
        byte[] stale = Bytes(1, 1, 1);
        byte[] bytes = Bytes(2, 2, 2);
        string workingCopy = RemoteDatabaseStore.PathFor(url, user);
        Directory.CreateDirectory(Path.GetDirectoryName(workingCopy)!);
        File.WriteAllBytes(workingCopy, stale);
        var preparer = new NewDatabasePreparer(states);

        preparer.CommitRemoteAsync(url, user, bytes, "\"e1\"").GetAwaiter().GetResult();

        List<BackupEntry> backups = BackupService.ListBackups(workingCopy);
        check("a remote create backs up a stale working copy",
            File.ReadAllBytes(workingCopy).SequenceEqual(bytes) &&
            backups.Count == 1 &&
            File.ReadAllBytes(backups[0].Path).SequenceEqual(stale),
            backups.Count.ToString());
    }

    private static void RemoteCommitFailureLeavesNoBaseline(TestCheck check,
        string dataDirectory)
    {
        (_, SyncStateStore states) = NewEnvironment(dataDirectory, "remote-failure");
        string url = "https://example.test/failing-remote.kdbx";
        const string user = "user";
        string workingCopy = RemoteDatabaseStore.PathFor(url, user);
        // A directory where the working copy should be makes the durable write
        // fail after the remote upload already succeeded.
        Directory.CreateDirectory(workingCopy);
        var preparer = new NewDatabasePreparer(states);

        bool threw = false;
        try
        {
            preparer.CommitRemoteAsync(url, user, Bytes(7, 7), "\"e1\"")
                .GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            threw = true;
        }

        check("a failed working-copy write is reported", threw);
        check("a failed working-copy write leaves no synced baseline",
            states.Load(url, user, workingCopy).Sha256 is null &&
            !states.Load(url, user, workingCopy).LocalDirty);
        Directory.Delete(workingCopy);
    }

    private static (string Folder, SyncStateStore States) NewEnvironment(
        string dataDirectory, string name)
    {
        string folder = Path.Combine(dataDirectory,
            "new-database-" + name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        return (folder, new SyncStateStore(Path.Combine(folder, "state")));
    }

    private static byte[] Bytes(params byte[] values) => values;

    private static string Sha(byte[] bytes)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
            .ToLowerInvariant();
}
