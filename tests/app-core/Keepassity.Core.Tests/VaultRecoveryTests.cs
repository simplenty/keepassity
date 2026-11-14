// SPDX-License-Identifier: GPL-3.0-or-later
using Keepassity.Core;

namespace Keepassity.CoreTests;

/// <summary>
/// Recovery from a database that cannot be opened: the newest local backup whose
/// bytes actually decrypt must win, while unreadable, corrupt and
/// wrong-password candidates are skipped.
/// </summary>
internal static class VaultRecoveryTests
{
    private const string FixtureName = "test_db_kdbx4_with_password_aes.kdbx";
    private const string FixturePassword = "demopass";

    public static void Run(TestCheck check, string rootDirectory)
    {
        NewestRestorableWins(check, rootDirectory);
        CorruptNewestIsSkipped(check, rootDirectory);
        EmptyNewestIsSkipped(check, rootDirectory);
        ThrowingVerifierIsSkipped(check, rootDirectory);
        NoBackupsReturnsNull(check, rootDirectory);
        CancellationIsHonored(check, rootDirectory);
        RealCorePicksTheBackupThatDecrypts(check, rootDirectory);
    }

    private static void NewestRestorableWins(TestCheck check, string rootDirectory)
    {
        (string database, byte[] valid) = NewDamagedDatabase(rootDirectory,
            "recovery-newest");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        BackupService.CreateBackup(database, 20, "older", now.AddMinutes(-10));
        BackupEntry newest = BackupService.CreateBackup(database, 20, "newer", now)
            ?? throw new InvalidOperationException("backup was not created");

        RestorableBackup? found = Find(database, valid).GetAwaiter().GetResult();
        check("recovery: the newest decryptable backup is chosen",
            found is not null &&
            string.Equals(found.Entry.Path, newest.Path, StringComparison.OrdinalIgnoreCase) &&
            found.Bytes.SequenceEqual(valid), found?.Entry.Path ?? "(none)");
    }

    private static void CorruptNewestIsSkipped(TestCheck check, string rootDirectory)
    {
        (string database, byte[] valid) = NewDamagedDatabase(rootDirectory,
            "recovery-corrupt");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        BackupEntry older = BackupService.CreateBackup(database, 20, "valid",
            now.AddMinutes(-10)) ?? throw new InvalidOperationException("backup missing");
        BackupEntry newest = BackupService.CreateBackup(database, 20, "corrupt", now)
            ?? throw new InvalidOperationException("backup missing");
        File.WriteAllBytes(newest.Path,
            File.ReadAllBytes(TestEnvironment.Fixture("broken_random_data.kdbx")));

        RestorableBackup? found = Find(database, valid).GetAwaiter().GetResult();
        check("recovery: a corrupt newest backup is skipped",
            found is not null &&
            string.Equals(found.Entry.Path, older.Path, StringComparison.OrdinalIgnoreCase) &&
            found.Bytes.SequenceEqual(valid), found?.Entry.Path ?? "(none)");
    }

    private static void EmptyNewestIsSkipped(TestCheck check, string rootDirectory)
    {
        (string database, byte[] valid) = NewDamagedDatabase(rootDirectory,
            "recovery-empty");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        BackupEntry older = BackupService.CreateBackup(database, 20, "valid",
            now.AddMinutes(-10)) ?? throw new InvalidOperationException("backup missing");
        BackupEntry newest = BackupService.CreateBackup(database, 20, "empty", now)
            ?? throw new InvalidOperationException("backup missing");
        File.WriteAllBytes(newest.Path, Array.Empty<byte>());
        // An unreadable candidate (open with no sharing) must be skipped too.
        BackupEntry locked = BackupService.CreateBackup(database, 20, "locked",
            now.AddMinutes(-5)) ?? throw new InvalidOperationException("backup missing");
        File.WriteAllBytes(locked.Path, valid);
        using var exclusive = new FileStream(locked.Path, FileMode.Open, FileAccess.Read,
            FileShare.None);

        RestorableBackup? found = Find(database, valid).GetAwaiter().GetResult();
        check("recovery: empty and unreadable backups are skipped",
            found is not null &&
            string.Equals(found.Entry.Path, older.Path, StringComparison.OrdinalIgnoreCase) &&
            found.Bytes.SequenceEqual(valid), found?.Entry.Path ?? "(none)");
    }

