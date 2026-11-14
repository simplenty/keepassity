// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Text.Json;

namespace Keepassity.Core;

/// <summary>A run marker left behind by the previous process.</summary>
public sealed record PreviousRun(int ProcessId, DateTimeOffset StartedAt);

/// <summary>
/// Detects an unclean shutdown: a marker is written when the window starts and
/// removed on a normal close. A marker whose process is gone means the previous
/// process died (crash, power loss, forced kill), so the next launch can point
/// the user at the backups even when the database itself opens fine.
/// </summary>
public sealed class RunStateStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
    };

    private sealed class RunState
    {
        public int Pid { get; set; }
        public long StartedUnix { get; set; }
    }

    private readonly string _path;
    private readonly Func<int, bool> _isProcessRunning;

    /// <param name="path">Marker path; defaults to the application data root.</param>
    /// <param name="isProcessRunning">
    /// Injectable process probe for headless tests; defaults to the real process table.
    /// </param>
    public RunStateStore(string? path = null, Func<int, bool>? isProcessRunning = null)
    {
        _path = string.IsNullOrWhiteSpace(path)
            ? System.IO.Path.Combine(ApplicationPaths.Root, "state", "run.json")
            : System.IO.Path.GetFullPath(path);
        _isProcessRunning = isProcessRunning ?? ProcessIsRunning;
    }

    /// <summary>Absolute path of the marker file.</summary>
    public string MarkerPath => _path;

    /// <summary>
    /// The previous run that did not close cleanly, or null when the marker is
    /// missing or belongs to a still-running instance (for example a second
    /// window started with the test-only multiple-instance switch). A malformed
    /// marker counts as an unclean run: the previous process did not get to write
    /// a clean one.
    /// </summary>
    public PreviousRun? DetectPreviousCrash()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }
            RunState? state = null;
            try
            {
                state = JsonSerializer.Deserialize<RunState>(File.ReadAllText(_path));
            }
            catch (JsonException)
            {
                // Fall through: a damaged marker still means no clean close.
            }
            if (state is null || state.Pid <= 0)
            {
                return new PreviousRun(0, File.GetLastWriteTimeUtc(_path));
            }
            if (_isProcessRunning(state.Pid))
            {
                return null;
            }
            return new PreviousRun(state.Pid,
                DateTimeOffset.FromUnixTimeSeconds(state.StartedUnix));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"run state could not be read: {ex.Message}");
            return null;
        }
    }

    /// <summary>Write the marker for this process. Best effort.</summary>
    public void MarkRunning(DateTimeOffset? startedAt = null)
    {
        try
        {
            AtomicFile.Write(_path, JsonSerializer.SerializeToUtf8Bytes(new RunState
            {
                Pid = Environment.ProcessId,
                StartedUnix = (startedAt ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds(),
            }, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"run state could not be written: {ex.Message}");
        }
    }

    /// <summary>Remove the marker on a clean close. Best effort.</summary>
    public void Clear()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"run state could not be cleared: {ex.Message}");
        }
    }

    private static bool ProcessIsRunning(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
