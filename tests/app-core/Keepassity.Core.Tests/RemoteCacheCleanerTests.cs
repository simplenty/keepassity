// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using Keepassity.Core;

namespace Keepassity.CoreTests;

/// <summary>
/// The WebDAV cache may be pruned only for copies that can be downloaded again:
/// a clean copy whose baseline hash matches its file and which has not been
/// touched for the retention window. Dirty, unknown and listed copies stay.
/// </summary>
internal static class RemoteCacheCleanerTests
{
    private const string FixtureName = "test_db_kdbx4_with_password_aes.kdbx";

    public static void Run(TestCheck check, string dataDirectory)
    {
        PrunesOldCleanCopy(check, dataDirectory);
        KeepsDirtyCopy(check, dataDirectory);
        KeepsRecentCleanCopy(check, dataDirectory);
        KeepsUnknownProvenance(check, dataDirectory);
        KeepsListedCopy(check, dataDirectory);
        KeepsHashMismatch(check, dataDirectory);
        CleansOrphanLeftovers(check, dataDirectory);
        PrunedCopyLeavesRecentList(check, dataDirectory);
    }

    private static void PrunesOldCleanCopy(TestCheck check, string dataDirectory)
    {
        using var scope = new IsolatedRoot(dataDirectory, "prune-clean");
        (string working, string url, string user) = CreateCachedCopy(
            scope.States, clean: true, old: true);
        BackupEntry? backup = BackupService.CreateBackup(working, 5, "test");
        string? metadata = RemoteDatabaseStore.MetadataPathFor(working);
        string statePath = scope.States.StatePathFor(url, user, working);

        RemoteCacheCleanupResult result = scope.NewCleaner().Prune(Array.Empty<string>(),
            TimeSpan.FromDays(90));
        check("remote cache: an old clean working copy is pruned",
            result.RemovedWorkingCopies == 1 && !File.Exists(working), result.ToString());
        check("remote cache: pruning removes its backups, metadata and sync state",
            backup is not null && !Directory.Exists(BackupService.BackupDirectory(working)) &&
            metadata is not null && !File.Exists(metadata) && !File.Exists(statePath));
    }

    private static void KeepsDirtyCopy(TestCheck check, string dataDirectory)
    {
        using var scope = new IsolatedRoot(dataDirectory, "keep-dirty");
        (string working, _, _) = CreateCachedCopy(scope.States, clean: false, old: true);

        RemoteCacheCleanupResult result = scope.NewCleaner().Prune(Array.Empty<string>(),
            TimeSpan.FromDays(90));
        check("remote cache: a copy with unsynced edits is never pruned",
            File.Exists(working) && result.KeptDirty == 1, result.ToString());
    }

    private static void KeepsRecentCleanCopy(TestCheck check, string dataDirectory)
    {
        using var scope = new IsolatedRoot(dataDirectory, "keep-recent");
        (string working, _, _) = CreateCachedCopy(scope.States, clean: true, old: false);

        RemoteCacheCleanupResult result = scope.NewCleaner().Prune(Array.Empty<string>(),
            TimeSpan.FromDays(90));
        check("remote cache: a recently used clean copy is kept",
            File.Exists(working) && result.KeptRecent == 1, result.ToString());
    }

    private static void KeepsUnknownProvenance(TestCheck check, string dataDirectory)
    {
        using var scope = new IsolatedRoot(dataDirectory, "keep-unknown");
        Directory.CreateDirectory(RemoteDatabaseStore.Root);
        string working = Path.Combine(RemoteDatabaseStore.Root,
            Guid.NewGuid().ToString("N") + ".kdbx");
        File.WriteAllBytes(working, File.ReadAllBytes(TestEnvironment.Fixture(FixtureName)));
        File.SetLastWriteTimeUtc(working, DateTime.UtcNow.AddDays(-200));

        RemoteCacheCleanupResult result = scope.NewCleaner().Prune(Array.Empty<string>(),
            TimeSpan.FromDays(90));
        check("remote cache: a copy without source metadata is kept",
            File.Exists(working) && result.KeptUnknown == 1, result.ToString());
    }

    private static void KeepsListedCopy(TestCheck check, string dataDirectory)
    {
        using var scope = new IsolatedRoot(dataDirectory, "keep-listed");
        (string working, _, _) = CreateCachedCopy(scope.States, clean: true, old: true);

        RemoteCacheCleanupResult result = scope.NewCleaner()
            .Prune(new[] { working }, TimeSpan.FromDays(90));
        check("remote cache: the current target is kept even when old and clean",
            File.Exists(working) && result.RemovedWorkingCopies == 0, result.ToString());
    }

