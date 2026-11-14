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
    /// Place an already flushed temporary sibling at its destination.
    /// <see cref="File.Replace(string, string, string?, bool)"/> gives the best
    /// semantics (metadata and backup handling) but is unavailable on FAT/exFAT
    /// and some network shares, where it fails with an IO error. The fallback is
    /// a same-volume <see cref="File.Move(string, string, bool)"/>, which is also
    /// a rename: a crash leaves either the old or the new file, and the caller has
    /// already taken a backup.
    /// </summary>
    internal static void ReplaceFlushedTemp(string tempPath, string destinationPath,
        bool allowFileReplace = true)
    {
        if (allowFileReplace)
        {
            try
            {
                File.Replace(tempPath, destinationPath, null, ignoreMetadataErrors: true);
                return;
            }
            catch (Exception ex) when (ex is PlatformNotSupportedException or IOException)
            {
                AppLog.Warn($"File.Replace unavailable ({ex.Message}); falling back to a " +
                            "same-volume rename");
            }
        }
        File.Move(tempPath, destinationPath, overwrite: true);
    }

    /// <summary>
    /// Replace <paramref name="path"/> with <paramref name="content"/>, keeping
    /// a backup of the previous file when it exists.
    /// <paramref name="changedBeforeReplace"/> runs after the backup and
    /// immediately before the rename: returning true aborts with a
    /// <see cref="CoreException"/> so a file that another program changed during
    /// the save is not silently overwritten.
    /// </summary>
    public static void Replace(string path, byte[] content, int backupRetention = 20,
        string reason = "save", Func<bool>? changedBeforeReplace = null)
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
                if (changedBeforeReplace?.Invoke() == true)
                {
                    throw new CoreException("VaultChanged",
                        "the database changed outside this window");
                }
                ReplaceFlushedTemp(temp, fullPath);
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
            name + ".restore-tmp-",
            "." + name + ".restore-tmp-",
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
    /// Removes crash residue under the application data folder. Only files whose
    /// name carries one of the protocol infixes are considered, and only when
    /// they are older than <paramref name="olderThan"/> so a concurrent test
    /// instance's write is never disturbed. Returns the number removed.
    /// </summary>
    public static int CleanupResidualTempsInTree(string root, TimeSpan olderThan)
    {
        try
        {
            string fullRoot = Path.GetFullPath(root);
            if (!Directory.Exists(fullRoot))
            {
                return 0;
            }
            return CleanupResidualTempsInDirectory(fullRoot, DateTime.UtcNow - olderThan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"residual temporary cleanup failed: {ex.Message}");
            return 0;
        }
    }

    private static int CleanupResidualTempsInDirectory(string directory, DateTime cutoff)
    {
        int removed = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                string name = Path.GetFileName(file);
                bool ours = name.Contains(TempInfix, StringComparison.Ordinal) ||
                            name.Contains(".restore-tmp-", StringComparison.Ordinal) ||
                            name.Contains(".tmp-", StringComparison.Ordinal);
                if (!ours || File.GetLastWriteTimeUtc(file) > cutoff)
                {
                    continue;
                }
                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch
                {
                    // Leave it; the next launch retries.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"residual temporary cleanup could not list {directory}: {ex.Message}");
        }
        try
        {
            foreach (string child in Directory.EnumerateDirectories(directory))
            {
                removed += CleanupResidualTempsInDirectory(child, cutoff);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"residual temporary cleanup could not descend into {directory}: " +
                        ex.Message);
        }
        return removed;
    }

    /// <summary>
    /// Copy a file through the same flushed temporary and atomic move as a
    /// durable write. Used for user-visible conflict copies, which must never be
    /// left truncated after a crash.
    /// </summary>
    public static void Copy(string source, string destination)
    {
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("Source file is missing.", source);
        }
        Write(destination, File.ReadAllBytes(source));
    }

    /// <summary>
    /// Write a brand-new file without ever overwriting: the flushed temporary
    /// sibling is moved into place only when the target does not exist, so a
    /// file created by another program in the meantime is not replaced.
    /// </summary>
    public static void WriteNew(string path, byte[] content)
    {
        WriteCore(path, content, overwrite: false);
    }

    /// <summary>
    /// Write small configuration/state files durably without creating a backup.
    /// Uses the same flushed temporary file and atomic move as a database save.
    /// </summary>
    public static void Write(string path, byte[] content)
    {
        WriteCore(path, content, overwrite: true);
    }

    private static void WriteCore(string path, byte[] content, bool overwrite)
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
            if (overwrite)
            {
                File.Move(temp, fullPath, true);
            }
            else
            {
                // No overwrite flag: an existing target fails the move instead
                // of being replaced.
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
                    // The previous file is still intact; a clearly named
                    // temporary file is harmless.
                }
            }
        }
    }
}
