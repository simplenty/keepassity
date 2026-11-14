// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;

namespace Keepassity.Core;

/// <summary>
/// A database downloaded from WebDAV that has not been written to its local
/// working copy yet. The bytes stay in memory until the master password opens
/// them, so a corrupted download, a wrong file or an HTML error page can never
/// become the local vault.
/// </summary>
public sealed class PendingRemoteDatabase
{
    public required string Url { get; init; }
    public required string Username { get; init; }
    public required string WorkingCopyPath { get; init; }
    public required WebDavFileInfo Metadata { get; init; }
    public required byte[] Bytes { get; init; }

    /// <summary>Backups kept when the verified bytes replace the working copy.</summary>
    public int BackupRetention { get; init; } = 20;
}

/// <summary>
/// Opens a database downloaded from WebDAV. The order is the data-safety rule:
/// open the bytes first (only the master password can do that), and only then
/// promote them to the working copy with the durable write protocol and record a
/// clean sync baseline. A failed open leaves both the working copy and its
/// baseline untouched.
/// </summary>
public sealed class RemoteVaultOpener
{
    private readonly SyncStateStore _states;

    public RemoteVaultOpener(SyncStateStore states)
    {
        _states = states ?? throw new ArgumentNullException(nameof(states));
    }

    /// <summary>Download the remote file and keep it pending. Never writes locally.</summary>
    public async Task<PendingRemoteDatabase> DownloadAsync(ISyncRemote remote, string url,
        string username, int backupRetention = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remote);
        WebDavFileInfo? metadata = await remote.GetMetadataAsync(cancellationToken);
        if (metadata is null)
        {
            throw new WebDavException("the remote database file was not found",
                statusCode: 404);
        }
        byte[] bytes = await remote.DownloadAsync(cancellationToken);
        return new PendingRemoteDatabase
        {
            Url = url.Trim(),
            Username = username.Trim(),
            WorkingCopyPath = RemoteDatabaseStore.PathFor(url, username),
            Metadata = metadata,
            Bytes = bytes,
            BackupRetention = backupRetention,
        };
    }

    /// <summary>
    /// Open <paramref name="pending"/> with <paramref name="password"/> and, only
    /// when it decrypts, replace the working copy and save the synced baseline.
    /// Throws <see cref="CoreException"/> for a wrong password or a damaged file;
    /// on any failure the core is locked again and nothing on disk changed.
    /// </summary>
    public async Task OpenAsync(PendingRemoteDatabase pending, ICoreClient core,
        string password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(core);
        // The master password proves the download is a readable database before
        // it is allowed to touch the working copy.
        await core.OpenBytesAsync(pending.Bytes, password, pending.WorkingCopyPath)
            .ConfigureAwait(false);
        try
        {
            // Same durable protocol as a save: flushed sibling, backup of the
            // previous working copy, atomic replace. Then the baseline, so a
            // crash in between leaves the vault dirty instead of silently synced.
            await Task.Run(() => AtomicFile.Replace(pending.WorkingCopyPath, pending.Bytes,
                pending.BackupRetention, "download"), cancellationToken).ConfigureAwait(false);
            SyncState state = _states.Load(pending.Url, pending.Username,
                pending.WorkingCopyPath);
            state.Etag = pending.Metadata.ETag;
            state.Sha256 = Sha256Hex(pending.Bytes);
            state.Size = pending.Bytes.Length;
            state.RemoteModifiedUnix = pending.Metadata.ModifiedUnix;
            state.LocalDirty = false;
            state.LastSyncUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _states.Save(pending.Url, pending.Username, pending.WorkingCopyPath, state);
        }
        catch
        {
            await core.LockAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static string Sha256Hex(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
