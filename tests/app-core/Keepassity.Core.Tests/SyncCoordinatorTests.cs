// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using Keepassity.Core;

namespace Keepassity.CoreTests;

/// <summary>
/// Behaviour of the sync coordinator, which owns the data-safety rules of a sync
/// run: verify before replacing, write before upload, commit after acceptance,
/// retry/back-off, conflict copies and dirty-on-failure. The fakes replace the
/// network and the native core so every rule can be asserted headlessly.
/// </summary>
internal static class SyncCoordinatorTests
{
    private const string Url = "https://example.test/db.kdbx";
    private const string User = "user";
    private const string Password = "master-password";

    public static void Run(TestCheck check, string dataDirectory)
    {
        FirstUpload(check, dataDirectory);
        UpToDate(check, dataDirectory);
        UpToDateNeedsBaseline(check, dataDirectory);
        StaleCleanBaseline(check, dataDirectory);
        Download(check, dataDirectory);
        RejectedDownload(check, dataDirectory);
        DownloadReplaceFailure(check, dataDirectory);
        UploadBackoff(check, dataDirectory);
        ExhaustedUploadMerges(check, dataDirectory);
        ConflictChoices(check, dataDirectory);
        ConflictCopiesAreReused(check, dataDirectory);
        LocalEditDuringSync(check, dataDirectory);
        LocalEditDuringUpload(check, dataDirectory);
        NothingQueued(check, dataDirectory);
    }

    // ---------------------------------------------------------------- scenarios

    private static void FirstUpload(TestCheck check, string dataDirectory)
    {
        Harness harness = new(dataDirectory, "first-upload", Local("local"));
        byte[] model = Local("model");
        harness.Core.ModelBytes = model;
        harness.Remote.Missing = true;

        SyncReport report = harness.RunAsync().GetAwaiter().GetResult();
        SyncState state = harness.State;

        check("sync uploads the first version when the remote file is missing",
            report.Outcome == SyncOutcome.Synced && harness.Remote.Uploaded.Count == 1 &&
            harness.Remote.Uploaded[0].SequenceEqual(model));
        check("first upload creates the file instead of overwriting",
            harness.Remote.IfMatch.Count == 1 && harness.Remote.IfMatch[0] is null);
        check("upload writes the database before uploading and commits last",
            string.Join(",", harness.Events) == "write,upload,commit",
            string.Join(",", harness.Events));
        check("upload records a clean baseline",
            state.LocalDirty == false && state.Sha256 == Sha(model) &&
            state.Etag == harness.Remote.Metadata?.ETag && state.Size == model.Length);
        check("upload reports the synced status", harness.LastStatus == "Synced",
            harness.LastStatus);
    }

    private static void UpToDate(TestCheck check, string dataDirectory)
    {
        Harness harness = new(dataDirectory, "up-to-date", Local("remote"));
        harness.Remote.Bytes = Local("remote");
        harness.SaveCleanState(harness.Remote.Bytes, "\"old\"");
        harness.Core.Decision = new SyncDecisionDto { Action = "up_to_date" };
        harness.Remote.Metadata = new WebDavFileInfo
        {
            ETag = "\"e2\"",
            Size = harness.Remote.Bytes.Length,
            ModifiedUnix = 700,
        };

        SyncReport report = harness.RunAsync().GetAwaiter().GetResult();
        SyncState state = harness.State;

        check("up-to-date sync writes nothing",
            harness.Document.Writes.Count == 0 && harness.Remote.Uploaded.Count == 0 &&
            harness.Remote.Downloads == 0 && report.Notice == SyncNotice.None);
        check("up-to-date sync adopts the remote baseline",
            state.Etag == "\"e2\"" && !state.LocalDirty && state.RemoteModifiedUnix == 700);
        check("up-to-date sync re-seeds and confirms the core baseline",
            harness.Core.Commits.Count == 2 &&
            harness.Core.Commits[0].Sha == Sha(harness.Remote.Bytes) &&
            harness.Core.Commits[1].Etag == "\"e2\"");
        check("up-to-date sync reports synced", harness.LastStatus == "Synced");
        check("a clean sidecar that matches the local file stays clean",
            !harness.Core.LastLocalDirty);

        Harness degraded = new(dataDirectory, "degraded", Local("remote"));
        degraded.Remote.Bytes = Local("remote");
        degraded.SaveCleanState(degraded.Remote.Bytes, "\"old\"");
        degraded.Core.Decision = new SyncDecisionDto
        {
            Action = "up_to_date",
            Degraded = true,
        };
        SyncReport degradedReport = degraded.RunAsync().GetAwaiter().GetResult();
        check("a degraded up-to-date sync says so",
            degradedReport.Degraded && degraded.LastStatus == "Synced (degraded)",
            degraded.LastStatus);
    }

