// SPDX-License-Identifier: GPL-3.0-or-later
using Keepassity.Core;

namespace Keepassity.CoreTests;

/// <summary>
/// The run marker separates a clean close from a crash/power loss, so the next
/// launch can point the user at the backups. Headless checks inject the process
/// probe instead of querying the real process table.
/// </summary>
internal static class RunStateStoreTests
{
    public static void Run(TestCheck check, string rootDirectory)
    {
        CleanAndCrashedRuns(check, rootDirectory);
        LiveProcessIsNotACrash(check, rootDirectory);
        MalformedMarkerCountsAsACrash(check, rootDirectory);
        MarkIsAtomic(check, rootDirectory);
    }

    private static void CleanAndCrashedRuns(TestCheck check, string rootDirectory)
    {
        string directory = Path.Combine(rootDirectory,
            "run-state-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "run.json");
        var store = new RunStateStore(path, pid => pid == Environment.ProcessId);

        check("run state: a fresh data folder has no previous run",
            store.DetectPreviousCrash() is null);

        DateTimeOffset started = DateTimeOffset.UtcNow.AddMinutes(-5);
        store.MarkRunning(started);
        check("run state: marking writes the marker", File.Exists(path));
        check("run state: a marker for this live process is not a crash",
            store.DetectPreviousCrash() is null);

        store.Clear();
        check("run state: a clean close removes the marker", !File.Exists(path));
        check("run state: a clean close leaves no crash report",
            store.DetectPreviousCrash() is null);

        // A dead process id: the marker survived a crash.
        File.WriteAllText(path,
            "{\"Pid\":999999,\"StartedUnix\":" + started.ToUnixTimeSeconds() + "}");
        PreviousRun? crashed = store.DetectPreviousCrash();
        check("run state: a marker from a dead process is a crash report",
            crashed is not null && crashed.ProcessId == 999999 &&
            Math.Abs((crashed.StartedAt - started).TotalSeconds) < 1,
            crashed?.ToString() ?? "(none)");
        store.Clear();
    }

    private static void LiveProcessIsNotACrash(TestCheck check, string rootDirectory)
    {
        string path = Path.Combine(rootDirectory,
            "run-live-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        File.WriteAllText(path, "{\"Pid\":1234,\"StartedUnix\":1700000000}");
        var store = new RunStateStore(path, pid => pid == 1234);
        check("run state: a marker held by a live process is ignored",
            store.DetectPreviousCrash() is null);
        store.Clear();
    }

    private static void MalformedMarkerCountsAsACrash(TestCheck check, string rootDirectory)
    {
        string path = Path.Combine(rootDirectory,
            "run-corrupt-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        File.WriteAllText(path, "{not valid json");
        var store = new RunStateStore(path, _ => false);
        PreviousRun? crashed = store.DetectPreviousCrash();
        check("run state: a malformed marker is reported as a crash",
            crashed is not null && crashed.ProcessId == 0, crashed?.ToString() ?? "(none)");
        store.Clear();
        check("run state: clearing a malformed marker works", !File.Exists(path));
    }

    private static void MarkIsAtomic(TestCheck check, string rootDirectory)
    {
        string directory = Path.Combine(rootDirectory,
            "run-atomic-" + Guid.NewGuid().ToString("N")[..8]);
        string path = Path.Combine(directory, "nested", "run.json");
        var store = new RunStateStore(path, _ => true);
        store.MarkRunning();
        check("run state: marking creates the state folder and file",
            File.Exists(path) && store.DetectPreviousCrash() is null);
        check("run state: marking leaves no temporary file",
            Directory.GetFiles(Path.GetDirectoryName(path)!, "*keepassity-tmp*")
                .Length == 0);
        store.Clear();
    }
}
