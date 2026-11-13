// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Keepassity.Core;

namespace Keepassity.CoreTests;

/// <summary>
/// M4-05: backup retention boundary and restore ordering, using real KDBX
/// fixtures in temporary directories. The corrupted-backup check reproduces the
/// application's restore guard: the WinUI restore path opens the selected backup
/// with the current master password before calling BackupService.RestoreBytes,
/// and aborts on failure. The WinUI project cannot be referenced headlessly, so
/// the same C ABI validation call is used here to prove a corrupted backup is
/// rejected and the live database is left untouched.
/// </summary>
internal static class BackupServiceTests
{
    private const string FixtureName = "test_db_kdbx4_with_password_aes.kdbx";
    private const string FixturePassword = "demopass";
    private const int DefaultRetention = 20;

    public static void Run(TestCheck check, string rootDirectory)
    {
        RunRetentionBoundary(check, rootDirectory);
        RunDefaultRetention(check, rootDirectory);
        RunRetentionClamp(check, rootDirectory);
        RunRestore(check, rootDirectory);
        RunCorruptedRestoreRejected(check, rootDirectory);
    }

    private static void RunRetentionBoundary(TestCheck check, string root)
    {
        string directory = NewDirectory(root, "backup-retention-boundary");
        string database = Path.Combine(directory, "vault.kdbx");
        byte[] content = File.ReadAllBytes(TestEnvironment.Fixture(FixtureName));
        File.WriteAllBytes(database, content);
        DateTime baseTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);
        const int retention = 4;
        var created = new List<string>();
        for (int index = 0; index < retention + 3; index++)
        {
            File.SetLastWriteTime(database, baseTime.AddMinutes(index));
            BackupEntry? backup = BackupService.CreateBackup(database, retention);
            check($"retention: backup {index + 1} created", backup is not null);
            if (backup is null)
            {
                return;
            }
            created.Add(backup.Path);
            // File.Copy may preserve the source timestamp, so pin each backup's
            // LastWriteTime to make the pruning order deterministic.
            File.SetLastWriteTime(backup.Path, baseTime.AddMinutes(index));
            int count = BackupService.ListBackups(database).Count;
            check($"retention: at most {retention} backups kept after {index + 1} create(s)",
                count == Math.Min(index + 1, retention),
                count.ToString(CultureInfo.InvariantCulture));
        }
        List<BackupEntry> listed = BackupService.ListBackups(database);
        check("retention: keeps exactly the configured number of backups",
            listed.Count == retention);
        var newest = created.Skip(created.Count - retention)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var kept = listed.Select(entry => entry.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        check("retention: keeps the newest backups", kept.SetEquals(newest),
            string.Join(", ", kept.Select(Path.GetFileName)));
        check("retention: prunes the oldest backups",
            created.Take(created.Count - retention).All(path => !File.Exists(path)));
        check("retention: lists backups newest first",
            listed.Zip(listed.Skip(1))
                .All(pair => pair.First.Timestamp >= pair.Second.Timestamp));
        check("retention: backup files copy the database bytes",
            listed.All(entry => File.ReadAllBytes(entry.Path).SequenceEqual(content)));
    }

    private static void RunDefaultRetention(TestCheck check, string root)
    {
        string directory = NewDirectory(root, "backup-retention-default");
        string database = Path.Combine(directory, "vault.kdbx");
        File.WriteAllBytes(database,
            File.ReadAllBytes(TestEnvironment.Fixture(FixtureName)));
        DateTime baseTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);
        var created = new List<string>();
        for (int index = 0; index <= DefaultRetention; index++)
        {
            File.SetLastWriteTime(database, baseTime.AddMinutes(index));
            BackupEntry? backup = BackupService.CreateBackup(database);
            check($"default retention: backup {index + 1} created", backup is not null);
            if (backup is null)
            {
                return;
            }
            created.Add(backup.Path);
            File.SetLastWriteTime(backup.Path, baseTime.AddMinutes(index));
        }
        List<BackupEntry> listed = BackupService.ListBackups(database);
        check("default retention: keeps 20 backups",
            listed.Count == DefaultRetention,
            listed.Count.ToString(CultureInfo.InvariantCulture));
        check("default retention: prunes the oldest backup", !File.Exists(created[0]));
        var newest = created.Skip(1).ToHashSet(StringComparer.OrdinalIgnoreCase);
        check("default retention: keeps exactly the 20 newest backups",
            listed.Select(entry => entry.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(newest));
    }

