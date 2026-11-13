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
    }
}