    private static void UpToDateNeedsBaseline(TestCheck check, string dataDirectory)
    {
        Harness harness = new(dataDirectory, "needs-baseline", Local("local"));
        harness.Remote.Bytes = Local("remote");
        harness.Core.Decision = new SyncDecisionDto { Action = "up_to_date" };

        SyncReport report = harness.RunAsync().GetAwaiter().GetResult();
        SyncState state = harness.State;

        check("an up-to-date remote without a content hash is not trusted",
            report.Outcome == SyncOutcome.NeedsBaseline && state.LocalDirty);
        check("a missing baseline keeps the vault dirty and says why",
            harness.LastStatus == "Sync needs baseline" && harness.Core.Commits.Count == 0,
            harness.LastStatus);
    }

    private static void StaleCleanBaseline(TestCheck check, string dataDirectory)
    {
        Harness harness = new(dataDirectory, "stale-clean", Local("saved-after-crash"));
        harness.Remote.Bytes = Local("remote-stale");
        harness.SaveCleanState(harness.Remote.Bytes, "\"e1\"");
        harness.Core.Decision = new SyncDecisionDto { Action = "upload" };

        SyncReport report = harness.RunAsync().GetAwaiter().GetResult();

        // A crash between the database write and the sidecar write leaves a clean
        // sidecar pointing at the previous content. Treating that as synced would
        // silently drop the upload, so the coordinator compares the file first.
        check("a clean sidecar that disagrees with the local file is treated as dirty",
            harness.Core.LastLocalDirty && report.Outcome == SyncOutcome.Synced &&
            harness.Remote.Uploaded.Count == 1,
            $"dirty={harness.Core.LastLocalDirty}");
    }

    private static void Download(TestCheck check, string dataDirectory)
    {
        Harness harness = new(dataDirectory, "download", Local("local"));
        byte[] remote = Local("remote-new");
        harness.Remote.Bytes = remote;
        harness.Remote.Metadata = new WebDavFileInfo
        {
            ETag = "\"eN\"",
            Size = remote.Length,
            ModifiedUnix = 900,
        };
        harness.SaveCleanState(Local("remote-old"), "\"e-old\"");
        harness.MarkDirty();
        harness.Core.Decision = new SyncDecisionDto { Action = "download" };

        SyncReport report = harness.RunAsync().GetAwaiter().GetResult();
        SyncState state = harness.State;

        check("download verifies before it replaces the live database",
            harness.Events.SequenceEqual(new[]
            {
                "commit", "download", "verify", "write", "reload", "commit",
            }),
            string.Join(",", harness.Events));
        check("download applies the verified bytes",
            harness.Document.Writes.Count == 1 &&
            harness.Document.Writes[0].SequenceEqual(remote) &&
            harness.Document.Reloads == 1);
        check("download records the remote baseline",
            state.Sha256 == Sha(remote) && state.Etag == "\"eN\"" && !state.LocalDirty &&
            state.RemoteModifiedUnix == 900);
        check("download reports what changed",
            report.Notice == SyncNotice.Downloaded && harness.LastStatus == "Synced");

        Harness unchanged = new(dataDirectory, "download-unchanged", Local("remote-same"));
        unchanged.Remote.Bytes = Local("remote-same");
        unchanged.SaveCleanState(unchanged.Remote.Bytes, "\"e1\"");
        unchanged.Core.Decision = new SyncDecisionDto { Action = "download" };
        SyncReport unchangedReport = unchanged.RunAsync().GetAwaiter().GetResult();
        check("an unchanged download does not rewrite the vault",
            unchanged.Document.Writes.Count == 0 && unchanged.Core.Verified.Count == 0 &&
            unchanged.Document.Reloads == 0 &&
            unchangedReport.Notice == SyncNotice.None);
    }

