// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Keepassity.Mock;

/// <summary>
/// Builds a small diagnostics archive the user can inspect before sharing it.
/// It contains logs, environment facts and non-sensitive settings; it never
/// includes vault bytes, passwords, TOTP seeds or stored credentials.
/// </summary>
public static class DiagnosticsService
{
    public static async Task<string> ExportAsync(AppSettings settings)
    {
        string directory = Path.Combine(AppDataPaths.Root, "diagnostics");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(
            directory, $"keepassity-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        string temp = path + ".tmp";
        if (File.Exists(temp))
        {
            File.Delete(temp);
        }
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
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
            File.Move(temp, path);
            AppLog.Info("diagnostics exported");
            return path;
        }
        catch
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
            throw;
        }
    }

    private static string BuildSummary(AppSettings settings)
    {
        var assembly = typeof(DiagnosticsService).Assembly;
        string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? assembly.GetName().Version?.ToString() ?? "unknown";
        string syncStates = CountFiles(Path.Combine(AppDataPaths.Root, "state"), "*.json");
        string credentials = CountFiles(
            Path.Combine(AppDataPaths.Root, "credentials"), "*.bin");
        var summary = new StringBuilder()
            .AppendLine("keepassity diagnostics")
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
            using FileStream source = File.OpenRead(path);
            source.CopyTo(target);
        }
        catch
        {
            // A log file can be rotated between enumeration and opening.
        }
    }
}
