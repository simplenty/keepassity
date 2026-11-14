// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Keepassity.Core;

/// <summary>The remote source a working copy belongs to.</summary>
public sealed record RemoteSourceMetadata(string Url, string Username);

/// <summary>
/// Local working copies for databases opened from WebDAV. The KDBX bytes stay
/// encrypted at rest; the folder only keeps a cached database per remote source.
/// A small metadata file records which remote source a working copy came from,
/// so stale cache entries can be identified safely later.
/// </summary>
public static class RemoteDatabaseStore
{
    public static string Root => Path.Combine(ApplicationPaths.Root, "remote");

    /// <summary>Working-copy path for a remote file URL + username pair.</summary>
    public static string PathFor(string url, string username)
    {
        string key = $"{url.Trim()}\n{username.Trim()}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        string name = Convert.ToHexString(hash)[..32].ToLowerInvariant() + ".kdbx";
        Directory.CreateDirectory(Root);
        return Path.Combine(Root, name);
    }

    /// <summary>Metadata path for a working copy; null for paths outside the cache.</summary>
    public static string? MetadataPathFor(string workingCopyPath)
    {
        try
        {
            string fullPath = Path.GetFullPath(workingCopyPath);
            string root = Path.GetFullPath(Root);
            if (!string.Equals(Path.GetDirectoryName(fullPath), root,
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return Path.Combine(root,
                Path.GetFileNameWithoutExtension(fullPath) + ".source.json");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Record the remote source for a working copy. Best effort: the copy still
    /// works without it, it just cannot be pruned as safely later.
    /// </summary>
    public static void WriteMetadata(string url, string username)
    {
        try
        {
            string? path = MetadataPathFor(PathFor(url, username));
            if (path is null)
            {
                return;
            }
            AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(
                new RemoteSourceMetadata(url.Trim(), username.Trim())));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"remote working-copy metadata could not be written: {ex.Message}");
        }
    }

    /// <summary>Read the remote source recorded for a working copy.</summary>
    public static RemoteSourceMetadata? ReadMetadata(string workingCopyPath)
    {
        try
        {
            string? path = MetadataPathFor(workingCopyPath);
            if (path is null || !File.Exists(path))
            {
                return null;
            }
            RemoteSourceMetadata? metadata = JsonSerializer.Deserialize<RemoteSourceMetadata>(
                File.ReadAllText(path));
            return metadata is { Url.Length: > 0 }
                ? metadata
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or JsonException)
        {
            AppLog.Warn($"remote working-copy metadata is unreadable: {ex.Message}");
            return null;
        }
    }

    /// <summary>Remove the metadata file of a working copy (best effort).</summary>
    public static void DeleteMetadata(string workingCopyPath)
    {
        try
        {
            string? path = MetadataPathFor(workingCopyPath);
            if (path is not null && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"remote working-copy metadata could not be removed: {ex.Message}");
        }
    }
}