    private static void RejectedDownload(TestCheck check, string dataDirectory)
    {
        Harness harness = new(dataDirectory, "rejected-download", Local("local"));
        harness.Remote.Bytes = Local("corrupt-remote");
        harness.SaveCleanState(Local("remote-old"), "\"e-old\"");
        harness.MarkDirty();
        harness.Core.Decision = new SyncDecisionDto { Action = "download" };
        harness.Core.VerifyFails = true;

        bool threw = false;
        try
        {
            harness.RunAsync().GetAwaiter().GetResult();
        }
        catch (CoreException)
        {
            threw = true;
        }

        check("downloaded bytes that do not decrypt are rejected before writing",
            threw && harness.Document.Writes.Count == 0 &&
            File.ReadAllBytes(harness.Document.Path).SequenceEqual(Local("local")));
        check("a rejected download leaves the baseline dirty", harness.State.LocalDirty);
    }

    private static void DownloadReplaceFailure(TestCheck check, string dataDirectory)
    {
        Harness harness = new(dataDirectory, "replace-failure", Local("local"));
        harness.Remote.Bytes = Local("remote-new");
        harness.SaveCleanState(Local("remote-old"), "\"e-old\"");
        harness.MarkDirty();
        harness.Core.Decision = new SyncDecisionDto { Action = "download" };
        harness.Document.ReplaceFails = true;

        bool threw = false;
        try
        {
            harness.RunAsync().GetAwaiter().GetResult();
        }
        catch (CoreException)
        {
            threw = true;
        }

        check("a refused local write is not followed by a clean baseline",
            threw && harness.Core.Commits.Count == 1 && harness.State.LocalDirty,
            $"commits={harness.Core.Commits.Count}");
    }

    private static void UploadBackoff(TestCheck check, string dataDirectory)
    {
        Harness harness = new(dataDirectory, "backoff", Local("local"));
        harness.Remote.Bytes = Local("remote");
        harness.SaveCleanState(harness.Remote.Bytes, "\"e1\"");
        harness.Core.Decision = new SyncDecisionDto { Action = "upload" };
        harness.Remote.UploadConflictsRemaining = 2;

        SyncReport report = harness.RunAsync().GetAwaiter().GetResult();

        check("a 412 during upload retries with the back-off schedule",
            report.Outcome == SyncOutcome.Synced && harness.Remote.Uploaded.Count == 1 &&
            harness.Time.Delays.SequenceEqual(new[]
            {
                TimeSpan.FromMilliseconds(500),
                TimeSpan.FromMilliseconds(1500),
            }),
            string.Join(",", harness.Time.Delays));
        check("the retry status names the delay",
            harness.StatusSources.Contains("Retrying sync in {0} ms") &&
            harness.Statuses.Any(update =>
                update.Source == "Retrying sync in {0} ms" &&
                update.Arguments.Length == 1 && Equals(update.Arguments[0], 500)));
        check("a retried upload replays the current baseline precondition",
            harness.Remote.IfMatch.Count == 1 && harness.Remote.IfMatch[0] == "\"e1\"");
    }

