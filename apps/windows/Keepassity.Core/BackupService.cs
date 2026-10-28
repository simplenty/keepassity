// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Keepassity.Mock;

public sealed class BackupEntry
{
    public required string Path { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required long Size { get; init; }

    public string Display =>
        $"{Timestamp.LocalDateTime:yyyy-MM-dd HH:mm:ss}  ·  {Size / 1024.0:F1} KB";

    public override string ToString() => Display;
}

/// <summary>Timestamped local backups under &lt;dbdir&gt;\.keepassity\backups\&lt;dbname&gt;\.</summary>
public static class BackupService
{
    public static string BackupDirectory(string databasePath)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(databasePath))!;
        return Path.Combine(directory, ".keepassity", "backups", Path.GetFileName(databasePath));
    }

    public static BackupEntry? CreateBackup(string databasePath, int retention = 20,
        string? reason = null)
    {
        if (!File.Exists(databasePath))
        {
            return null;
        }
        string directory = BackupDirectory(databasePath);
        Directory.CreateDirectory(directory);
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff", CultureInfo.InvariantCulture);
        string name = reason is null ? $"{stamp}.kdbx" : $"{stamp}_{Sanitize(reason)}.kdbx";
        string path = Path.Combine(directory, name);
        int suffix = 1;
        while (File.Exists(path))
        {
            path = Path.Combine(directory,
                Path.GetFileNameWithoutExtension(name) + $"-{suffix++}.kdbx");
        }
        File.Copy(databasePath, path);
        Prune(directory, retention);
        return Describe(path);
    }

    public static List<BackupEntry> ListBackups(string databasePath)
    {
        string directory = BackupDirectory(databasePath);
        if (!Directory.Exists(directory))
        {
            return new List<BackupEntry>();
        }
        return Directory.EnumerateFiles(directory, "*.kdbx")
            .Select(Describe)
            .Where(entry => entry is not null)
            .Cast<BackupEntry>()
            .OrderByDescending(entry => entry.Timestamp)
            .ToList();
    }

    /// <summary>Restore is two-step: back up the current file first, then replace it.</summary>
    public static void Restore(string databasePath, string backupPath, int retention = 20)
    {
        if (!File.Exists(backupPath))
        {
            throw new FileNotFoundException("Backup file is missing.", backupPath);
        }
        byte[] content = File.ReadAllBytes(backupPath);
        RestoreBytes(databasePath, content, retention);
    }

    public static void RestoreBytes(string databasePath, byte[] content, int retention = 20)
    {
        CreateBackup(databasePath, retention, "before-restore");
        string directory = Path.GetDirectoryName(Path.GetFullPath(databasePath))!;
        string temp = Path.Combine(directory,
            $".{Path.GetFileName(databasePath)}.restore-tmp-{Guid.NewGuid():N}");
        File.WriteAllBytes(temp, content);
        if (File.Exists(databasePath))
        {
            File.Replace(temp, databasePath, databasePath + ".keepassity-restore-bak", true);
        }
        else
        {
            File.Move(temp, databasePath);
        }
    }

    private static BackupEntry? Describe(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return new BackupEntry
            {
                Path = path,
                Timestamp = info.LastWriteTime,
                Size = info.Length,
            };
        }
        catch
        {
            return null;
        }
    }

    private static void Prune(string directory, int retention)
    {
        if (retention < 1)
        {
            retention = 1;
        }
        var files = Directory.EnumerateFiles(directory, "*.kdbx")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTime)
            .Skip(retention)
            .ToList();
        foreach (FileInfo file in files)
        {
            try
            {
                file.Delete();
            }
            catch
            {
                // A locked backup is kept; retention is best-effort.
            }
        }
    }

    private static string Sanitize(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '-');
        }
        return value.Length > 40 ? value[..40] : value;
    }
}
