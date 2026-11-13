// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;

namespace Keepassity.Core;

/// <summary>
/// Cross-process advisory lock for one database path. A second keepassity
/// process cannot open the same vault while the first holds the lock, which
/// stops two windows from saving over each other. The lock file lives under the
/// application data root, so read-only vault directories are not touched; the
/// operating system releases the file handle when the process dies, so a crash
/// leaves no permanent lock.
/// </summary>
public sealed class VaultFileLock : IDisposable
{
    private readonly string _path;
    private FileStream? _stream;

    private VaultFileLock(string path, FileStream stream)
    {
        _path = path;
        _stream = stream;
    }

    /// <summary>Absolute database path this lock belongs to.</summary>
    public string DatabasePath { get; private set; } = string.Empty;

    /// <summary>
    /// Try to take the exclusive lock. Returns null when another process owns it
    /// (or the lock file cannot be created); <paramref name="error"/> carries a
    /// diagnostic for the log, not necessarily user-facing text.
    /// </summary>
    public static VaultFileLock? TryAcquire(string databasePath, out string? error)
    {
        error = null;
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(databasePath);
            string directory = Path.Combine(ApplicationPaths.Root, "locks");
            Directory.CreateDirectory(directory);
            string key = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(fullPath.ToLowerInvariant()))).ToLowerInvariant();
            string path = Path.Combine(directory, key + ".lock");
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, 4096, FileOptions.WriteThrough);
            return new VaultFileLock(path, stream) { DatabasePath = fullPath };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return null;
        }
    }

    public void Dispose()
    {
        FileStream? stream = _stream;
        _stream = null;
        if (stream is not null)
        {
            try
            {
                stream.Dispose();
            }
            catch
            {
                // The handle is released by the OS even if Dispose reports a
                // cached write error; the lock itself is gone either way.
            }
        }
        try
        {
            File.Delete(_path);
        }
        catch
        {
            // A stale zero-byte lock file is harmless: FileShare.None on a fresh
            // open is what enforces the lock, not the file's existence.
        }
    }
}