    private static void ExhaustedUploadMerges(TestCheck check, string dataDirectory)
    {
        Harness harness = new(dataDirectory, "merge-auto", Local("local"));
        byte[] remote = Local("remote");
        byte[] merged = Local("merged");
        harness.Remote.Bytes = remote;
        harness.SaveCleanState(remote, "\"e1\"");
        harness.Core.Decision = new SyncDecisionDto { Action = "upload" };
        harness.Core.MergeResult = new SyncMergeResult(merged, new List<SyncConflictDto>());
        harness.Document.MergeResult = harness.Core.MergeResult;
        harness.Remote.UploadConflictsRemaining = 4;

        SyncReport report = harness.RunAsync().GetAwaiter().GetResult();
        SyncState state = harness.State;

        check("an upload that keeps losing 412 merges instead of overwriting",
            report.Outcome == SyncOutcome.Merged &&
            report.Notice == SyncNotice.MergedAutomatically &&
            harness.Document.MergeCalls == 1 &&
            harness.Time.Delays.Count == 3);
        check("the merge is written, uploaded and committed",
            harness.Document.Writes[^1].SequenceEqual(merged) &&
            harness.Remote.Uploaded[^1].SequenceEqual(merged) &&
            harness.Events.TakeLast(6).SequenceEqual(new[]
            {
                "download", "merge", "write", "reload", "upload", "commit",
            }),
            string.Join(",", harness.Events));
        check("an automatically resolved merge records a clean baseline",
            !state.LocalDirty && state.Sha256 == Sha(merged) &&
            harness.LastStatus == "Synced");
        check("both sides are kept as conflict copies before merging",
            ConflictCopyPaths(harness).Count == 2,
            string.Join("; ", ConflictCopyPaths(harness)));
    }

    private static void ConflictChoices(TestCheck check, string dataDirectory)
    {
        static Harness Conflicted(string dataDirectory, string name, byte[] local)
        {
            Harness harness = new(dataDirectory, name, local);
            byte[] remote = Local("remote-" + name);
            harness.Remote.Bytes = remote;
            harness.SaveCleanState(remote, "\"e1\"");
            harness.Core.Decision = new SyncDecisionDto { Action = "conflict" };
            harness.Core.MergeResult = new SyncMergeResult(
                Local("merged-" + name),
                new List<SyncConflictDto>
                {
                    new()
                    {
                        Uuid = "entry-1",
                        Kind = "both_modified",
                        Fields = new List<string> { "password" },
                    },
                });
            harness.Document.MergeResult = harness.Core.MergeResult;
            return harness;
        }

        Harness keepBoth = Conflicted(dataDirectory, "keep-both", Local("local-keep-both"));
        keepBoth.Choice = SyncConflictChoice.KeepBoth;
        SyncReport keepBothReport = keepBoth.RunAsync().GetAwaiter().GetResult();
        check("the conflict dialog sees both copies and the conflict fields",
            keepBoth.Context is not null &&
            keepBoth.Context.LocalCopy is not null &&
            File.Exists(keepBoth.Context.LocalCopy) &&
            keepBoth.Context.RemoteCopy is not null &&
            File.Exists(keepBoth.Context.RemoteCopy) &&
            keepBoth.Context.Conflicts.Count == 1);
        check("keeping both merges, uploads and reports the merge",
            keepBothReport.Notice == SyncNotice.MergedKeptBoth &&
            keepBoth.LastStatus == "Merged" &&
            keepBoth.Remote.Uploaded[^1].SequenceEqual(keepBoth.Core.MergeResult.Bytes));

        Harness cancel = Conflicted(dataDirectory, "cancel", Local("local-cancel"));
        cancel.Choice = SyncConflictChoice.Cancel;
        SyncReport cancelReport = cancel.RunAsync().GetAwaiter().GetResult();
        check("cancelling a conflict writes nothing and stays dirty",
            cancelReport.Outcome == SyncOutcome.ConflictPaused &&
            cancel.Document.Writes.Count == 0 && cancel.State.LocalDirty &&
            cancel.LastStatus == "Conflict");

        Harness keepLocal = Conflicted(dataDirectory, "keep-local", Local("local-keep"));
        keepLocal.Choice = SyncConflictChoice.KeepLocal;
        SyncReport keepLocalReport = keepLocal.RunAsync().GetAwaiter().GetResult();
        check("keeping local uploads the local file and leaves it untouched",
            keepLocalReport.Notice == SyncNotice.KeptLocal &&
            keepLocal.Document.Writes.Count == 0 &&
            keepLocal.Remote.Uploaded[^1].SequenceEqual(Local("local-keep")) &&
            keepLocal.Remote.IfMatch[^1] == "\"e1\"" && !keepLocal.State.LocalDirty);

        Harness keepRemote = Conflicted(dataDirectory, "keep-remote", Local("local-remote"));
        keepRemote.Choice = SyncConflictChoice.KeepRemote;
        SyncReport keepRemoteReport = keepRemote.RunAsync().GetAwaiter().GetResult();
        check("keeping remote verifies, replaces and reports it",
            keepRemoteReport.Notice == SyncNotice.KeptRemote &&
            keepRemote.Core.Verified.Count == 1 &&
            keepRemote.Document.Writes.Count == 1 &&
            keepRemote.Document.Writes[0].SequenceEqual(keepRemote.Remote.Bytes) &&
            !keepRemote.State.LocalDirty);
    }

