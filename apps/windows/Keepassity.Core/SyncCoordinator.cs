// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text.Json;

namespace Keepassity.Core;

/// <summary>Inputs for one sync run.</summary>
public sealed class SyncOptions
{
    public required string RemoteUrl { get; init; }
    public required string Username { get; init; }

    /// <summary>
    /// The current master password. Required before downloaded or remote-chosen
    /// bytes replace the local database; null/empty means the vault is locked and
    /// the run fails closed instead of writing unverified bytes.
    /// </summary>
    public string? MasterPassword { get; init; }
}

/// <summary>How a sync run ended, independent of how the shell renders it.</summary>
public enum SyncOutcome
{
    /// <summary>Local and remote are in sync (uploaded, downloaded or already equal).</summary>
    Synced,

    /// <summary>A merge was applied and uploaded.</summary>
    Merged,

    /// <summary>The remote is unchanged but no content hash exists to prove it.</summary>
    NeedsBaseline,

    /// <summary>The core asked to retry later; nothing was written.</summary>
    Retry,

    /// <summary>The remote is unreachable; nothing was written.</summary>
    Offline,

    /// <summary>The user paused the conflict; both copies are untouched and dirty.</summary>
    ConflictPaused,

    /// <summary>The local merged/kept bytes were applied but the remote changed again.</summary>
    RemoteChanged,

    /// <summary>
    /// The remote copy cannot be opened with the master password; nothing local
    /// was touched and the user has to decide whether to replace it.
    /// </summary>
    RemoteUnreadable,
}

/// <summary>The user-visible follow-up the shell should show after a run.</summary>
public enum SyncNotice
{
    None,
    Downloaded,
    MergedAutomatically,
    MergedKeptBoth,
    KeptLocal,
    KeptRemote,
    ConflictPaused,
    MergedRemoteChanged,
    KeptLocalRemoteChanged,
    LocalChangedWhileSyncing,
    RemoteUnreadable,
    RemoteRepaired,
    RemoteUnreadableRemoteChanged,
}

/// <summary>Result of a sync run: what happened and which notice to surface.</summary>
public sealed record SyncReport(
    SyncOutcome Outcome,
    SyncNotice Notice = SyncNotice.None,
    string? LocalCopy = null,
    string? RemoteCopy = null,
    string? Reason = null,
    bool Degraded = false);

/// <summary>
/// One status line for the navigation pane. The source string is an untranslated
/// catalog key; the shell renders it through the localization façade.
/// </summary>
public sealed record SyncStatusUpdate(string Source, object?[] Arguments);

/// <summary>What the shell needs to ask the user how to resolve a conflict.</summary>
public sealed record SyncConflictContext(
    string? LocalCopy,
    string? RemoteCopy,
    IReadOnlyList<SyncConflictDto> Conflicts);

/// <summary>The user's answer to a three-way merge conflict.</summary>
public enum SyncConflictChoice
{
    Cancel,
    KeepLocal,
    KeepRemote,
    KeepBoth,
}

/// <summary>
/// Runs one WebDAV sync: decide, upload with back-off, download, three-way merge
/// and the conflict rules, then record the new baseline.
///
/// This is the application-service layer of the sync protocol. Keeping it out of
/// the window is what makes the ordering rules testable: the local database is
/// only replaced by bytes that were verified with the master password, the disk
/// write always happens before the in-memory baseline commit, the baseline is
/// only saved after the core accepted it, and a failed step leaves the vault
/// dirty rather than silently clean.
/// </summary>
public sealed class SyncCoordinator
{
    private const int UploadConflictRetries = 3;

    private readonly SyncStateStore _states;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    // One local/remote copy pair per unresolved conflict: a repeated attempt for
    // the same pair reuses the copies instead of writing another pair each time.
    private string? _conflictLocalCopy;
    private string? _conflictRemoteCopy;

    /// <param name="delayAsync">
    /// Wait used by the upload back-off. Tests pass an immediate waiter so the
    /// retry schedule can be asserted without real waiting; the shell uses
    /// <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.
    /// </param>
    public SyncCoordinator(SyncStateStore states,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _states = states ?? throw new ArgumentNullException(nameof(states));
        _delayAsync = delayAsync ?? Task.Delay;
    }

