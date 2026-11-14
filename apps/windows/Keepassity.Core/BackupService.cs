// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Keepassity.Core;

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
        string? reason = null, DateTimeOffset? timestamp = null)
    {
        if (!File.Exists(databasePath))
        {
            return null;
        }
        string directory = BackupDirectory(databasePath);
        Directory.CreateDirectory(directory);
        DateTimeOffset created = timestamp ?? DateTimeOffset.Now;
        string stamp = created.ToLocalTime().ToString("yyyy-MM-dd_HH-mm-ss-fff",
            CultureInfo.InvariantCulture);
        string name = reason is null ? $"{stamp}.kdbx" : $"{stamp}_{Sanitize(reason)}.kdbx";
        string path = Path.Combine(directory, name);
        int suffix = 1;
        while (File.Exists(path))
        {
            path = Path.Combine(directory,
                Path.GetFileNameWithoutExtension(name) + $"-{suffix++}.kdbx");
        }

        // Copy to a temporary sibling, flush it, then move it into place. A crash
        // during the copy therefore cannot leave a truncated file with a real
        // backup name that a later restore might choose.
        string temp = Path.Combine(directory,
            $".{Path.GetFileName(path)}.tmp-{Guid.NewGuid():N}");
        try
        {
            File.Copy(databasePath, temp, overwrite: false);
            FlushToDisk(temp);
            File.Move(temp, path);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another backup won the race for this name; pick the next suffix
            // and copy again instead of overwriting it.
            File.Delete(temp);
            return CreateBackup(databasePath, retention, reason, timestamp);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch
                {
                    // A clearly named temporary file is safer than deleting the
                    // wrong path or leaving a partially named backup.
                }
            }
        }

        Prune(directory, retention, path);
        return Describe(path);
    }

    public static List<BackupEntry> ListBackups(string databasePath)
    {        string directory = BackupDirectory(databasePath);
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

    /// <summary>
    /// Removes temporary files a crash left inside the backup folder (a backup
    /// interrupted mid-copy). Only names produced by this service are touched and
    /// only when they are older than <paramref name="olderThan"/>, so an active
    /// backup is never disturbed.
    /// </summary>
    public static int CleanupResidualTemps(string databasePath, TimeSpan olderThan)
    {
        string directory = BackupDirectory(databasePath);
        if (!Directory.Exists(directory))
        {
            return 0;
        }
        DateTime cutoff = DateTime.UtcNow - olderThan;
        int removed = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*.tmp-*"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) > cutoff)
                    {
                        continue;
                    }
                    File.Delete(file);
                    removed++;
                }
                catch
                {
                    // Leave it; the next unlock retries.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"backup temporary cleanup failed: {ex.Message}");
        }
        return removed;
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
        if (content is null || content.Length == 0)
        {
            throw new ArgumentException("The backup contains no data.", nameof(content));
        }
        CreateBackup(databasePath, retention, "before-restore");
        string directory = Path.GetDirectoryName(Path.GetFullPath(databasePath))!;
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory,
            $".{Path.GetFileName(databasePath)}.restore-tmp-{Guid.NewGuid():N}");
        File.WriteAllBytes(temp, content);
        FlushToDisk(temp);
        try
        {
            if (File.Exists(databasePath))
            {
                // The pre-restore backup above is the recovery point; a second
                // ".bak" file next to the database would only be clutter.
                AtomicFile.ReplaceFlushedTemp(temp, databasePath);
            }
            else
            {
                File.Move(temp, databasePath);
            }
        }
        finally
        {
            if (File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch
                {
                    // The replacement already happened or a scanner holds the
                    // temporary file; the live database is what matters.
                }
            }
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
                Timestamp = BackupTimestamp(path),
                Size = info.Length,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Read the creation timestamp from the backup name instead of the file's
    /// LastWriteTime. File.Copy preserves the source database's timestamp, so a
    /// name is the only reliable ordering key across filesystems and clones.
    /// </summary>
    private static DateTimeOffset BackupTimestamp(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        string stamp = name.Length >= 23 ? name[..23] : name;
        if (DateTime.TryParseExact(stamp, "yyyy-MM-dd_HH-mm-ss-fff",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime local))
        {
            return new DateTimeOffset(local);
        }
        try
        {
            return new FileInfo(path).LastWriteTime;
        }
        catch
        {
            return DateTimeOffset.MinValue;
        }
    }

    private static void Prune(string directory, int retention, string keepPath)
    {
        retention = Math.Max(1, retention);
        string fullKeep = Path.GetFullPath(keepPath);
        var files = Directory.EnumerateFiles(directory, "*.kdbx")
            .Where(path => !string.Equals(Path.GetFullPath(path), fullKeep,
                StringComparison.OrdinalIgnoreCase))
            .Select(path => new
            {
                Path = path,
                Timestamp = BackupTimestamp(path),
            })
            .OrderByDescending(file => file.Timestamp)
            .ThenByDescending(file => Path.GetFileName(file.Path), StringComparer.Ordinal)
            .Skip(retention - 1)
            .ToList();
        foreach (var file in files)
        {
            try
            {
                File.Delete(file.Path);
            }
            catch
            {
                // A locked backup is kept; retention is best-effort.
            }
        }
    }

    private static void FlushToDisk(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
            FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Flush(flushToDisk: true);
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
