// SPDX-License-Identifier: GPL-3.0-or-later
using Keepassity.Core;

namespace Keepassity.CoreTests;

/// <summary>
/// The logger must never grow without bound and must never lose a line because
/// another process (or the diagnostics exporter) holds the file: daily files are
/// rotated at a size cap, pruning honours retention and a total-size ceiling, and
/// all handles share read/write/delete.
/// </summary>
internal static class LogFileTests
{
    public static void Run(TestCheck check, string rootDirectory)
    {
        AppendsAndShares(check, rootDirectory);
        RotatesAtTheSizeCap(check, rootDirectory);
        PrunesOldFilesAndHonoursTotalSize(check, rootDirectory);
    }

    private static void AppendsAndShares(TestCheck check, string rootDirectory)
    {
        string directory = NewDirectory(rootDirectory, "log-append");
        var first = new LogFile(directory);
        var second = new LogFile(directory);
        DateTimeOffset now = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        first.Append("first writer line", now);
        second.Append("second writer line", now);
        string path = Path.Combine(directory, "app-20260102.log");
        string content;
        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete))
        using (var text = new StreamReader(reader))
        {
            content = text.ReadToEnd();
        }
        check("log: two writers append to the same daily file",
            content.Contains("first writer line", StringComparison.Ordinal) &&
            content.Contains("second writer line", StringComparison.Ordinal), content);
        first.Append("readable while a reader is open", now);
        second.Append("and the reader still allows writes", now);
        check("log: a second append works while a reader holds the file",
            File.ReadAllText(path).Contains("still allows writes", StringComparison.Ordinal));
    }

    private static void RotatesAtTheSizeCap(TestCheck check, string rootDirectory)
    {
        string directory = NewDirectory(rootDirectory, "log-rotate");
        var log = new LogFile(directory, maxFileBytes: 10);
        DateTimeOffset now = new(2026, 2, 3, 4, 5, 6, TimeSpan.Zero);
        log.Append("1234567890", now);
        log.Append("abcdefghij", now);
        string path = Path.Combine(directory, "app-20260203.log");
        string rotated = path + ".1";
        check("log: a file that reaches the size cap rotates to one sibling",
            File.Exists(rotated) && File.Exists(path), rotated);
        check("log: the rotated file keeps the earlier line and the new file the latest",
            File.ReadAllText(rotated).Contains("1234567890", StringComparison.Ordinal) &&
            !File.ReadAllText(rotated).Contains("abcdefghij", StringComparison.Ordinal) &&
            File.ReadAllText(path).Contains("abcdefghij", StringComparison.Ordinal));
        log.Append("third line", now);
        check("log: rotation keeps at most one sibling",
            Directory.GetFiles(directory, "app-*.log.1").Length == 1,
            string.Join(", ", Directory.GetFiles(directory)));
    }

    private static void PrunesOldFilesAndHonoursTotalSize(TestCheck check,
        string rootDirectory)
    {
        string directory = NewDirectory(rootDirectory, "log-prune");
        var log = new LogFile(directory);
        DateTimeOffset now = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        string old = Path.Combine(directory, "app-20250101.log");
        string oldRotated = Path.Combine(directory, "app-20250101.log.1");
        string fresh = Path.Combine(directory, "app-20260531.log");
        File.WriteAllText(old, "old");
        File.WriteAllText(oldRotated, "old-rotated");
        File.WriteAllText(fresh, "fresh");
        File.SetLastWriteTimeUtc(old, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(oldRotated,
            new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(fresh, new DateTime(2026, 5, 31, 0, 0, 0, DateTimeKind.Utc));

        int removed = log.Prune(now, retentionDays: 14);
        check("log: pruning removes files past the retention window",
            removed == 2 && !File.Exists(old) && !File.Exists(oldRotated) &&
            File.Exists(fresh), removed.ToString());

        string sizeDirectory = NewDirectory(rootDirectory, "log-size-cap");
        var sizeLog = new LogFile(sizeDirectory);
        var names = new List<string>();
        for (int index = 0; index < 3; index++)
        {
            string name = Path.Combine(sizeDirectory, $"app-2026060{index + 1}.log");
            File.WriteAllBytes(name, new byte[100]);
            File.SetLastWriteTimeUtc(name,
                new DateTime(2026, 6, index + 1, 0, 0, 0, DateTimeKind.Utc));
            names.Add(name);
        }
        int sizeRemoved = sizeLog.Prune(now, retentionDays: 365, maxTotalBytes: 150);
        check("log: pruning enforces a total-size ceiling from the oldest files",
            sizeRemoved == 2 && !File.Exists(names[0]) && !File.Exists(names[1]) &&
            File.Exists(names[2]), sizeRemoved.ToString());
    }

    private static string NewDirectory(string rootDirectory, string name)
    {
        string directory = Path.Combine(rootDirectory,
            name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        return directory;
    }
}
