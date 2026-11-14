// SPDX-License-Identifier: GPL-3.0-or-later
using Keepassity.Core;

namespace Keepassity.CoreTests;

/// <summary>
/// Durable replacement contract: write-through temporary file, backup, atomic
/// replace, no stray temporary or .bak files, and an empty payload is refused
/// without touching the live file.
/// </summary>
internal static class AtomicFileTests
{
    public static void Run(TestCheck check, string rootDirectory)
    {
        string directory = Path.Combine(rootDirectory, "atomic-file");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "vault.kdbx");
        byte[] first = "first-version"u8.ToArray();
        byte[] second = "second-version"u8.ToArray();

        AtomicFile.Replace(path, first, backupRetention: 5, reason: "test");
        check("atomic write creates the file", File.Exists(path) &&
            File.ReadAllBytes(path).SequenceEqual(first));
        check("atomic write leaves no temporary file",
            Directory.GetFiles(directory, "*.keepassity-tmp-*").Length == 0,
            string.Join(", ", Directory.GetFiles(directory)));

        AtomicFile.Replace(path, second, backupRetention: 5, reason: "test");
        check("atomic replace writes the new bytes",
            File.ReadAllBytes(path).SequenceEqual(second));
        check("atomic replace leaves no .bak file",
            Directory.GetFiles(directory, "*.keepassity-bak").Length == 0);
        check("atomic replace leaves no temporary file after replace",
            Directory.GetFiles(directory, "*.keepassity-tmp-*").Length == 0);
        List<BackupEntry> backups = BackupService.ListBackups(path);
        check("atomic replace backs up the previous bytes", backups.Count == 1 &&
            File.ReadAllBytes(backups[0].Path).SequenceEqual(first),
            backups.Count.ToString());

        bool emptyRejected = false;
        try
        {
            AtomicFile.Replace(path, Array.Empty<byte>(), 5, "test");
        }
        catch (ArgumentException)
        {
            emptyRejected = true;
        }
        check("atomic replace refuses an empty payload", emptyRejected);
        check("refused atomic replace keeps the live bytes",
            File.ReadAllBytes(path).SequenceEqual(second));

        string config = Path.Combine(directory, "settings.json");
        byte[] firstConfig = "{\"theme\":\"Light\"}"u8.ToArray();
        byte[] secondConfig = "{\"theme\":\"Dark\"}"u8.ToArray();
        AtomicFile.Write(config, firstConfig);
        check("atomic config write creates the file",
            File.ReadAllBytes(config).SequenceEqual(firstConfig));
        AtomicFile.Write(config, secondConfig);
        check("atomic config write replaces the file",
            File.ReadAllBytes(config).SequenceEqual(secondConfig));
        check("atomic config write leaves no backup or temporary file",
            Directory.GetFiles(directory, "*.keepassity-bak").Length == 0 &&
            Directory.GetFiles(directory, "*.keepassity-tmp-*").Length == 0);

        string created = Path.Combine(directory, "created.kdbx");
        byte[] createdBytes = "new-database"u8.ToArray();
        AtomicFile.WriteNew(created, createdBytes);
        check("create-only write creates the file",
            File.ReadAllBytes(created).SequenceEqual(createdBytes));
        check("create-only write leaves no temporary file",
            Directory.GetFiles(directory, "created.kdbx.keepassity-tmp-*").Length == 0);
        File.WriteAllBytes(created, "existing"u8.ToArray());
        bool createOnlyRefused = false;
        try
        {
            AtomicFile.WriteNew(created, "replacement"u8.ToArray());
        }
        catch (IOException)
        {
            createOnlyRefused = true;
        }
        check("create-only write refuses an existing file", createOnlyRefused);
        check("a refused create-only write keeps the existing bytes",
            File.ReadAllBytes(created).SequenceEqual("existing"u8.ToArray()));
        check("a refused create-only write leaves no temporary file",
            Directory.GetFiles(directory, "created.kdbx.keepassity-tmp-*").Length == 0);

        string live = Path.Combine(directory, "vault.kdbx");
        File.WriteAllBytes(live, "live"u8.ToArray());
        string temp = live + AtomicFile.TempInfix + "abc";
        string legacy = Path.Combine(directory, ".vault.kdbx" + AtomicFile.TempInfix + "old");
        string other = Path.Combine(directory, "other.kdbx" + AtomicFile.TempInfix + "x");
        File.WriteAllBytes(temp, "partial"u8.ToArray());
        File.WriteAllBytes(legacy, "partial"u8.ToArray());
        File.WriteAllBytes(other, "unrelated"u8.ToArray());
        int removed = AtomicFile.CleanupResidualTemps(live);
        check("residual save temps are removed", removed == 2 && !File.Exists(temp) &&
            !File.Exists(legacy), removed.ToString());
        check("residual cleanup keeps the live database",
            File.Exists(live) && File.ReadAllBytes(live).SequenceEqual("live"u8.ToArray()));
        check("residual cleanup keeps another database's temp",
            File.Exists(other), other);

        string copySource = Path.Combine(directory, "copy-source.bin");
        string copyTarget = Path.Combine(directory, "copy-target.bin");
        File.WriteAllBytes(copySource, "conflict-copy"u8.ToArray());
        AtomicFile.Copy(copySource, copyTarget);
        check("atomic copy writes the source bytes",
            File.ReadAllBytes(copyTarget).SequenceEqual("conflict-copy"u8.ToArray()));
        check("atomic copy leaves no temporary file",
            Directory.GetFiles(directory, "copy-target.bin.keepassity-tmp-*").Length == 0);

        // File.Replace is unavailable on FAT/exFAT and some network shares; the
        // same-volume rename fallback must still place the bytes and leave no
        // temporary file behind.
        string fallbackTarget = Path.Combine(directory, "fallback.kdbx");
        byte[] fallbackOld = "old-bytes"u8.ToArray();
        byte[] fallbackNew = "new-bytes"u8.ToArray();
        File.WriteAllBytes(fallbackTarget, fallbackOld);
        string fallbackTemp = fallbackTarget + AtomicFile.TempInfix + "fallback";
        File.WriteAllBytes(fallbackTemp, fallbackNew);
        AtomicFile.ReplaceFlushedTemp(fallbackTemp, fallbackTarget, allowFileReplace: false);
        check("replace fallback writes the new bytes",
            File.ReadAllBytes(fallbackTarget).SequenceEqual(fallbackNew));
        check("replace fallback leaves no temporary file", !File.Exists(fallbackTemp));
        string createdTarget = Path.Combine(directory, "fallback-created.kdbx");
        string createdTemp = createdTarget + AtomicFile.TempInfix + "created";
        File.WriteAllBytes(createdTemp, fallbackNew);
        AtomicFile.ReplaceFlushedTemp(createdTemp, createdTarget, allowFileReplace: false);
        check("replace fallback creates a missing destination",
            File.ReadAllBytes(createdTarget).SequenceEqual(fallbackNew) &&
            !File.Exists(createdTemp));
    }
}
