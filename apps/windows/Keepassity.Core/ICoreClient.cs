// SPDX-License-Identifier: GPL-3.0-or-later
namespace Keepassity.Core;

/// <summary>
/// The application-facing view of the native core. View models and application
/// services depend on this port, never on the P/Invoke implementation, so their
/// behaviour can be tested without the native library or a window.
/// </summary>
public interface ICoreClient : IDisposable
{
    /// <summary>Whether a database handle is currently held (also true when locked).</summary>
    bool IsOpen { get; }

    /// <summary>Path of the database the handle was opened from, when known.</summary>
    string? DatabasePath { get; }

    /// <summary>
    /// Open and decrypt a database. Throws <see cref="CoreException"/> for a
    /// wrong password, a damaged file or an unsupported format.
    /// </summary>
    Task OpenAsync(string path, string password);

    /// <summary>
    /// Open and decrypt KDBX bytes already in memory, without reading a file
    /// first. Used for a remote download so an unverified download never has to
    /// reach the working copy; <paramref name="databasePath"/> is only recorded
    /// for diagnostics and later saves.
    /// </summary>
    Task OpenBytesAsync(byte[] kdbx, string password, string? databasePath = null);

    /// <summary>Lock the vault and drop the decrypted state; the handle stays usable.</summary>
    Task LockAsync();

    /// <summary>
    /// Serialize the current model as KDBX bytes without writing them to disk.
    /// The core has already decrypted and re-parsed the result before returning,
    /// so the bytes are safe to place on disk.
    /// </summary>
    Task<byte[]> SaveBytesAsync();

    /// <summary>
    /// Decrypt and validate KDBX bytes with the given password without keeping a
    /// handle. Used before downloaded or restored bytes replace the live file.
    /// </summary>
    Task VerifyAsync(byte[] bytes, string password);

    /// <summary>Ask the core's sync state machine what to do with the remote copy.</summary>
    Task<SyncDecisionDto> SyncDecideAsync(string remoteMetaJson, bool localDirty);

    /// <summary>Record the remote etag and content hash the local copy is now in sync with.</summary>
    Task SyncCommitAsync(string? etag, string? sha256);
}
