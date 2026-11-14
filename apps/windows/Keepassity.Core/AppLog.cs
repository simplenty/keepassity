// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;

namespace Keepassity.Core;

/// <summary>
/// Minimal local file logger. Logs stay on the machine, never leave it, and never
/// contain vault content or secrets (callers pass only metadata and errors).
/// </summary>
public static class AppLog
{
    private const int RetentionDays = 14;
    private static readonly object Gate = new();
    private static bool _initialized;
    private static string? _directory;
    private static LogFile? _log;

    public static string DirectoryPath
    {
        get
        {
            if (_directory is not null)
            {
                return _directory;
            }
            return Path.Combine(ApplicationPaths.Root, "logs");
        }
    }

    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }
        _initialized = true;
        string directory = Path.Combine(ApplicationPaths.Root, "logs");
        try
        {
            Directory.CreateDirectory(directory);
            _directory = directory;
            _log = new LogFile(directory);
            _log.Prune(DateTimeOffset.Now, RetentionDays);
        }
        catch
        {
            _directory = null;
            _log = null;
        }
        Info($"application started (pid {Environment.ProcessId})");
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message) => Write("WARN", message, null);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        if (!_initialized)
        {
            Initialize();
        }
        string? directory = _directory;
        LogFile? log = _log;
        if (directory is null || log is null)
        {
            return;
        }
        var line = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"))
            .Append(" [").Append(level).Append("] ")
            .Append(message);
        if (exception is not null)
        {
            line.AppendLine().Append(exception);
        }
        try
        {
            lock (Gate)
            {
                log.Append(line.ToString(), DateTimeOffset.Now);
            }
        }
        catch
        {
            // Logging must never break the application.
        }
    }
}
