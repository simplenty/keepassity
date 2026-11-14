// SPDX-License-Identifier: GPL-3.0-or-later
namespace Keepassity.Core;

/// <summary>
/// The remote side of a sync run: one KDBX file on a WebDAV server. The sync
/// coordinator depends on this narrow port, so its ordering and failure rules can
/// be tested without a network server.
/// </summary>
public interface ISyncRemote
{
    /// <summary>
    /// Remote metadata, or null when the file does not exist yet (the first
    /// upload creates it).
    /// </summary>
    Task<WebDavFileInfo?> GetMetadataAsync(CancellationToken cancellationToken = default);

    /// <summary>Download the remote file. The bytes are not trusted until verified.</summary>
    Task<byte[]> DownloadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Upload the bytes. <paramref name="ifMatch"/> is the remote etag the upload
    /// is based on; null means "only create, never overwrite" (If-None-Match: *).
    /// Throws <see cref="WebDavException"/> with <see cref="WebDavException.Conflict"/>
    /// when the server rejects the precondition (the remote changed meanwhile).
    /// </summary>
    Task<string?> UploadAsync(byte[] bytes, string? ifMatch,
        CancellationToken cancellationToken = default);
}