    /// <summary>
    /// Asked only when the three-way merge left conflicts. When unset (headless
    /// runs and tests) the run pauses the conflict without writing anything.
    /// </summary>
    public Func<SyncConflictContext, Task<SyncConflictChoice>>? ConflictChoice { get; set; }

    /// <summary>
    /// One-shot explicit user choice: when the downloaded remote copy cannot be
    /// opened with the master password, replace it with the verified local copy
    /// instead of pausing. Never enabled without asking the user: the unreadable
    /// remote may still be the only copy of data the user can recover elsewhere.
    /// </summary>
    public bool OverwriteUnreadableRemote { get; set; }

    /// <summary>
    /// Execute one sync run against <paramref name="remote"/>. Network and core
    /// exceptions propagate to the caller unchanged; anything already written to
    /// disk has a recorded dirty baseline at that point.
    /// </summary>
    public async Task<SyncReport> RunAsync(ISyncRemote remote, IVaultDocument document,
        ICoreClient core, SyncOptions options, Action<SyncStatusUpdate>? status = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(options);
        var run = new SyncRun(
            remote,
            document,
            core,
            options.RemoteUrl.Trim(),
            options.Username.Trim(),
            options.MasterPassword,
            status,
            cancellationToken);
        return await ExecuteAsync(run);
    }

    private sealed record SyncRun(
        ISyncRemote Remote,
        IVaultDocument Document,
        ICoreClient Core,
        string Url,
        string Username,
        string? MasterPassword,
        Action<SyncStatusUpdate>? Status,
        CancellationToken Cancellation)
    {
        /// <summary>
        /// Hash of the local file when this run started. A save that lands while
        /// the run downloads, merges or verifies must not be overwritten by the
        /// older result the decision was based on.
        /// </summary>
        public string? LocalShaAtStart { get; set; }
    }

