// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Compression;
using Keepassity.Core;

namespace Keepassity.CoreTests;

/// <summary>
/// The diagnostics export must stay readable, unique, durable and bounded: no
/// truncated archive under a real name, no collision when two exports happen in
/// the same second, and only the newest archives are kept.
/// </summary>
internal static class DiagnosticsServiceTests
{
    public static void Run(TestCheck check, string rootDirectory)
    {
        ExportWritesAReadableArchive(check, rootDirectory);
        RepeatedExportsDoNotCollide(check, rootDirectory);
        RetentionKeepsNewest(check, rootDirectory);
        StaleTempFilesAreRemoved(check, rootDirectory);
    }

    private static void ExportWritesAReadableArchive(TestCheck check, string rootDirectory)
    {
        string directory = NewDirectory(rootDirectory, "diagnostics-export");
        var settings = new AppSettings
        {
            Theme = "Light",
            LastDatabasePath = @"C:\private\vault.kdbx",
            WebDavUrl = "https://private.example/db.kdbx",
            WebDavUsername = "alice",
        };
        string path = DiagnosticsService.ExportCore(settings, directory,
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));

        check("diagnostics: export creates a timestamped archive",
            File.Exists(path) &&
            string.Equals(Path.GetFileName(path),
                "keepassity-diagnostics-20260102-030405-000.zip", StringComparison.Ordinal),
            Path.GetFileName(path));
        string summary = ReadEntry(path, "summary.txt");
        string snapshot = ReadEntry(path, "settings.json");
        check("diagnostics: summary and settings snapshot are inside the archive",
            summary.Contains("Keepassity diagnostics", StringComparison.Ordinal) &&
            summary.Contains("app version:", StringComparison.Ordinal) &&
            snapshot.Contains("\"theme\": \"Light\"", StringComparison.Ordinal),
            summary.Length.ToString());
        check("diagnostics: the archive never contains database paths, URLs or usernames",
            !snapshot.Contains("vault.kdbx", StringComparison.OrdinalIgnoreCase) &&
            !snapshot.Contains("private.example", StringComparison.OrdinalIgnoreCase) &&
            !snapshot.Contains("alice", StringComparison.OrdinalIgnoreCase) &&
            !snapshot.Contains("last_database_path", StringComparison.OrdinalIgnoreCase),
            snapshot);
        check("diagnostics: a finished export leaves no temporary file",
            Directory.GetFiles(directory, "*.tmp").Length == 0);
    }

    private static void RepeatedExportsDoNotCollide(TestCheck check, string rootDirectory)
    {
        string directory = NewDirectory(rootDirectory, "diagnostics-collision");
        var now = new DateTimeOffset(2026, 2, 3, 4, 5, 6, 700, TimeSpan.Zero);
        string first = DiagnosticsService.ExportCore(new AppSettings(), directory, now);
        string second = DiagnosticsService.ExportCore(new AppSettings(), directory, now);
        check("diagnostics: two exports in the same millisecond get distinct names",
            !string.Equals(first, second, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(first) && File.Exists(second),
            $"{Path.GetFileName(first)} / {Path.GetFileName(second)}");
    }

    private static void RetentionKeepsNewest(TestCheck check, string rootDirectory)
    {
        string directory = NewDirectory(rootDirectory, "diagnostics-retention");
        var baseTime = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int index = 0; index < 12; index++)
        {
            string fake = Path.Combine(directory,
                $"keepassity-diagnostics-2025{index + 1:00}01-000000-000.zip");
            File.WriteAllBytes(fake, new byte[] { 0x50, 0x4B });
            File.SetLastWriteTimeUtc(fake, baseTime.AddDays(index));
        }
        string newest = Path.Combine(directory,
            "keepassity-diagnostics-20251201-000000-000.zip");
        File.WriteAllBytes(newest, new byte[] { 0x50, 0x4B });
        File.SetLastWriteTimeUtc(newest, baseTime.AddDays(20));

        string export = DiagnosticsService.ExportCore(new AppSettings(), directory,
            new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero));
        string[] archives = Directory.GetFiles(directory, "keepassity-diagnostics-*.zip");
        check("diagnostics: retention keeps only the newest archives",
            archives.Length == DiagnosticsService.KeepArchives && File.Exists(export),
            archives.Length.ToString());
        check("diagnostics: the oldest archive is removed by retention",
            !File.Exists(Path.Combine(directory,
                "keepassity-diagnostics-20250101-000000-000.zip")));
        check("diagnostics: the newest pre-existing archive survives retention",
            File.Exists(newest));
    }

    private static void StaleTempFilesAreRemoved(TestCheck check, string rootDirectory)
    {
        string directory = NewDirectory(rootDirectory, "diagnostics-temp");
        string stale = Path.Combine(directory, "keepassity-diagnostics-stale.zip.tmp");
        File.WriteAllBytes(stale, new byte[] { 1, 2, 3 });
        _ = DiagnosticsService.ExportCore(new AppSettings(), directory,
            new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        check("diagnostics: a crash-left temporary archive is cleaned up",
            !File.Exists(stale));
    }

    private static string ReadEntry(string archivePath, string entryName)
    {
        using FileStream stream = File.OpenRead(archivePath);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        ZipArchiveEntry entry = zip.GetEntry(entryName)
            ?? throw new InvalidOperationException($"missing {entryName}");
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static string NewDirectory(string rootDirectory, string name)
    {
        string directory = Path.Combine(rootDirectory,
            name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        return directory;
    }
}
