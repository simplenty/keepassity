// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Keepassity.Core;

/// <summary>
/// Builds a small diagnostics archive the user can inspect before sharing it.
/// It contains logs, environment facts and non-sensitive settings; it never
/// includes vault bytes, passwords, TOTP seeds or stored credentials.
/// </summary>
public static class DiagnosticsService
{
    /// <summary>
    /// Builds the diagnostics archive on a worker thread; the UI calls this
    /// without blocking while the logs are zipped. Older archives beyond the
    /// newest <see cref="KeepArchives"/> are removed so repeated exports cannot
    /// grow the data folder without bound.
    /// </summary>
    public static Task<string> ExportAsync(AppSettings settings)
        => Task.Run(() => ExportCore(settings, null, null));

    /// <summary>Archive retention: the newest exports are kept, older ones removed.</summary>
    internal const int KeepArchives = 10;

    internal static string ExportCore(AppSettings settings, string? outputDirectory,
        DateTimeOffset? now)
    {
        string directory = outputDirectory ??
                           Path.Combine(ApplicationPaths.Root, "diagnostics");
        Directory.CreateDirectory(directory);
        DateTimeOffset stampTime = now ?? DateTimeOffset.Now;
        string stamp = stampTime.ToString("yyyyMMdd-HHmmss-fff",
            System.Globalization.CultureInfo.InvariantCulture);
        string path = UniqueArchivePath(directory, stamp);
        string temp = path + ".tmp";
        if (File.Exists(temp))
        {
            File.Delete(temp);
        }
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                // leaveOpen so the finished archive can be flushed to disk before
                // the rename: a crash must not leave a truncated archive under a
                // real diagnostics name.
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create,
                           leaveOpen: true))
                {
                    AddText(zip, "summary.txt", BuildSummary(settings));
                    AddText(zip, "settings.json", BuildSettingsSnapshot(settings));
                    foreach (string log in Directory
                                 .EnumerateFiles(AppLog.DirectoryPath, "app-*.log")
                                 .OrderByDescending(File.GetLastWriteTime)
                                 .Take(3))
                    {
                        AddFile(zip, "logs/" + Path.GetFileName(log), log);
                    }
                }
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path);
            Prune(directory);
            AppLog.Info("diagnostics exported");
            return path;
        }
        catch
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch
            {
                // Best effort: the temporary name is clearly not an archive.
            }
            throw;
        }
    }

    private static string UniqueArchivePath(string directory, string stamp)
    {
        string path = Path.Combine(directory, $"keepassity-diagnostics-{stamp}.zip");
        int suffix = 2;
        while (File.Exists(path) || File.Exists(path + ".tmp"))
        {
            path = Path.Combine(directory,
                $"keepassity-diagnostics-{stamp}-{suffix++}.zip");
        }
        return path;
    }

    private static void Prune(string directory)
    {
        try
        {
            // A crash-left temporary file has no value; remove it with the
            // retention pass instead of leaving it forever.
            foreach (string temp in Directory.EnumerateFiles(directory, "*.tmp"))
            {
                try
                {
                    File.Delete(temp);
                }
                catch
                {
                    // A file still being written by another export is kept.
                }
            }
            var archives = Directory.EnumerateFiles(directory,
                    "keepassity-diagnostics-*.zip")
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenByDescending(file => file.Name, StringComparer.Ordinal)
                .Skip(KeepArchives)
                .ToList();
            foreach (FileInfo archive in archives)
            {
                try
                {
                    archive.Delete();
                }
                catch
                {
                    // Retention is best effort; the next export retries.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"diagnostics retention failed: {ex.Message}");
        }
    }

    private static string BuildSummary(AppSettings settings)
    {
        var assembly = typeof(DiagnosticsService).Assembly;
        string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? assembly.GetName().Version?.ToString() ?? "unknown";
        string syncStates = CountFiles(Path.Combine(ApplicationPaths.Root, "state"), "*.json");
        string credentials = CountFiles(
            Path.Combine(ApplicationPaths.Root, "credentials"), "*.bin");
        var summary = new StringBuilder()
            .AppendLine("Keepassity diagnostics")
            .AppendLine($"app version: {version}")
            .AppendLine($"os: {Environment.OSVersion.VersionString}")
            .AppendLine($"process architecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}")
            .AppendLine($"runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}")
            .AppendLine($"settings schema version: {settings.SchemaVersion}")
            .AppendLine($"sync state files: {syncStates}")
            .AppendLine($"stored credentials: {credentials}")
            .AppendLine($"log directory: {AppLog.DirectoryPath}");
        return summary.ToString();
    }

    private static string BuildSettingsSnapshot(AppSettings settings)
    {
        // Deliberately excludes database paths, WebDAV URLs and usernames.
        var snapshot = new Dictionary<string, object?>
        {
            ["schema_version"] = settings.SchemaVersion,
            ["theme"] = settings.Theme,
            ["clear_clipboard_after_30_seconds"] = settings.ClearClipboardAfter30Seconds,
            ["auto_lock_enabled"] = settings.AutoLockEnabled,
            ["auto_lock_minutes"] = settings.AutoLockMinutes,
            ["sync_on_save"] = settings.SyncOnSave,
            ["backup_retention"] = settings.BackupRetention,
            ["start_minimized"] = settings.StartMinimized,
            ["last_database_kind"] = settings.LastDatabaseKind,
            ["has_last_database"] = !string.IsNullOrEmpty(settings.LastDatabasePath),
            ["has_webdav_target"] = !string.IsNullOrWhiteSpace(settings.WebDavUrl),
        };
        return JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
        {
            WriteIndented = true,
        });
    }

    private static string CountFiles(string directory, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(directory, pattern).Count().ToString();
        }
        catch
        {
            return "0";
        }
    }

    private static void AddText(ZipArchive zip, string name, string content)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using Stream stream = entry.Open();
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        writer.Write(content);
    }

    private static void AddFile(ZipArchive zip, string name, string path)
    {
        try
        {
            ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using Stream target = entry.Open();
            // The app may be appending to the newest log while diagnostics are
            // exported; share read/write/delete instead of failing the export.
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            source.CopyTo(target);
        }
        catch
        {
            // A log file can be rotated between enumeration and opening.
        }
    }
}