    private static void ConflictCopiesAreReused(TestCheck check, string dataDirectory)
    {
        Harness harness = new(dataDirectory, "copy-reuse", Local("local-reuse"));
        byte[] remote = Local("remote-reuse");
        harness.Remote.Bytes = remote;
        harness.SaveCleanState(remote, "\"e1\"");
        harness.Core.Decision = new SyncDecisionDto { Action = "conflict" };
        harness.Core.MergeResult = new SyncMergeResult(
            Local("merged-reuse"),
            new List<SyncConflictDto>
            {
                new() { Uuid = "entry-2", Kind = "both_modified" },
            });
        harness.Document.MergeResult = harness.Core.MergeResult;
        harness.Choice = SyncConflictChoice.Cancel;

        harness.RunAsync().GetAwaiter().GetResult();
        string? firstLocal = harness.Context?.LocalCopy;
        string? firstRemote = harness.Context?.RemoteCopy;
        harness.RunAsync().GetAwaiter().GetResult();

        check("a repeated conflict reuses its copies instead of writing new ones",
            ConflictCopyPaths(harness).Count == 2 &&
            harness.Context?.LocalCopy == firstLocal &&
            harness.Context?.RemoteCopy == firstRemote &&
            Directory.GetFiles(harness.Folder).Length == 3,
            string.Join("; ", ConflictCopyPaths(harness)));
    }

    private static void LocalEditDuringSync(TestCheck check, string dataDirectory)
    {
        // A save that lands while the remote bytes are downloading must not be
        // overwritten by the older download the decision was based on.
        Harness download = new(dataDirectory, "edit-during-download", Local("baseline"));
        byte[] remote = Local("remote");
        download.Remote.Bytes = remote;
        download.Remote.Metadata = new WebDavFileInfo
        {
            ETag = "\"e2\"",
            Size = remote.Length,
            ModifiedUnix = 700,
        };
        download.SaveCleanState(Local("baseline"), "\"e1\"");
        download.Core.Decision = new SyncDecisionDto { Action = "download" };
        byte[] userEdit = Local("user-edit");
        download.Remote.OnDownload =
            () => download.Document.ReplaceAsync(userEdit).GetAwaiter().GetResult();

        SyncReport report = download.RunAsync().GetAwaiter().GetResult();
        check("sync: a save during a download pauses instead of being overwritten",
            report.Outcome == SyncOutcome.ConflictPaused &&
            report.Notice == SyncNotice.LocalChangedWhileSyncing &&
            download.Document.Bytes.SequenceEqual(userEdit) &&
            download.Document.Writes.Count == 1 && download.State.LocalDirty,
            report.Notice.ToString());
        check("sync: the paused download uploads nothing and says why",
            download.Remote.Uploaded.Count == 0 && download.LastStatus == "Local changed",
            download.LastStatus);

        // Same race while a merge result is being prepared.
        Harness merge = new(dataDirectory, "edit-during-merge", Local("baseline"));
        merge.Remote.Bytes = Local("remote");
        merge.SaveCleanState(Local("baseline"), "\"e1\"");
        merge.Core.Decision = new SyncDecisionDto { Action = "download_and_merge" };
        merge.Core.MergeResult = new SyncMergeResult(Local("merged"),
            new List<SyncConflictDto>());
        merge.Document.MergeResult = merge.Core.MergeResult;
        byte[] mergeEdit = Local("merge-user-edit");
        merge.Remote.OnDownload =
            () => merge.Document.ReplaceAsync(mergeEdit).GetAwaiter().GetResult();

        SyncReport mergeReport = merge.RunAsync().GetAwaiter().GetResult();
        check("sync: a save during a merge pauses instead of applying the older result",
            mergeReport.Outcome == SyncOutcome.ConflictPaused &&
            mergeReport.Notice == SyncNotice.LocalChangedWhileSyncing &&
            merge.Document.Bytes.SequenceEqual(mergeEdit) &&
            merge.Document.Writes.Count == 1 && merge.State.LocalDirty,
            mergeReport.Notice.ToString());
    }