    private static void RunRetentionClamp(TestCheck check, string root)
    {
        string directory = NewDirectory(root, "backup-retention-clamp");
        string database = Path.Combine(directory, "vault.kdbx");
        File.WriteAllBytes(database,
            File.ReadAllBytes(TestEnvironment.Fixture(FixtureName)));
        DateTime baseTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);
        for (int index = 0; index < 3; index++)
        {
            File.SetLastWriteTime(database, baseTime.AddMinutes(index));
            // Retention values below one are clamped to one backup, never zero.
            BackupEntry? backup = BackupService.CreateBackup(database, retention: 0);
            if (backup is not null)
            {
                File.SetLastWriteTime(backup.Path, baseTime.AddMinutes(index));
            }
            check("retention: value below one is clamped to a single backup",
                BackupService.ListBackups(database).Count == 1,
                BackupService.ListBackups(database).Count.ToString(
                    CultureInfo.InvariantCulture));
        }
    }

    private static void RunRestore(TestCheck check, string root)
    {
        string directory = NewDirectory(root, "backup-restore");
        byte[] restoredContent =
            File.ReadAllBytes(TestEnvironment.Fixture(FixtureName));
        // A second public fixture (same test password) represents the state the
        // live database moved on to after the selected backup was taken.
        byte[] currentContent =
            File.ReadAllBytes(TestEnvironment.Fixture("test_db_with_password.kdbx"));
        string database = Path.Combine(directory, "vault.kdbx");
        File.WriteAllBytes(database, restoredContent);
        BackupEntry? selected = BackupService.CreateBackup(database, DefaultRetention);
        check("restore: fixture backup created", selected is not null);
        if (selected is null)
        {
            return;
        }
        File.WriteAllBytes(database, currentContent);

        check("restore: selected backup opens with the current password",
            CoreValidation.OpensWithPassword(File.ReadAllBytes(selected.Path),
                FixturePassword));
        check("restore: selected backup rejects a wrong password",
            !CoreValidation.OpensWithPassword(File.ReadAllBytes(selected.Path),
                "not-the-master-password"));
        check("restore: live database opens with the current password",
            CoreValidation.OpensWithPassword(currentContent, FixturePassword));

        BackupService.Restore(database, selected.Path, DefaultRetention);

        check("restore: live database now contains the backup bytes",
            File.ReadAllBytes(database).SequenceEqual(restoredContent));
        check("restore: restored database opens with the current password",
            CoreValidation.OpensWithPassword(File.ReadAllBytes(database),
                FixturePassword));
        List<BackupEntry> after = BackupService.ListBackups(database);
        BackupEntry? beforeRestore = after.FirstOrDefault(entry =>
            Path.GetFileName(entry.Path)
                .Contains("before-restore", StringComparison.Ordinal));
        check("restore: backs up the live database before replacing it",
            beforeRestore is not null,
            string.Join(", ", after.Select(entry => Path.GetFileName(entry.Path))));
        check("restore: the pre-restore backup holds the previous live bytes",
            beforeRestore is not null &&
            File.ReadAllBytes(beforeRestore.Path).SequenceEqual(currentContent));
        check("restore: keeps the selected backup file",
            after.Any(entry => string.Equals(entry.Path, selected.Path,
                StringComparison.OrdinalIgnoreCase)));
        check("restore: respects the retention limit",
            after.Count <= DefaultRetention,
            after.Count.ToString(CultureInfo.InvariantCulture));

        bool missingRejected = false;
        try
        {
            BackupService.Restore(database,
                Path.Combine(directory, "does-not-exist.kdbx"), DefaultRetention);
        }
        catch (FileNotFoundException)
        {
            missingRejected = true;
        }
        check("restore: missing backup file is rejected", missingRejected);
    }

    private static void RunCorruptedRestoreRejected(
        TestCheck check, string root)
    {
        string directory = NewDirectory(root, "backup-restore-corrupted");
        string database = Path.Combine(directory, "vault.kdbx");
        byte[] liveContent =
            File.ReadAllBytes(TestEnvironment.Fixture(FixtureName));
        File.WriteAllBytes(database, liveContent);
        if (CoreValidation.LibraryPath is null)
        {
            check("corrupted backup is rejected before restoring", false,
                "keepassity_ffi was not found; build with " +
                "cargo build -p keepassity-ffi --release");
            return;
        }

        byte[] corrupted = File.ReadAllBytes(
            TestEnvironment.Fixture("broken_random_data.kdbx"));
        byte[] truncated = liveContent[..(liveContent.Length / 2)];
        check("corrupted backup fails the current-password validation",
            !CoreValidation.OpensWithPassword(corrupted, FixturePassword));
        check("truncated backup fails the current-password validation",
            !CoreValidation.OpensWithPassword(truncated, FixturePassword));
        check("intact backup passes the current-password validation",
            CoreValidation.OpensWithPassword(liveContent, FixturePassword));
        check("intact backup fails validation with a wrong password",
            !CoreValidation.OpensWithPassword(liveContent, "not-the-master-password"));

        // The application validates the selected backup first and returns before
        // BackupService.RestoreBytes is reached; a rejected backup must not
        // change the live database or leave restores half-applied.
        bool validated = CoreValidation.OpensWithPassword(corrupted, FixturePassword);
        if (validated)
        {
            BackupService.RestoreBytes(database, corrupted, DefaultRetention);
        }
        check("rejected corrupted backup leaves the live database unchanged",
            File.ReadAllBytes(database).SequenceEqual(liveContent));
        check("rejected corrupted backup creates no restore backup",
            BackupService.ListBackups(database).Count == 0);
    }

    private static string NewDirectory(string rootDirectory, string name)
    {
        string directory = Path.Combine(rootDirectory, name);
        Directory.CreateDirectory(directory);
        return directory;
    }
}
