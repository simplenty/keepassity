// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;

namespace Keepassity.Core;

/// <summary>What a cache cleanup pass removed and why it kept the rest.</summary>
public sealed record RemoteCacheCleanupResult(
    int RemovedWorkingCopies,
    int RemovedOrphans,
    int KeptDirty,
    int KeptRecent,
    int KeptUnknown)
{
    public int TotalRemoved => RemovedWorkingCopies + RemovedOrphans;

    public override string ToString()
        => $"removed {TotalRemoved} (copies {RemovedWorkingCopies}, orphans {RemovedOrphans}); " +
           $"kept dirty={KeptDirty}, recent={KeptRecent}, unknown={KeptUnknown}";
}

/// <summary>
/// Bounded-size cleanup for the local WebDAV cache. A clean working copy can be
/// downloaded again, so it may be dropped after it has not been touched for a
/// while; a copy with unsynced edits exists only on this machine and must never
/// be removed. Copies whose remote source is unknown (created before the
/// metadata sidecar existed) are kept too.
/// </summary>
public sealed class RemoteCacheCleaner
{
    private readonly SyncStateStore _states;
    private readonly RecentDatabaseStore? _recents;
    private readonly Func<DateTimeOffset> _now;

    public RemoteCacheCleaner(SyncStateStore states, Func<DateTimeOffset>? now = null,
        RecentDatabaseStore? recents = null)
    {
        _states = states ?? throw new ArgumentNullException(nameof(states));
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _recents = recents;
    }

    /// <summary>
    /// Remove cached working copies that are clean (the file hash matches the
    /// recorded baseline and there are no unsynced edits) and untouched for
    /// <paramref name="maxAge"/>, plus leftovers whose working copy is already
    /// gone. Call only when no other instance may have a cache file open.
    /// </summary>
    public RemoteCacheCleanupResult Prune(IReadOnlyCollection<string> keepPaths,
        TimeSpan maxAge)
    {
        ArgumentNullException.ThrowIfNull(keepPaths);
        var keep = new HashSet<string>(keepPaths.Select(Path.GetFullPath),
            StringComparer.OrdinalIgnoreCase);
        string root = RemoteDatabaseStore.Root;
        if (!Directory.Exists(root))
        {
            return new RemoteCacheCleanupResult(0, 0, 0, 0, 0);
        }
        DateTimeOffset now = _now();
        int removed = 0;
        int orphans = 0;
        int keptDirty = 0;
        int keptRecent = 0;
        int keptUnknown = 0;

        foreach (string file in Directory.EnumerateFiles(root, "*.kdbx"))
        {
            string path = Path.GetFullPath(file);
            if (keep.Contains(path))
            {
                continue;
            }
            RemoteSourceMetadata? metadata = RemoteDatabaseStore.ReadMetadata(path);
            if (metadata is null)
            {
                keptUnknown++;
                continue;
            }
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                keptUnknown++;
                continue;
            }
            SyncState state = _states.Load(metadata.Url, metadata.Username, path);
            bool clean = !state.LocalDirty && state.Sha256 is not null &&
                         string.Equals(state.Sha256, Sha256Hex(bytes),
                             StringComparison.OrdinalIgnoreCase);
            if (!clean)
            {
                keptDirty++;
                continue;
            }
            if (now - File.GetLastWriteTimeUtc(path) < maxAge)
            {
                keptRecent++;
                continue;
            }
            if (DeleteWorkingCopy(path, metadata))
            {
                removed++;
            }
            else
            {
                keptUnknown++;
            }
        }

        string backupRoot = Path.Combine(root, ".keepassity", "backups");
        if (Directory.Exists(backupRoot))
        {
            foreach (string directory in Directory.EnumerateDirectories(backupRoot))
            {
                string workingCopy = Path.Combine(root, Path.GetFileName(directory));
                if (File.Exists(workingCopy) || keep.Contains(Path.GetFullPath(workingCopy)))
                {
                    continue;
                }
                try
                {
                    Directory.Delete(directory, recursive: true);
                    _recents?.Remove(workingCopy);
                    orphans++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppLog.Warn($"orphaned remote backup could not be removed: {ex.Message}");
                }
            }
        }

        foreach (string metadataFile in Directory.EnumerateFiles(root, "*.source.json"))
        {
            string workingCopy = Path.Combine(root,
                Path.GetFileName(metadataFile)[..^".source.json".Length] + ".kdbx");
            if (File.Exists(workingCopy) || keep.Contains(Path.GetFullPath(workingCopy)))
            {
                continue;
            }
            try
            {
                File.Delete(metadataFile);
                _recents?.Remove(workingCopy);
                orphans++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn($"orphaned remote metadata could not be removed: {ex.Message}");
            }
        }

        return new RemoteCacheCleanupResult(removed, orphans, keptDirty, keptRecent,
            keptUnknown);
    }

    private bool DeleteWorkingCopy(string path, RemoteSourceMetadata metadata)
    {
        try
        {
            // Delete the copy itself first: if it is unexpectedly held, nothing
            // else is removed and the entry simply stays cached.
            File.Delete(path);
            string backupDirectory = BackupService.BackupDirectory(path);
            if (Directory.Exists(backupDirectory))
            {
                Directory.Delete(backupDirectory, recursive: true);
            }
            _states.Remove(metadata.Url, metadata.Username, path);
            RemoteDatabaseStore.DeleteMetadata(path);
            _recents?.Remove(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"stale remote working copy could not be removed: {ex.Message}");
            return false;
        }
    }

    private static string Sha256Hex(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