    private static void LocalEditDuringUpload(TestCheck check, string dataDirectory)
    {
        // A save that lands while the core is serializing the upload snapshot
        // must not be overwritten by that older snapshot.
        Harness harness = new(dataDirectory, "edit-during-upload", Local("baseline"));
        harness.Remote.Missing = true;
        harness.Core.ModelBytes = Local("model");
        byte[] userEdit = Local("upload-user-edit");
        harness.Core.OnSaveBytes =
            () => harness.Document.ReplaceAsync(userEdit).GetAwaiter().GetResult();

        SyncReport report = harness.RunAsync().GetAwaiter().GetResult();
        check("sync: a save during the upload snapshot pauses instead of being overwritten",
            report.Outcome == SyncOutcome.ConflictPaused &&
            report.Notice == SyncNotice.LocalChangedWhileSyncing &&
            harness.Document.Bytes.SequenceEqual(userEdit) &&
            harness.Remote.Uploaded.Count == 0 && harness.State.LocalDirty,
            report.Notice.ToString());
    }

    private static void NothingQueued(TestCheck check, string dataDirectory)
    {
        Harness offline = new(dataDirectory, "offline", Local("local-offline"));
        offline.Core.Decision = new SyncDecisionDto
        {
            Action = "offline",
            Reason = "network down",
        };
        SyncReport offlineReport = offline.RunAsync().GetAwaiter().GetResult();
        check("an offline decision writes nothing and says offline",
            offlineReport.Outcome == SyncOutcome.Offline &&
            offlineReport.Reason == "network down" &&
            offline.Document.Writes.Count == 0 &&
            offline.Core.Commits.Count == 0 && offline.LastStatus == "Offline");

        Harness retry = new(dataDirectory, "retry", Local("local-retry"));
        retry.Core.Decision = new SyncDecisionDto { Action = "retry" };
        SyncReport retryReport = retry.RunAsync().GetAwaiter().GetResult();
        check("a retry decision writes nothing and reports retry",
            retryReport.Outcome == SyncOutcome.Retry &&
            retry.Document.Writes.Count == 0 && retry.LastStatus == "Sync retry");

        Harness dirty = new(dataDirectory, "decision-inputs", Local("local-dirty"));
        dirty.Remote.Bytes = Local("remote-dirty");
        dirty.SaveCleanState(Local("remote-old-dirty"), "\"e-old\"");
        dirty.MarkDirty();
        dirty.Remote.Metadata = new WebDavFileInfo
        {
            ETag = "\"e-mark\"",
            Size = 3,
            ModifiedUnix = 42,
        };
        dirty.Core.Decision = new SyncDecisionDto { Action = "up_to_date" };
        dirty.RunAsync().GetAwaiter().GetResult();
        check("the decision receives the persisted dirty flag and remote metadata",
            dirty.Core.LastLocalDirty &&
            dirty.Core.LastRemoteMeta?.Contains("e-mark", StringComparison.Ordinal) == true &&
            dirty.Core.LastRemoteMeta?.Contains("\"modified_unix\":42", StringComparison.Ordinal) == true,
            dirty.Core.LastRemoteMeta ?? string.Empty);
    }

    // ------------------------------------------------------------------ helpers

    private static byte[] Local(string value)
        => System.Text.Encoding.UTF8.GetBytes("kdbx-" + value);

