// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;

namespace Keepassity.Core;

/// <summary>
/// Writes a freshly created database to disk with the same durability rules as a
/// save. For WebDAV it also claims the local working copy and records the clean
/// sync baseline, because the remote create-only upload has already succeeded by
/// then; a plain copy or a half-written local file must not be able to hide that.
/// </summary>
public sealed class NewDatabasePreparer
{
    private readonly SyncStateStore _states;

    public NewDatabasePreparer(SyncStateStore states)
    {
        _states = states ?? throw new ArgumentNullException(nameof(states));
    }

    /// <summary>
    /// Durably write a new local database. Existing files are never replaced:
    /// the create-only move fails instead, so a program that created the same
    /// path between the caller's check and this write keeps its data.
    /// </summary>
    public void CreateLocal(string path, byte[] bytes)
    {
        RequireContent(bytes);
        AtomicFile.WriteNew(path, bytes);
    }

    /// <summary>
    /// After a successful create-only upload: durably write the local working
    /// copy (backing up a stale copy if one exists), then record the clean sync
    /// baseline. The order means a crash in between leaves the vault dirty
    /// instead of silently claiming a baseline that the local file never had.
    /// Returns the working-copy path.
    /// </summary>
    public async Task<string> CommitRemoteAsync(string url, string username, byte[] bytes,
        string? etag, int backupRetention = 20, CancellationToken cancellationToken = default)
    {
        RequireContent(bytes);
        string normalizedUrl = url.Trim();
        string normalizedUsername = username.Trim();
        string workingCopy = RemoteDatabaseStore.PathFor(normalizedUrl, normalizedUsername);
        await Task.Run(
            () => AtomicFile.Replace(workingCopy, bytes, backupRetention, "new database"),
            cancellationToken).ConfigureAwait(false);
        SyncState state = _states.Load(normalizedUrl, normalizedUsername, workingCopy);
        state.Etag = etag;
        state.Sha256 = Sha256Hex(bytes);
        state.Size = bytes.Length;
        state.RemoteModifiedUnix = null;
        state.LocalDirty = false;
        state.LastSyncUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _states.Save(normalizedUrl, normalizedUsername, workingCopy, state);
        RemoteDatabaseStore.WriteMetadata(normalizedUrl, normalizedUsername);
        return workingCopy;
    }

    private static void RequireContent(byte[] bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            throw new ArgumentException("The new database is empty.", nameof(bytes));
        }
    }

    private static string Sha256Hex(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
