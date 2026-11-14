// SPDX-License-Identifier: GPL-3.0-or-later
namespace Keepassity.Core;

/// <summary>
/// The local side of a sync run: the currently open vault. The shell adapts its
/// window state to this port (durable write, core reopen, merge through a second
/// core handle), which keeps the sync coordinator platform-free and testable.
/// </summary>
public interface IVaultDocument
{
    /// <summary>Absolute path of the open database; also the sync-state key.</summary>
    string Path { get; }

    /// <summary>Read the current bytes from disk.</summary>
    Task<byte[]> ReadBytesAsync();

    /// <summary>
    /// Replace the live database with <paramref name="bytes"/> using the durable
    /// save protocol. Must refuse (throw) when the file changed outside the
    /// application since it was opened.
    /// </summary>
    Task ReplaceAsync(byte[] bytes);

    /// <summary>Reopen the core and refresh the UI from the bytes just written.</summary>
    Task ReloadAsync();

    /// <summary>
    /// Three-way merge the remote bytes into the local model and return the
    /// merged KDBX bytes plus the remaining conflicts. Never writes to disk.
    /// </summary>
    Task<SyncMergeResult> MergeRemoteAsync(byte[] remote);
}