    private static string Sha(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static List<string> ConflictCopyPaths(Harness harness)
        => Directory.GetFiles(harness.Folder)
            .Where(path => Path.GetFileName(path).Contains("(conflict ", StringComparison.Ordinal) ||
                           Path.GetFileName(path).Contains("(remote conflict ", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

    private sealed class Harness
    {
        private readonly List<string> _events = new();

        public Harness(string dataDirectory, string name, byte[] localBytes)
        {
            Folder = Path.Combine(dataDirectory,
                "sync-" + name + "-" + Guid.NewGuid().ToString("N")[..8]);
            System.IO.Directory.CreateDirectory(Folder);
            Document = new FakeDocument(Path.Combine(Folder, "vault.kdbx"), localBytes)
            {
                Events = _events,
            };
            Remote = new FakeRemote { Events = _events };
            Core = new FakeCore(_events);
            Time = new ImmediateDelay();
            States = new SyncStateStore(Path.Combine(Folder, "state"));
            Coordinator = new SyncCoordinator(States, Time.DelayAsync)
            {
                ConflictChoice = AnswerConflict,
            };
        }

        public string Folder { get; }

        public FakeDocument Document { get; }

        public FakeRemote Remote { get; }

        public FakeCore Core { get; }

        public ImmediateDelay Time { get; }

        public SyncStateStore States { get; }

        public SyncCoordinator Coordinator { get; }

        public List<SyncStatusUpdate> Statuses { get; } = new();

        public List<string> Events => _events;

        public SyncConflictChoice Choice { get; set; } = SyncConflictChoice.Cancel;

        public SyncConflictContext? Context { get; private set; }

        public string LastStatus => Statuses.Count == 0 ? "" : Statuses[^1].Source;

        public IEnumerable<string> StatusSources => Statuses.Select(update => update.Source);

        public SyncState State => States.Load(Url, User, Document.Path);

        public Task<SyncReport> RunAsync()
            => Coordinator.RunAsync(
                Remote,
                Document,
                Core,
                new SyncOptions
                {
                    RemoteUrl = Url,
                    Username = User,
                    MasterPassword = Password,
                },
                update => Statuses.Add(update));

        public void SaveCleanState(byte[] remoteBytes, string etag)
            => States.Save(Url, User, Document.Path, new SyncState
            {
                Etag = etag,
                Sha256 = Sha(remoteBytes),
                Size = remoteBytes.Length,
                RemoteModifiedUnix = 100,
                LocalDirty = false,
            });

        public void MarkDirty()
        {
            SyncState state = States.Load(Url, User, Document.Path);
            state.LocalDirty = true;
            States.Save(Url, User, Document.Path, state);
        }

        private Task<SyncConflictChoice> AnswerConflict(SyncConflictContext context)
        {
            Context = context;
            return Task.FromResult(Choice);
        }
    }

    private sealed class FakeRemote : ISyncRemote
    {
        public List<string>? Events { get; init; }

        public WebDavFileInfo? Metadata { get; set; } = new()
        {
            ETag = "\"e1\"",
            Size = 3,
            ModifiedUnix = 100,
        };

        public byte[] Bytes { get; set; } = new byte[] { 1, 2, 3 };

        public bool Missing { get; set; }

        public int UploadConflictsRemaining { get; set; }

        public List<byte[]> Uploaded { get; } = new();

        public List<string?> IfMatch { get; } = new();

        public int Downloads { get; private set; }

        /// <summary>Hook to simulate a local save while the download is in flight.</summary>
        public Action? OnDownload { get; set; }

        public Task<WebDavFileInfo?> GetMetadataAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Missing ? null : Metadata);

        public Task<byte[]> DownloadAsync(CancellationToken cancellationToken = default)
        {
            Downloads++;
            Events?.Add("download");
            OnDownload?.Invoke();
            return Task.FromResult(Bytes);
        }

        public Task<string?> UploadAsync(byte[] bytes, string? ifMatch,
            CancellationToken cancellationToken = default)
        {
            if (UploadConflictsRemaining > 0)
            {
                UploadConflictsRemaining--;
                throw new WebDavException("the remote changed", 412, conflict: true);
            }
            Events?.Add("upload");
            Uploaded.Add(bytes);
            IfMatch.Add(ifMatch);
            Bytes = bytes;
            Metadata = new WebDavFileInfo
            {
                ETag = "\"e" + (Uploaded.Count + 1) + "\"",
                Size = bytes.Length,
                ModifiedUnix = 200 + Uploaded.Count,
            };
            return Task.FromResult<string?>(Metadata?.ETag);
        }
    }

    private sealed class FakeCore : ICoreClient
    {
        public FakeCore(List<string>? events = null)
        {
            Events = events;
        }

        public List<string>? Events { get; }

        public SyncDecisionDto Decision { get; set; } = new() { Action = "upload" };

        public byte[] ModelBytes { get; set; } = new byte[] { 9, 9, 9 };

        public SyncMergeResult MergeResult { get; set; } =
            new(Array.Empty<byte>(), new List<SyncConflictDto>());

        public bool VerifyFails { get; set; }

        public List<(string? Etag, string? Sha)> Commits { get; } = new();

        public List<byte[]> Verified { get; } = new();

        public string? LastRemoteMeta { get; private set; }

        public bool LastLocalDirty { get; private set; }

        public bool IsOpen => true;

        public string? DatabasePath { get; set; }

        public Task OpenAsync(string path, string password)
            => throw new NotSupportedException();

        public Task OpenBytesAsync(byte[] kdbx, string password, string? databasePath = null)
            => throw new NotSupportedException();

        public Task LockAsync() => Task.CompletedTask;

        public Task<byte[]> SaveBytesAsync()
        {
            OnSaveBytes?.Invoke();
            return Task.FromResult(ModelBytes);
        }

        /// <summary>Hook to simulate a local save while the snapshot is taken.</summary>
        public Action? OnSaveBytes { get; set; }

        public Task SaveAsync(string path, int backupRetention = 20,
            Func<bool>? databaseChanged = null)
            => throw new NotSupportedException();

        public Task VerifyAsync(byte[] bytes, string password)
        {
            if (VerifyFails)
            {
                throw new CoreException("Corrupt", "downloaded bytes do not decrypt");
            }
            Verified.Add(bytes);
            Events?.Add("verify");
            return Task.CompletedTask;
        }

        public Task<SyncDecisionDto> SyncDecideAsync(string remoteMetaJson, bool localDirty)
        {
            LastRemoteMeta = remoteMetaJson;
            LastLocalDirty = localDirty;
            return Task.FromResult(Decision);
        }

        public Task SyncCommitAsync(string? etag, string? sha256)
        {
            Commits.Add((etag, sha256));
            Events?.Add("commit");
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeDocument : IVaultDocument
    {
        public FakeDocument(string path, byte[] bytes)
        {
            Path = path;
            Bytes = bytes;
            File.WriteAllBytes(path, bytes);
        }

        public string Path { get; }

        public byte[] Bytes { get; private set; }

        public List<string>? Events { get; init; }

        public bool ReplaceFails { get; set; }

        public List<byte[]> Writes { get; } = new();

        public int Reloads { get; private set; }

        public int MergeCalls { get; private set; }

        public SyncMergeResult MergeResult { get; set; } =
            new(Array.Empty<byte>(), new List<SyncConflictDto>());

        public Task<byte[]> ReadBytesAsync() => Task.FromResult(Bytes);

        public Task ReplaceAsync(byte[] bytes)
        {
            if (ReplaceFails)
            {
                throw new CoreException("VaultChanged", "the file changed outside");
            }
            Writes.Add(bytes);
            Bytes = bytes;
            File.WriteAllBytes(Path, bytes);
            Events?.Add("write");
            return Task.CompletedTask;
        }

        public Task ReloadAsync()
        {
            Reloads++;
            Events?.Add("reload");
            return Task.CompletedTask;
        }

        public Task<SyncMergeResult> MergeRemoteAsync(byte[] remote)
        {
            MergeCalls++;
            Events?.Add("merge");
            return Task.FromResult(MergeResult);
        }
    }

    private sealed class ImmediateDelay
    {
        public List<TimeSpan> Delays { get; } = new();

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Delays.Add(delay);
            return cancellationToken.IsCancellationRequested
                ? Task.FromCanceled(cancellationToken)
                : Task.CompletedTask;
        }
    }
}
