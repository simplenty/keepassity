// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;

namespace Keepassity.Core;

/// <summary>
/// Advisory lock for one database path. Two layers protect the file: an
/// account-scoped lock under the application data folder and, when the vault
/// folder is writable, an exclusive lock inside the vault's own
/// <c>.keepassity/locks</c> folder. The second layer is what coordinates a
/// database on a network share between different Windows accounts or machines,
/// where the per-account lock cannot see the other side. The operating system
/// releases both file handles when the process dies, so a crash leaves no
/// permanent lock.
/// </summary>
public sealed class VaultFileLock : IDisposable
{
    private readonly string _path;
    private readonly string? _sharedPath;
    private FileStream? _stream;
    private FileStream? _sharedStream;

    private VaultFileLock(string path, FileStream stream, string? sharedPath = null,
        FileStream? sharedStream = null)
    {
        _path = path;
        _stream = stream;
        _sharedPath = sharedPath;
        _sharedStream = sharedStream;
    }

    /// <summary>Absolute database path this lock belongs to.</summary>
    public string DatabasePath { get; private set; } = string.Empty;

    /// <summary>
    /// Shared lock file next to the database, or null when the vault folder
    /// could not hold one (read-only folder or a not-yet-created database
    /// directory). Diagnostics/tests use it to observe whether cross-machine
    /// coordination is active.
    /// </summary>
    public string? SharedLockPath => _sharedPath;

    /// <summary>
    /// Path of the lock file shared between accounts/machines for a database.
    /// It lives in the database's own <c>.keepassity</c> folder so every client
    /// opening the same shared folder sees the same lock file.
    /// </summary>
    public static string SharedLockPathFor(string databasePath)
    {
        string fullPath = Path.GetFullPath(databasePath);
        string directory = Path.GetDirectoryName(fullPath)!;
        return Path.Combine(directory, ".keepassity", "locks",
            Path.GetFileName(fullPath) + ".lock");
    }

    /// <summary>
    /// Try to take the exclusive lock. Returns null when another process,
    /// account or machine owns it (or the account lock file cannot be created);
    /// <paramref name="error"/> carries a diagnostic for the log, not
    /// necessarily user-facing text.
    /// </summary>
    public static VaultFileLock? TryAcquire(string databasePath, out string? error)
    {
        error = null;
        string fullPath;
        string localPath;
        FileStream local;
        try
        {
            fullPath = Path.GetFullPath(databasePath);
            localPath = LocalLockPath(fullPath);
            local = OpenExclusive(localPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return null;
        }

        string? sharedPath = null;
        FileStream? shared = null;
        try
        {
            string directory = Path.GetDirectoryName(fullPath)!;
            if (!Directory.Exists(directory))
            {
                // The database is being created: the create-only write protects
                // the first bytes, and the next open takes the shared lock.
                return new VaultFileLock(localPath, local) { DatabasePath = fullPath };
            }
            sharedPath = SharedLockPathFor(fullPath);
            Directory.CreateDirectory(Path.GetDirectoryName(sharedPath)!);
            shared = OpenExclusive(sharedPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            bool contended = ex is IOException && sharedPath is not null &&
                             File.Exists(sharedPath);
            if (contended)
            {
                local.Dispose();
                error = "the database is open in another Keepassity instance " +
                        "(shared-folder lock)";
                return null;
            }
            // A read-only or otherwise unwritable vault folder keeps the old
            // account-only protection instead of refusing to open the database.
            AppLog.Info($"shared-folder lock unavailable ({ex.Message}); " +
                        "only this Windows account is protected");
            if (shared is not null)
            {
                shared.Dispose();
                shared = null;
            }
            sharedPath = null;
        }
        return new VaultFileLock(localPath, local, sharedPath, shared)
        {
            DatabasePath = fullPath,
        };
    }

    public void Dispose()
    {
        DisposeStream(ref _stream);
        DisposeStream(ref _sharedStream);
        try
        {
            File.Delete(_path);
        }
        catch
        {
            // A stale zero-byte lock file is harmless: FileShare.None on a fresh
            // open is what enforces the lock, not the file's existence.
        }
        if (_sharedPath is null)
        {
            return;
        }
        try
        {
            File.Delete(_sharedPath);
        }
        catch
        {
            // Same reasoning as the account lock above.
        }
        try
        {
            string lockDirectory = Path.GetDirectoryName(_sharedPath)!;
            if (Directory.Exists(lockDirectory) &&
                Directory.GetFileSystemEntries(lockDirectory).Length == 0)
            {
                Directory.Delete(lockDirectory);
            }
        }
        catch
        {
            // An empty ".keepassity/locks" folder is harmless and may be used by
            // another open database in the same folder.
        }
    }

    private static string LocalLockPath(string fullPath)
    {
        string directory = Path.Combine(ApplicationPaths.Root, "locks");
        Directory.CreateDirectory(directory);
        string key = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(fullPath.ToLowerInvariant()))).ToLowerInvariant();
        return Path.Combine(directory, key + ".lock");
    }

    private static FileStream OpenExclusive(string path)
        => new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096,
            FileOptions.WriteThrough);

    private static void DisposeStream(ref FileStream? stream)
    {
        FileStream? current = stream;
        stream = null;
        if (current is null)
        {
            return;
        }
        try
        {
            current.Dispose();
        }
        catch
        {
            // The handle is released by the OS even if Dispose reports a cached
            // write error; the lock itself is gone either way.
        }
    }
}
