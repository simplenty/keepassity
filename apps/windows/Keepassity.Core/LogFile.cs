// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;

namespace Keepassity.Core;

/// <summary>
/// One directory of daily log files. Appends are bounded: a day file that grows
/// past the size cap is rotated to a single <c>.1</c> sibling, and pruning keeps
/// both the retention window and a total-size ceiling. File handles share
/// read/write/delete so a second instance and the diagnostics exporter never lose
/// a write to a sharing violation.
/// </summary>
internal sealed class LogFile
{
    internal const long DefaultMaxFileBytes = 5 * 1024 * 1024;
    internal const long DefaultMaxTotalBytes = 50 * 1024 * 1024;

    private readonly string _directory;
    private readonly long _maxFileBytes;
    private readonly object _gate = new();

    public LogFile(string directory, long maxFileBytes = DefaultMaxFileBytes)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _maxFileBytes = maxFileBytes > 0
            ? maxFileBytes
            : throw new ArgumentOutOfRangeException(nameof(maxFileBytes));
    }

    public void Append(string line, DateTimeOffset now)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            string path = Path.Combine(_directory, $"app-{now:yyyyMMdd}.log");
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length >= _maxFileBytes)
                {
                    // Keep exactly one rotated sibling; a crash-loop or an error
                    // loop therefore cannot grow the folder without bound.
                    File.Move(path, path + ".1", overwrite: true);
                }
            }
            catch
            {
                // Rotation is best effort; the append below still records the line.
            }
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(line);
            writer.Write(Environment.NewLine);
        }
    }

    /// <summary>
    /// Delete logs older than <paramref name="retentionDays"/> and then the oldest
    /// remaining ones until the folder fits <paramref name="maxTotalBytes"/>.
    /// Returns the number of files removed.
    /// </summary>
    public int Prune(DateTimeOffset now, int retentionDays = 14,
        long maxTotalBytes = DefaultMaxTotalBytes)
    {
        int removed = 0;
        try
        {
            Directory.CreateDirectory(_directory);
            DateTime cutoff = now.AddDays(-retentionDays).UtcDateTime;
            List<FileInfo> files = Enumerate();
            foreach (FileInfo file in files)
            {
                if (file.LastWriteTimeUtc >= cutoff)
                {
                    continue;
                }
                if (TryDelete(file))
                {
                    removed++;
                }
            }

            files = Enumerate();
            long total = files.Sum(file => file.Length);
            foreach (FileInfo file in files.OrderBy(file => file.LastWriteTimeUtc))
            {
                if (total <= maxTotalBytes)
                {
                    break;
                }
                long length = file.Length;
                if (TryDelete(file))
                {
                    total -= length;
                    removed++;
                }
            }
        }
        catch
        {
            // Logging must never break the application.
        }
        return removed;
    }

    private List<FileInfo> Enumerate()
    {
        var files = new List<FileInfo>();
        files.AddRange(Directory.EnumerateFiles(_directory, "app-*.log")
            .Select(path => new FileInfo(path)));
        files.AddRange(Directory.EnumerateFiles(_directory, "app-*.log.1")
            .Select(path => new FileInfo(path)));
        return files;
    }

    private static bool TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
