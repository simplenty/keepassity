// SPDX-License-Identifier: GPL-3.0-or-later
namespace Keepassity.Core;

/// <summary>
/// Durable, verified-in-placement file replacement: write a sibling temporary
/// file with write-through and an explicit disk flush, take a timestamped backup
/// of the previous file, then replace atomically. The live file is never opened
/// for partial writes, so a crash leaves either the old or the new database.
/// </summary>
public static class AtomicFile
{
    /// <summary>
    /// Marker between the database path and the random nonce. This is the same
    /// convention as the core save protocol, so a crash-left temporary file can
    /// be identified by either layer.
    /// </summary>
    public const string TempInfix = ".keepassity-tmp-";

    /// <summary>
    /// Replace <paramref name="path"/> with <paramref name="content"/>, keeping
    /// a backup of the previous file when it exists.
    /// </summary>
    public static void Replace(string path, byte[] content, int backupRetention = 20,
        string reason = "save")
    {
        if (content is null || content.Length == 0)
        {
            throw new ArgumentException("Refusing to write an empty file.", nameof(content));
        }
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory,
            Path.GetFileName(fullPath) + TempInfix + Guid.NewGuid().ToString("N"));
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(fullPath))
            {
                BackupService.CreateBackup(fullPath, backupRetention, reason);
                File.Replace(temp, fullPath, null, true);
            }
            else
            {
                File.Move(temp, fullPath);
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
                    // The replacement is what matters; a scanner may still hold
                    // the temporary file, which is harmless and clearly named.
                }
            }
        }
    }

    /// <summary>
    /// Remove temporary files left by a crash for <paramref name="path"/>. Only
    /// names matching the protocol are touched; the live database and unrelated
    /// files are never deleted. Legacy dot-prefixed names from older builds are
    /// included.
    /// </summary>
    public static int CleanupResidualTemps(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return 0;
        }
        string name = Path.GetFileName(fullPath);
        string[] candidates =
        {
            name + TempInfix,
            "." + name + TempInfix,
        };
        int removed = 0;
        foreach (string prefix in candidates)
        {
            foreach (string candidate in Directory.EnumerateFiles(directory, prefix + "*"))
            {
                // A concurrent save would have the file open with FileShare.None;
                // deletion fails and the next cleanup retries. The vault lock
                // means this should not happen during a normal save.
                try
                {
                    File.Delete(candidate);
                    removed++;
                }
                catch
                {
                    // Leave it; it is clearly named and not the live file.
                }
            }
        }
        return removed;
    }

    /// <summary>
    /// Write small configuration/state files durably without creating a backup.
    /// Uses the same flushed temporary file and atomic move as a database save.
    /// </summary>
    public static void Write(string path, byte[] content)
    {
        if (content is null || content.Length == 0)
        {
            throw new ArgumentException("Refusing to write an empty file.", nameof(content));
        }
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory,
            Path.GetFileName(fullPath) + TempInfix + Guid.NewGuid().ToString("N"));
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, fullPath, true);
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
                    // The previous file is still intact; a clearly named
                    // temporary file is harmless.
                }
            }
        }
    }
}
