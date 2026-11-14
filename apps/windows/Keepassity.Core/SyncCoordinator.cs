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
    private string? _conflictSignature;
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
        CancellationToken Cancellation);

    private async Task<SyncReport> ExecuteAsync(SyncRun run)
    {
        SyncState state = _states.Load(run.Url, run.Username, run.Document.Path);
        if (!string.IsNullOrEmpty(state.Sha256))
        {
            await run.Core.SyncCommitAsync(state.Etag, state.Sha256);
            // The sidecar is only clean if the local file still is the bytes that
            // were synced. A crash between the database write and the sidecar
            // write leaves the old sidecar behind; treat the vault as dirty
            // instead of letting the next sync no-op and lose the upload.
            if (File.Exists(run.Document.Path))
            {
                byte[] localBytes = await run.Document.ReadBytesAsync();
                if (!string.Equals(Sha256Hex(localBytes), state.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    AppLog.Warn(
                        "sync: local database differs from the synced baseline; treating it as dirty");
                    state.LocalDirty = true;
                }
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
        await run.Document.ReplaceAsync(bytes);
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
            // corrupted body must not become the local vault.
            RequireMasterPassword(run);
            await run.Core.VerifyAsync(bytes, run.MasterPassword!);
            await run.Document.ReplaceAsync(bytes);
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
        bool autoMerged = merged.Conflicts.Count == 0;
        await run.Document.ReplaceAsync(merged.Bytes);
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
        RequireMasterPassword(run);
        await run.Core.VerifyAsync(remote, run.MasterPassword!);
        await run.Document.ReplaceAsync(remote);
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
    /// Keep one local/remote copy pair for an unresolved conflict. A sync that
    /// runs again against the same pair (the user cancelled the dialog, or every
    /// later save retries the merge) reuses the existing copies instead of
    /// writing another pair into the vault folder.
    /// </summary>
    private async Task<(string? LocalCopy, string? RemoteCopy)> PrepareConflictCopiesAsync(
        SyncRun run, byte[] remote)
    {
        string? signature = await ConflictSignatureAsync(run, remote);
        if (signature is not null && signature == _conflictSignature &&
            _conflictLocalCopy is not null && File.Exists(_conflictLocalCopy) &&
            _conflictRemoteCopy is not null && File.Exists(_conflictRemoteCopy))
        {
            return (_conflictLocalCopy, _conflictRemoteCopy);
        }
        _conflictSignature = signature;
        _conflictLocalCopy = CreateLocalConflictCopy(run.Document.Path);
        _conflictRemoteCopy = CreateRemoteConflictCopy(run.Document.Path, remote);
        return (_conflictLocalCopy, _conflictRemoteCopy);
    }

    private async Task<string?> ConflictSignatureAsync(SyncRun run, byte[] remote)
    {
        try
        {
            byte[] local = await run.Document.ReadBytesAsync();
            return Sha256Hex(local) + ":" + Sha256Hex(remote);
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

    private static string? CreateLocalConflictCopy(string databasePath)
    {
        string directory = Path.GetDirectoryName(databasePath)!;
        string stem = Path.GetFileNameWithoutExtension(databasePath);
        string extension = Path.GetExtension(databasePath);
        string stamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
        string path = Path.Combine(directory, $"{stem} (conflict {stamp}){extension}");
        int suffix = 1;
        while (File.Exists(path))
        {
            path = Path.Combine(directory, $"{stem} (conflict {stamp}-{suffix++}){extension}");
        }
        AtomicFile.Copy(databasePath, path);
        return path;
    }

    private static string? CreateRemoteConflictCopy(string databasePath, byte[] remote)
    {
        string directory = Path.GetDirectoryName(databasePath)!;
        string stem = Path.GetFileNameWithoutExtension(databasePath);
        string extension = Path.GetExtension(databasePath);
        string stamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
        string path = Path.Combine(directory, $"{stem} (remote conflict {stamp}){extension}");
        int suffix = 1;
        while (File.Exists(path))
        {
            path = Path.Combine(directory,
                $"{stem} (remote conflict {stamp}-{suffix++}){extension}");
        }
        AtomicFile.Write(path, remote);
        return path;
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
        _conflictSignature = null;
        _conflictLocalCopy = null;
        _conflictRemoteCopy = null;
    }

    private static void Report(Action<SyncStatusUpdate>? status, string source,
        params object?[] arguments)
        => status?.Invoke(new SyncStatusUpdate(source, arguments));

    private static string Sha256Hex(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