    private static void ThrowingVerifierIsSkipped(TestCheck check, string rootDirectory)
    {
        (string database, byte[] valid) = NewDamagedDatabase(rootDirectory,
            "recovery-throwing");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        BackupEntry older = BackupService.CreateBackup(database, 20, "valid",
            now.AddMinutes(-10)) ?? throw new InvalidOperationException("backup missing");
        BackupEntry newest = BackupService.CreateBackup(database, 20, "wrong-password", now)
            ?? throw new InvalidOperationException("backup missing");
        File.WriteAllBytes(newest.Path,
            File.ReadAllBytes(TestEnvironment.Fixture("broken_random_data.kdbx")));

        RestorableBackup? found = VaultRecovery.FindRestorableBackupAsync(database, bytes =>
        {
            if (bytes.SequenceEqual(valid))
            {
                return Task.FromResult(true);
            }
            throw new CoreException("BadPassword", "different password");
        }).GetAwaiter().GetResult();
        check("recovery: a verifier exception skips the candidate",
            found is not null &&
            string.Equals(found.Entry.Path, older.Path, StringComparison.OrdinalIgnoreCase),
            found?.Entry.Path ?? "(none)");
    }

    private static void NoBackupsReturnsNull(TestCheck check, string rootDirectory)
    {
        (string database, byte[] valid) = NewDamagedDatabase(rootDirectory,
            "recovery-none");
        RestorableBackup? found = Find(database, valid).GetAwaiter().GetResult();
        check("recovery: no backups means no restore candidate", found is null);
    }

    private static void CancellationIsHonored(TestCheck check, string rootDirectory)
    {
        (string database, byte[] valid) = NewDamagedDatabase(rootDirectory,
            "recovery-cancel");
        _ = BackupService.CreateBackup(database, 20, "valid");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool cancelled = false;
        try
        {
            _ = VaultRecovery.FindRestorableBackupAsync(database,
                _ => Task.FromResult(true), cancellation.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        check("recovery: cancellation stops the search", cancelled);
    }

    private static void RealCorePicksTheBackupThatDecrypts(TestCheck check,
        string rootDirectory)
    {
        (string database, byte[] valid) = NewDamagedDatabase(rootDirectory,
            "recovery-real-core");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        BackupEntry older = BackupService.CreateBackup(database, 20, "valid",
            now.AddMinutes(-10)) ?? throw new InvalidOperationException("backup missing");
        BackupEntry newest = BackupService.CreateBackup(database, 20, "corrupt", now)
            ?? throw new InvalidOperationException("backup missing");
        File.WriteAllBytes(newest.Path,
            File.ReadAllBytes(TestEnvironment.Fixture("broken_random_data.kdbx")));
        if (CoreValidation.LibraryPath is null)
        {
            check("recovery: the real core picks the backup that decrypts", false,
                "keepassity_ffi was not found; build with " +
                "cargo build -p keepassity-ffi --release");
            return;
        }

        Func<byte[], Task<bool>> verifier = bytes => Task.FromResult(
            CoreValidation.OpensWithPassword(bytes, FixturePassword));
        RestorableBackup? found = VaultRecovery.FindRestorableBackupAsync(database, verifier)
            .GetAwaiter().GetResult();
        check("recovery: the real core picks the backup that decrypts",
            found is not null &&
            string.Equals(found.Entry.Path, older.Path, StringComparison.OrdinalIgnoreCase) &&
            CoreValidation.OpensWithPassword(found.Bytes, FixturePassword),
            found?.Entry.Path ?? "(none)");

        RestorableBackup? wrongPassword = VaultRecovery.FindRestorableBackupAsync(database,
            bytes => Task.FromResult(
                CoreValidation.OpensWithPassword(bytes, "not-the-master-password")))
            .GetAwaiter().GetResult();
        check("recovery: a backup for another password is not offered",
            wrongPassword is null);
    }

    private static Task<RestorableBackup?> Find(string database, byte[] valid)
        => VaultRecovery.FindRestorableBackupAsync(database, bytes =>
            Task.FromResult(bytes.SequenceEqual(valid)));

    private static (string Database, byte[] Valid) NewDamagedDatabase(
        string rootDirectory, string name)
    {
        string directory = Path.Combine(rootDirectory, name);
        Directory.CreateDirectory(directory);
        string database = Path.Combine(directory, "vault.kdbx");
        byte[] valid = File.ReadAllBytes(TestEnvironment.Fixture(FixtureName));
        // Recovery only inspects the backups, so the live file may already be the
        // damaged one; keeping it valid also proves recovery does not depend on it.
        File.WriteAllBytes(database, valid);
        return (database, valid);
    }
}