    private static void KeepsHashMismatch(TestCheck check, string dataDirectory)
    {
        using var scope = new IsolatedRoot(dataDirectory, "keep-mismatch");
        (string working, _, _) = CreateCachedCopy(scope.States, clean: true, old: true);
        // The recorded baseline no longer matches the file: it may contain local
        // edits whose sidecar was lost, so it is treated as dirty.
        File.WriteAllBytes(working, new byte[] { 9, 9, 9 });
        File.SetLastWriteTimeUtc(working, DateTime.UtcNow.AddDays(-200));

        RemoteCacheCleanupResult result = scope.NewCleaner().Prune(Array.Empty<string>(),
            TimeSpan.FromDays(90));
        check("remote cache: a copy whose baseline hash does not match is kept",
            File.Exists(working) && result.KeptDirty == 1, result.ToString());
    }

    private static void CleansOrphanLeftovers(TestCheck check, string dataDirectory)
    {
        using var scope = new IsolatedRoot(dataDirectory, "orphans");
        (string working, _, _) = CreateCachedCopy(scope.States, clean: true, old: true);
        _ = BackupService.CreateBackup(working, 5, "test");
        string? metadata = RemoteDatabaseStore.MetadataPathFor(working);
        File.Delete(working);

        RemoteCacheCleanupResult result = scope.NewCleaner().Prune(Array.Empty<string>(),
            TimeSpan.FromDays(90));
        check("remote cache: leftovers of a vanished working copy are removed",
            result.RemovedOrphans >= 1 &&
            !Directory.Exists(BackupService.BackupDirectory(working)) &&
            metadata is not null && !File.Exists(metadata), result.ToString());
    }

    private static void PrunedCopyLeavesRecentList(TestCheck check, string dataDirectory)
    {
        using var scope = new IsolatedRoot(dataDirectory, "recent-prune");
        (string working, string url, string user) = CreateCachedCopy(
            scope.States, clean: true, old: true);
        var recents = new RecentDatabaseStore();
        recents.Remember(working, "webdav", url, user);

        RemoteCacheCleanupResult result = scope.NewCleaner(recents)
            .Prune(Array.Empty<string>(), TimeSpan.FromDays(90));
        check("remote cache: pruning also forgets the deleted working copy in recents",
            result.RemovedWorkingCopies == 1 && recents.Entries.Count == 0 &&
            !File.Exists(working), recents.Entries.Count.ToString());
    }

    private static (string Working, string Url, string User) CreateCachedCopy(
        SyncStateStore states, bool clean, bool old)
    {
        string url = "https://example.test/" + Guid.NewGuid().ToString("N") + ".kdbx";
        const string user = "user";
        string working = RemoteDatabaseStore.PathFor(url, user);
        byte[] bytes = File.ReadAllBytes(TestEnvironment.Fixture(FixtureName));
        File.WriteAllBytes(working, bytes);
        RemoteDatabaseStore.WriteMetadata(url, user);
        SyncState state = states.Load(url, user, working);
        state.Etag = "\"etag\"";
        state.Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        state.Size = bytes.Length;
        state.LocalDirty = !clean;
        state.LastSyncUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        states.Save(url, user, working, state);
        if (old)
        {
            File.SetLastWriteTimeUtc(working, DateTime.UtcNow.AddDays(-200));
        }
        return (working, url, user);
    }

    /// <summary>
    /// ApplicationPaths.Root is process-wide, so each check points the data root
    /// at a fresh directory and restores the previous one afterwards.
    /// </summary>
    private sealed class IsolatedRoot : IDisposable
    {
        private readonly string? _previous;

        public IsolatedRoot(string dataDirectory, string name)
        {
            _previous = Environment.GetEnvironmentVariable("KEEPASSITY_DATA_DIR");
            string root = Path.Combine(dataDirectory,
                "remote-cache-" + name + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(root);
            Environment.SetEnvironmentVariable("KEEPASSITY_DATA_DIR", root);
            States = new SyncStateStore();
        }

        public SyncStateStore States { get; }

        public RemoteCacheCleaner NewCleaner(RecentDatabaseStore? recents = null)
            => new(States, () => DateTimeOffset.UtcNow, recents);

        public void Dispose()
            => Environment.SetEnvironmentVariable("KEEPASSITY_DATA_DIR", _previous);
    }
}