    private async Task<SyncReport> ExecuteAsync(SyncRun run)
    {
        SyncState state = _states.Load(run.Url, run.Username, run.Document.Path);
        byte[]? localAtStart = File.Exists(run.Document.Path)
            ? await run.Document.ReadBytesAsync()
            : null;
        if (localAtStart is not null)
        {
            run.LocalShaAtStart = Sha256Hex(localAtStart);
        }
        if (!string.IsNullOrEmpty(state.Sha256))
        {
            await run.Core.SyncCommitAsync(state.Etag, state.Sha256);
            // The sidecar is only clean if the local file still is the bytes that
            // were synced. A crash between the database write and the sidecar
            // write leaves the old sidecar behind; treat the vault as dirty
            // instead of letting the next sync no-op and lose the upload.
            if (localAtStart is not null &&
                !string.Equals(run.LocalShaAtStart, state.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Warn(
                    "sync: local database differs from the synced baseline; treating it as dirty");
                state.LocalDirty = true;
            }
        }

        WebDavFileInfo? metadata = await run.Remote.GetMetadataAsync(run.Cancellation);
        if (metadata is null)
        {
            // The remote file does not exist yet: the local database becomes the
            // first version on the server.
            return await UploadWithBackoffAsync(run, state);
        }

        string remoteMeta = JsonSerializer.Serialize(new
        {
            etag = metadata.ETag,
            size = metadata.Size,
            modified_unix = metadata.ModifiedUnix,
            reliability = metadata.ETag is null ? "degraded" : "full",
        });
        SyncDecisionDto decision = await run.Core.SyncDecideAsync(remoteMeta, state.LocalDirty);
        switch (decision.Action)
        {
            case "up_to_date":
                return await UpToDateAsync(run, state, metadata, decision.Degraded);
            case "upload":
                return await UploadWithBackoffAsync(run, state);
            case "download":
                return await DownloadAsync(run, state, metadata);
            case "download_and_merge":
            case "conflict":
                return await MergeAsync(run, state, metadata);
            case "retry":
                Report(run.Status, "Sync retry");
                return new SyncReport(SyncOutcome.Retry);
            case "offline":
            default:
                Report(run.Status, "Offline");
                return new SyncReport(SyncOutcome.Offline, Reason: decision.Reason);
        }
    }

    private async Task<SyncReport> UpToDateAsync(SyncRun run, SyncState state,
        WebDavFileInfo metadata, bool degraded)
    {
        if (string.IsNullOrEmpty(state.Sha256))
        {
            // The remote says unchanged, but there is no content hash to record
            // as a baseline. Do not mark the vault clean; a later sync could
            // otherwise overwrite local edits.
            state.LocalDirty = true;
            SaveState(run, state);
            Report(run.Status, "Sync needs baseline");
            return new SyncReport(SyncOutcome.NeedsBaseline);
        }
        state.Etag = metadata.ETag;
        state.Size = metadata.Size;
        state.RemoteModifiedUnix = metadata.ModifiedUnix;
        state.LocalDirty = false;
        state.LastSyncUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await run.Core.SyncCommitAsync(state.Etag, state.Sha256);
        SaveState(run, state);
        Report(run.Status, degraded ? "Synced (degraded)" : "Synced");
        return new SyncReport(SyncOutcome.Synced, Degraded: degraded);
    }

    /// <summary>
    /// Upload with the same 0.5 s / 1.5 s / 4 s back-off schedule as the core
    /// sync state machine. A 412 (remote changed during the upload) is retried
    /// three times; after the last retry the remote change is resolved through the
    /// normal merge/conflict path, so the remote is never overwritten silently.
    /// </summary>
    private async Task<SyncReport> UploadWithBackoffAsync(SyncRun run, SyncState state)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await UploadAsync(run, state);
            }
            catch (WebDavException ex) when (ex.Conflict)
            {
                if (attempt >= UploadConflictRetries)
                {
                    AppLog.Warn("sync: PUT 412 after three back-off retries; merging");
                    WebDavFileInfo? metadata =
                        await run.Remote.GetMetadataAsync(run.Cancellation);
                    if (metadata is null)
                    {
                        throw;
                    }
                    Report(run.Status, "Resolving conflict");
                    return await MergeAsync(run, state, metadata);
                }
                int delayMs = attempt switch
                {
                    0 => 500,
                    1 => 1500,
                    _ => 4000,
                };
                Report(run.Status, "Retrying sync in {0} ms", delayMs);
                AppLog.Warn($"sync: PUT 412, retry {attempt + 1}/3 after {delayMs} ms");
                await _delayAsync(TimeSpan.FromMilliseconds(delayMs), run.Cancellation);
            }
        }
    }

    private async Task<SyncReport> UploadAsync(SyncRun run, SyncState state)
    {
        byte[] bytes = await run.Core.SaveBytesAsync();
        string bytesSha = Sha256Hex(bytes);
        // The snapshot above can be older than a save that landed while the core
        // was serializing it; writing the snapshot would then undo that save.
        if (run.LocalShaAtStart is not null && File.Exists(run.Document.Path))
        {
            byte[] current = await run.Document.ReadBytesAsync();
            string currentSha = Sha256Hex(current);
            if (!string.Equals(currentSha, run.LocalShaAtStart,
                    StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(currentSha, bytesSha, StringComparison.OrdinalIgnoreCase))
            {
                return LocalChangedWhileSyncing(run, state);
            }
        }
        await run.Document.ReplaceAsync(bytes);
        // The coordinator itself just wrote the file; remember it so the
        // during-run edit guard only reacts to changes made after this point.
        run.LocalShaAtStart = bytesSha;
        string? etag = await run.Remote.UploadAsync(bytes, state.Etag, run.Cancellation);
        string sha = Sha256Hex(bytes);
        await run.Core.SyncCommitAsync(etag, sha);
        state.Etag = etag;
        state.Sha256 = sha;
        state.Size = bytes.Length;
        state.LocalDirty = false;
        state.LastSyncUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        SaveState(run, state);
        ClearConflictCopies();
        Report(run.Status, "Synced");
        return new SyncReport(SyncOutcome.Synced);
    }

    private async Task<SyncReport> DownloadAsync(SyncRun run, SyncState state,
        WebDavFileInfo metadata)
    {
        byte[] bytes = await run.Remote.DownloadAsync(run.Cancellation);
        if (await LocalChangedSinceStartAsync(run))
        {
            return LocalChangedWhileSyncing(run, state);
        }
        string sha = Sha256Hex(bytes);
        bool contentChanged = true;
        if (!string.IsNullOrEmpty(state.Sha256))
        {
            contentChanged = !string.Equals(sha, state.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        else if (File.Exists(run.Document.Path))
        {
            byte[] localBytes = await run.Document.ReadBytesAsync();
            contentChanged = !string.Equals(sha, Sha256Hex(localBytes),
                StringComparison.OrdinalIgnoreCase);
        }
        if (contentChanged)
        {
            // The live database is only replaced after the downloaded bytes
            // decrypt with the current master password. A redirect page or a
            // corrupted body must not become the local vault; when the remote
            // copy itself is unreadable the user decides whether to repair it.
            SyncReport? unreadable = await VerifyRemoteOrRepairAsync(run, state, metadata,
                bytes);
            if (unreadable is not null)
            {
                return unreadable;
            }
            await run.Document.ReplaceAsync(bytes);
            run.LocalShaAtStart = sha;
            await run.Document.ReloadAsync();
        }
        await run.Core.SyncCommitAsync(metadata.ETag, sha);
        state.Etag = metadata.ETag;
        state.Sha256 = sha;
        state.Size = bytes.Length;
        state.RemoteModifiedUnix = metadata.ModifiedUnix;
        state.LocalDirty = false;
        state.LastSyncUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        SaveState(run, state);
        ClearConflictCopies();
        Report(run.Status, "Synced");
        return new SyncReport(SyncOutcome.Synced,
            contentChanged ? SyncNotice.Downloaded : SyncNotice.None);
    }

    private async Task<SyncReport> MergeAsync(SyncRun run, SyncState state,
        WebDavFileInfo metadata)
    {
        byte[] remote = await run.Remote.DownloadAsync(run.Cancellation);
        SyncReport? unreadable = await VerifyRemoteOrRepairAsync(run, state, metadata,
            remote);
        if (unreadable is not null)
        {
            return unreadable;
        }
        (string? localCopy, string? remoteCopy) = await PrepareConflictCopiesAsync(run, remote);
        SyncMergeResult merged = await run.Document.MergeRemoteAsync(remote);
        if (merged.Conflicts.Count == 0)
        {
            return await ApplyMergedAsync(run, state, metadata, merged, localCopy, remoteCopy);
        }

        SyncConflictChoice choice = SyncConflictChoice.Cancel;
        if (ConflictChoice is not null)
        {
            choice = await ConflictChoice(
                new SyncConflictContext(localCopy, remoteCopy, merged.Conflicts));
        }
        switch (choice)
        {
            case SyncConflictChoice.KeepLocal:
                return await KeepLocalAsync(run, state, metadata, localCopy, remoteCopy);
            case SyncConflictChoice.KeepRemote:
                return await KeepRemoteAsync(run, state, metadata, remote, localCopy,
                    remoteCopy);
            case SyncConflictChoice.KeepBoth:
                return await ApplyMergedAsync(run, state, metadata, merged, localCopy,
                    remoteCopy);
            default:
                state.LocalDirty = true;
                SaveState(run, state);
                Report(run.Status, "Conflict");
                return new SyncReport(SyncOutcome.ConflictPaused, SyncNotice.ConflictPaused,
                    localCopy, remoteCopy);
        }
    }

    private async Task<SyncReport> ApplyMergedAsync(SyncRun run, SyncState state,
        WebDavFileInfo metadata, SyncMergeResult merged, string? localCopy,
        string? remoteCopy)
    {
        if (await LocalChangedSinceStartAsync(run))
        {
            return LocalChangedWhileSyncing(run, state);
        }
        bool autoMerged = merged.Conflicts.Count == 0;
        await run.Document.ReplaceAsync(merged.Bytes);
        run.LocalShaAtStart = Sha256Hex(merged.Bytes);
        await run.Document.ReloadAsync();
        string? etag;
        try
        {
            etag = await run.Remote.UploadAsync(merged.Bytes, metadata.ETag, run.Cancellation);
        }
        catch (WebDavException ex) when (ex.Conflict)
        {
            state.LocalDirty = true;
            SaveState(run, state);
            Report(run.Status, "Conflict");
            return new SyncReport(SyncOutcome.RemoteChanged,
                SyncNotice.MergedRemoteChanged, localCopy, remoteCopy);
        }
        await CommitSyncedAsync(run, state, merged.Bytes, etag, metadata,
            autoMerged ? "Synced" : "Merged");
        return new SyncReport(SyncOutcome.Merged,
            autoMerged ? SyncNotice.MergedAutomatically : SyncNotice.MergedKeptBoth,
            localCopy, remoteCopy);
    }

    private async Task<SyncReport> KeepLocalAsync(SyncRun run, SyncState state,
        WebDavFileInfo metadata, string? localCopy, string? remoteCopy)
    {
        byte[] localBytes = await run.Document.ReadBytesAsync();
        string? etag;
        try
        {
            etag = await run.Remote.UploadAsync(localBytes, metadata.ETag, run.Cancellation);
        }
        catch (WebDavException ex) when (ex.Conflict)
        {
            state.LocalDirty = true;
            SaveState(run, state);
            Report(run.Status, "Conflict");
            return new SyncReport(SyncOutcome.RemoteChanged,
                SyncNotice.KeptLocalRemoteChanged, localCopy, remoteCopy);
        }
        await CommitSyncedAsync(run, state, localBytes, etag, metadata, "Synced");
        return new SyncReport(SyncOutcome.Synced, SyncNotice.KeptLocal, localCopy, remoteCopy);
    }

    private async Task<SyncReport> KeepRemoteAsync(SyncRun run, SyncState state,
        WebDavFileInfo metadata, byte[] remote, string? localCopy, string? remoteCopy)
    {
        if (await LocalChangedSinceStartAsync(run))
        {
            return LocalChangedWhileSyncing(run, state);
        }
        // MergeAsync verified these exact bytes before asking the conflict question.
        await run.Document.ReplaceAsync(remote);
        run.LocalShaAtStart = Sha256Hex(remote);
        await run.Document.ReloadAsync();
        await CommitSyncedAsync(run, state, remote, metadata.ETag, metadata, "Synced");
        return new SyncReport(SyncOutcome.Synced, SyncNotice.KeptRemote, localCopy,
            remoteCopy);
    }

    /// <summary>
    /// Record a successful sync: commit the baseline in the core, persist the
    /// sidecar, drop the conflict copies bookkeeping and report the status.
    /// The order matters: the core validates the hash before the sidecar claims
    /// the vault is clean, so a crash between the two leaves it dirty.
    /// </summary>
    private async Task CommitSyncedAsync(SyncRun run, SyncState state, byte[] bytes,
        string? etag, WebDavFileInfo? metadata, string statusSource)
    {
        string sha = Sha256Hex(bytes);
        await run.Core.SyncCommitAsync(etag, sha);
        state.Etag = etag;
        state.Sha256 = sha;
        state.Size = bytes.Length;
        if (metadata is not null)
        {
            state.RemoteModifiedUnix = metadata.ModifiedUnix;
        }
        state.LocalDirty = false;
        state.LastSyncUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        SaveState(run, state);
        ClearConflictCopies();
        Report(run.Status, statusSource);
    }

    /// <summary>
    /// Keep one local/remote copy pair for an unresolved conflict. The names are
    /// derived from the two content hashes, so the same unresolved pair is reused
    /// even after a restart instead of writing another timestamped pair into the
    /// vault folder on every launch.
    /// </summary>
    private async Task<(string? LocalCopy, string? RemoteCopy)> PrepareConflictCopiesAsync(
        SyncRun run, byte[] remote)
    {
        ConflictIdentity? identity = await ConflictIdentityAsync(run, remote);
        if (identity is null)
        {
            return (null, null);
        }
        (string expectedLocal, string expectedRemote) =
            ConflictCopyPaths(run.Document.Path, identity.Signature);
        if (await FileHasHashAsync(expectedLocal, identity.LocalSha) &&
            await FileHasHashAsync(expectedRemote, identity.RemoteSha))
        {
            _conflictLocalCopy = expectedLocal;
            _conflictRemoteCopy = expectedRemote;
            return (expectedLocal, expectedRemote);
        }
        _conflictLocalCopy = CreateConflictCopy(run.Document.Path,
            "conflict", identity.ShortSignature, copySource: true, content: null);
        _conflictRemoteCopy = CreateConflictCopy(run.Document.Path,
            "remote conflict", identity.ShortSignature, copySource: false, content: remote);
        return (_conflictLocalCopy, _conflictRemoteCopy);
    }

    private sealed record ConflictIdentity(string Signature, string LocalSha,
        string RemoteSha)
    {
        /// <summary>Compact name-safe form used in the copy file names.</summary>
        public string ShortSignature => LocalSha[..8] + "-" + RemoteSha[..8];
    }

    private async Task<ConflictIdentity?> ConflictIdentityAsync(SyncRun run, byte[] remote)
    {
        try
        {
            byte[] local = await run.Document.ReadBytesAsync();
            string localSha = Sha256Hex(local);
            string remoteSha = Sha256Hex(remote);
            return new ConflictIdentity(localSha + ":" + remoteSha, localSha, remoteSha);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static (string Local, string Remote) ConflictCopyPaths(string databasePath,
        string signature)
    {
        string directory = Path.GetDirectoryName(databasePath)!;
        string stem = Path.GetFileNameWithoutExtension(databasePath);
        string extension = Path.GetExtension(databasePath);
        string[] parts = signature.Split(':');
        string shortSignature = parts.Length == 2 && parts[0].Length >= 8 &&
                                parts[1].Length >= 8
            ? parts[0][..8] + "-" + parts[1][..8]
            : signature.Replace(':', '-')[..Math.Min(16, signature.Length)];
        return (
            Path.Combine(directory, $"{stem} (conflict {shortSignature}){extension}"),
            Path.Combine(directory, $"{stem} (remote conflict {shortSignature}){extension}"));
    }

    private static Task<bool> FileHasHashAsync(string path, string expectedSha)
    {
        if (!File.Exists(path))
        {
            return Task.FromResult(false);
        }
        try
        {
            string actual = Sha256Hex(File.ReadAllBytes(path));
            return Task.FromResult(string.Equals(actual, expectedSha,
                StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Write a conflict copy under a deterministic name. An existing file with
    /// the same name but different bytes (for example a user-edited conflict
    /// copy) is never overwritten; the new copy gets a numeric suffix.
    /// </summary>
    private static string CreateConflictCopy(string databasePath, string label,
        string shortSignature, bool copySource, byte[]? content)
    {
        string directory = Path.GetDirectoryName(databasePath)!;
        string stem = Path.GetFileNameWithoutExtension(databasePath);
        string extension = Path.GetExtension(databasePath);
        string path = Path.Combine(directory,
            $"{stem} ({label} {shortSignature}){extension}");
        int suffix = 1;
        while (File.Exists(path))
        {
            path = Path.Combine(directory,
                $"{stem} ({label} {shortSignature}-{suffix++}){extension}");
        }
        if (copySource)
        {
            AtomicFile.Copy(databasePath, path);
        }
        else
        {
            AtomicFile.Write(path, content!);
        }
        return path;
    }

    /// <summary>
    /// True when the local file no longer has the bytes this run started with:
    /// a save landed after the decision, so applying the older sync result would
    /// silently undo it.
    /// </summary>
    private static async Task<bool> LocalChangedSinceStartAsync(SyncRun run)
    {
        if (run.LocalShaAtStart is null || !File.Exists(run.Document.Path))
        {
            return false;
        }
        byte[] current = await run.Document.ReadBytesAsync();
        return !string.Equals(Sha256Hex(current), run.LocalShaAtStart,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Stop before replacing or uploading: keep the newer local save, mark the
    /// vault dirty and let the next sync merge the two sides.
    /// </summary>
    private SyncReport LocalChangedWhileSyncing(SyncRun run, SyncState state)
    {
        AppLog.Info("sync: the local database changed during the run; pausing");
        state.LocalDirty = true;
        SaveState(run, state);
        Report(run.Status, "Local changed");
        return new SyncReport(SyncOutcome.ConflictPaused,
            SyncNotice.LocalChangedWhileSyncing);
    }

    private static bool IsUnreadableRemote(CoreException ex)
        => ex.Code is "BadPassword" or "CorruptDatabase" or "Corrupt";

    /// <summary>
    /// Verify downloaded remote bytes before they may replace anything. Returns a
    /// report when the remote copy cannot be opened; the caller then returns it
    /// unchanged. A null result means the bytes are readable.
    /// </summary>
    private async Task<SyncReport?> VerifyRemoteOrRepairAsync(SyncRun run, SyncState state,
        WebDavFileInfo metadata, byte[] remote)
    {
        RequireMasterPassword(run);
        try
        {
            await run.Core.VerifyAsync(remote, run.MasterPassword!);
            return null;
        }
        catch (CoreException ex) when (IsUnreadableRemote(ex))
        {
            return await RepairUnreadableRemoteAsync(run, state, metadata, ex);
        }
    }

    /// <summary>
    /// An unreadable remote copy never overwrites the local vault. By default the
    /// run pauses with a dirty baseline and asks the shell to offer the repair; only
    /// an explicit user choice (<see cref="OverwriteUnreadableRemote"/>) uploads the
    /// verified local copy over it.
    /// </summary>
    private async Task<SyncReport> RepairUnreadableRemoteAsync(SyncRun run, SyncState state,
        WebDavFileInfo metadata, CoreException cause)
    {
        if (!OverwriteUnreadableRemote)
        {
            state.LocalDirty = true;
            SaveState(run, state);
            AppLog.Warn("sync: the remote database cannot be opened with this password; " +
                        "keeping the local copy");
            Report(run.Status, "Remote unreadable");
            return new SyncReport(SyncOutcome.RemoteUnreadable,
                SyncNotice.RemoteUnreadable, Reason: cause.Code);
        }
        AppLog.Warn("sync: replacing an unreadable remote database with the verified local copy");
        byte[] localBytes = await run.Document.ReadBytesAsync();
        string? etag;
        try
        {
            etag = await run.Remote.UploadAsync(localBytes, metadata.ETag,
                run.Cancellation);
        }
        catch (WebDavException ex) when (ex.Conflict)
        {
            state.LocalDirty = true;
            SaveState(run, state);
            Report(run.Status, "Conflict");
            return new SyncReport(SyncOutcome.RemoteChanged,
                SyncNotice.RemoteUnreadableRemoteChanged);
        }
        await CommitSyncedAsync(run, state, localBytes, etag, metadata, "Remote repaired");
        return new SyncReport(SyncOutcome.Synced, SyncNotice.RemoteRepaired);
    }

    private static void RequireMasterPassword(SyncRun run)
    {
        if (string.IsNullOrEmpty(run.MasterPassword))
        {
            throw new InvalidOperationException(
                "Unlock the vault before applying remote changes.");
        }
    }

    private void SaveState(SyncRun run, SyncState state)
        => _states.Save(run.Url, run.Username, run.Document.Path, state);

    private void ClearConflictCopies()
    {
        _conflictLocalCopy = null;
        _conflictRemoteCopy = null;
    }

    private static void Report(Action<SyncStatusUpdate>? status, string source,
        params object?[] arguments)
        => status?.Invoke(new SyncStatusUpdate(source, arguments));

    private static string Sha256Hex(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
