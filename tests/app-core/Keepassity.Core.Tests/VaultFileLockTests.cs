// SPDX-License-Identifier: GPL-3.0-or-later
using Keepassity.Core;

namespace Keepassity.CoreTests;

/// <summary>
/// The database lock has two layers: the per-account lock under the application
/// data folder and, when the vault folder is writable, a shared lock next to the
/// database so different Windows accounts or machines on a network share see
/// each other.
/// </summary>
internal static class VaultFileLockTests
{
    public static void Run(TestCheck check, string dataDirectory)
    {
        SharedLockIsCreatedAndRemoved(check, dataDirectory);
        SharedLockHeldElsewhereBlocksOpen(check, dataDirectory);
        UnwritableSharedFolderFallsBack(check, dataDirectory);
        MissingDatabaseDirectoryUsesAccountLockOnly(check, dataDirectory);
    }

    private static void SharedLockIsCreatedAndRemoved(TestCheck check, string dataDirectory)
    {
        (_, string path) = NewDatabase(dataDirectory, "shared-create");
        VaultFileLock? held = VaultFileLock.TryAcquire(path, out string? error);
        check("lock: a writable vault folder also takes the shared lock",
            held is not null && held.SharedLockPath is not null &&
            File.Exists(held.SharedLockPath), error ?? "(none)");
        check("lock: the shared lock lives under the vault folder",
            held?.SharedLockPath is not null &&
            string.Equals(held.SharedLockPath, VaultFileLock.SharedLockPathFor(path),
                StringComparison.OrdinalIgnoreCase), held?.SharedLockPath ?? "(none)");
        held?.Dispose();
        check("lock: releasing removes the shared lock file",
            !File.Exists(VaultFileLock.SharedLockPathFor(path)));
    }

    private static void SharedLockHeldElsewhereBlocksOpen(TestCheck check,
        string dataDirectory)
    {
        (_, string path) = NewDatabase(dataDirectory, "shared-contended");
        string sharedPath = VaultFileLock.SharedLockPathFor(path);
        Directory.CreateDirectory(Path.GetDirectoryName(sharedPath)!);
        using (var otherAccount = new FileStream(sharedPath, FileMode.OpenOrCreate,
                   FileAccess.ReadWrite, FileShare.None))
        {
            VaultFileLock? blocked = VaultFileLock.TryAcquire(path, out string? error);
            check("lock: a shared lock held elsewhere refuses the open",
                blocked is null && error is not null, error ?? "(none)");
            blocked?.Dispose();
        }

        VaultFileLock? acquired = VaultFileLock.TryAcquire(path, out string? afterError);
        check("lock: the same vault opens after the shared lock is released",
            acquired is not null, afterError ?? "(none)");
        acquired?.Dispose();
    }

    private static void UnwritableSharedFolderFallsBack(TestCheck check,
        string dataDirectory)
    {
        (string directory, string path) = NewDatabase(dataDirectory, "shared-read-only");
        // A file where the folder should be makes the shared-lock directory
        // creation fail, like a read-only or permission-restricted folder.
        string blockingFile = Path.Combine(directory, ".keepassity");
        File.WriteAllBytes(blockingFile, Array.Empty<byte>());
        try
        {
            VaultFileLock? held = VaultFileLock.TryAcquire(path, out string? error);
            check("lock: an unwritable shared folder falls back to the account lock",
                held is not null && held.SharedLockPath is null, error ?? "(none)");
            VaultFileLock? second = VaultFileLock.TryAcquire(path, out string? secondError);
            check("lock: the account lock still excludes a second process",
                second is null && secondError is not null, secondError ?? "(none)");
            second?.Dispose();
            held?.Dispose();
        }
        finally
        {
            File.Delete(blockingFile);
        }
    }

    private static void MissingDatabaseDirectoryUsesAccountLockOnly(TestCheck check,
        string dataDirectory)
    {
        string directory = Path.Combine(dataDirectory,
            "lock-missing-" + Guid.NewGuid().ToString("N")[..8]);
        string path = Path.Combine(directory, "new-vault.kdbx");
        VaultFileLock? held = VaultFileLock.TryAcquire(path, out string? error);
        check("lock: a not-yet-created database uses the account lock only",
            held is not null && held.SharedLockPath is null, error ?? "(none)");
        held?.Dispose();
        check("lock: the account lock does not create the database directory",
            !Directory.Exists(directory));
    }

    private static (string Directory, string Path) NewDatabase(string dataDirectory,
        string name)
    {
        string directory = Path.Combine(dataDirectory,
            "lock-" + name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "vault.kdbx");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        return (directory, path);
    }
}
