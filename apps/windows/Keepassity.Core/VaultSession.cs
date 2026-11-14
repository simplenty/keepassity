// SPDX-License-Identifier: GPL-3.0-or-later
namespace Keepassity.Core;

/// <summary>
/// The single-writer guard for one vault. A password manager has three ways to
/// lose data on write, and this class makes the window apply all three
/// consistently instead of composing them at each call site:
///
/// <list type="bullet">
/// <item>a cross-process <see cref="VaultFileLock"/>, so two Keepassity windows
/// cannot own the same database;</item>
/// <item>an in-process <see cref="VaultWriteGate"/>, so an older async snapshot
/// cannot land after a newer one;</item>
/// <item>a <see cref="VaultFingerprint"/> of the bytes the model came from, so a
/// file changed by another program is refused instead of overwritten.</item>
/// </list>
///
/// Saves, sync replacements and backup restores all go through here. The caller
/// still owns opening the core and refreshing the UI around it.
/// </summary>
public sealed class VaultSession : IDisposable
{
    private readonly VaultWriteGate _writes = new();
    private readonly object _writeStateSync = new();
    private VaultFileLock? _lock;
    private VaultFingerprint? _fingerprint;
    private string? _fingerprintPath;
    private int _activeWrites;
    private TaskCompletionSource? _writesIdle;

    /// <summary>Absolute path of the acquired vault, or null before/after a release.</summary>
    public string? DatabasePath { get; private set; }

    /// <summary>
    /// Take the exclusive lock for <paramref name="databasePath"/> and remember it
    /// as the current database. Re-acquiring the current path keeps the lock.
    /// Returns false with <paramref name="error"/> when another window owns the
    /// vault (or the lock file cannot be created).
    /// </summary>
    public bool TryAcquire(string databasePath, out string? error)
    {
        error = null;
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(databasePath);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        if (_lock is not null && string.Equals(_lock.DatabasePath, fullPath,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        // Keep at most one lock: switching databases releases the previous vault.
        _lock?.Dispose();
        _lock = null;
        DatabasePath = null;
        _lock = VaultFileLock.TryAcquire(fullPath, out error);
        if (_lock is null)
        {
            return false;
        }
        DatabasePath = fullPath;
        return true;
    }

    /// <summary>Drop the lock and the fingerprint. The session can be reused.</summary>
    public void Release()
    {
        _lock?.Dispose();
        _lock = null;
        DatabasePath = null;
        _fingerprint = null;
        _fingerprintPath = null;
    }

    /// <summary>
    /// Remember the bytes the open model came from. Call after opening the vault,
    /// after every successful write and after a WebDAV download is promoted.
    /// </summary>
    public void CaptureFingerprint(string? databasePath = null)
    {
        string? path = databasePath ?? DatabasePath;
        _fingerprint = null;
        _fingerprintPath = null;
        if (path is null || !File.Exists(path))
        {
            return;
        }
        try
        {
            _fingerprint = VaultFingerprint.Capture(path);
            _fingerprintPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"cannot fingerprint the database: {ex.Message}");
        }
    }

    /// <summary>
    /// True when the file on disk no longer matches the bytes the model came
    /// from. A missing or unreadable file also counts as changed, so a save
    /// cannot recreate a file another program moved. Without a captured
    /// fingerprint there is nothing to compare and this returns false.
    /// </summary>
    public bool ChangedOnDisk(string? databasePath = null)
    {
        if (_fingerprint is not VaultFingerprint expected)
        {
            return false;
        }
        string? path = databasePath ?? _fingerprintPath ?? DatabasePath;
        if (path is null || !File.Exists(path))
        {
            return true;
        }
        try
        {
            return !VaultFingerprint.Capture(path).Matches(expected);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// True while a save, sync replacement or restore is queued or running. The
    /// shell uses this to drain in-flight writes before the process exits, so an
    /// edit that is already being written is not cut off by shutdown.
    /// </summary>
    public bool IsWriting
    {
        get
        {
            lock (_writeStateSync)
            {
                return _activeWrites > 0;
            }
        }
    }

    /// <summary>
    /// Wait until no write is queued or running. Returns when the vault is idle
    /// or when <paramref name="timeout"/> elapses; the caller may close anyway
    /// then, because the durable protocol always leaves the last complete file.
    /// </summary>
    public async Task WaitForIdleAsync(TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutSource.Token);
        while (true)
        {
            Task idle;
            lock (_writeStateSync)
            {
                if (_activeWrites == 0)
                {
                    return;
                }
                _writesIdle ??=
                    new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                idle = _writesIdle.Task;
            }
            try
            {
                await idle.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (timeoutSource.IsCancellationRequested &&
                      !cancellationToken.IsCancellationRequested)
            {
                // The shell closes after the bounded wait; the file is either
                // the previous or the next complete database, never a partial one.
                return;
            }
        }
    }

    /// <summary>
    /// Read the database bytes through the same gate that serializes writes.
    /// Without this a sync can hold the file open exactly while a save replaces
    /// it, and Windows then fails the replace with a sharing violation; reading
    /// under the gate means sync always sees a complete database and the save
    /// never collides with it. Counted as an operation for the shutdown drain.
    /// </summary>
    public async Task<byte[]> ReadBytesAsync(CancellationToken cancellationToken = default)
    {
        BeginWrite();
        try
        {
            string path = AcquiredPath();
            using IDisposable read = await _writes.EnterAsync().ConfigureAwait(false);
            return await File.ReadAllBytesAsync(path, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            EndWrite();
        }
    }

    /// <summary>
    /// Save the open model through <paramref name="core"/>: wait for the write
    /// gate, refuse when the file changed outside the application (checked once
    /// here and once more after serialization by the core), then remember the
    /// new bytes. Throws <see cref="CoreException"/> with code <c>VaultChanged</c>
    /// when the save was refused.
    /// </summary>
    public async Task SaveAsync(ICoreClient core, int backupRetention = 20)
    {
        ArgumentNullException.ThrowIfNull(core);
        BeginWrite();
        try
        {
            string path = AcquiredPath();
            using IDisposable write = await _writes.EnterAsync().ConfigureAwait(false);
            EnsureUnchanged(path);
            await core.SaveAsync(path, backupRetention, () => ChangedOnDisk(path))
                .ConfigureAwait(false);
            CaptureFingerprint(path);
        }
        finally
        {
            EndWrite();
        }
    }

    /// <summary>
    /// Replace the open vault with already verified bytes (sync download or
    /// merge) using the durable write protocol, including a backup.
    /// </summary>
    public async Task ReplaceAsync(byte[] bytes, int backupRetention = 20,
        string reason = "sync")
    {
        BeginWrite();
        try
        {
            string path = AcquiredPath();
            using IDisposable write = await _writes.EnterAsync().ConfigureAwait(false);
            EnsureUnchanged(path);
            await Task.Run(() => AtomicFile.Replace(path, bytes, backupRetention, reason,
                    () => ChangedOnDisk(path)))
                .ConfigureAwait(false);
            CaptureFingerprint(path);
        }
        finally
        {
            EndWrite();
        }
    }

    /// <summary>
    /// Replace the open vault with backup bytes: the current file is copied to
    /// the backup area first, then replaced atomically. <paramref name="afterReplace"/>
    /// runs inside the write gate, which is where reopening the core from the
    /// restored bytes belongs.
    /// </summary>
    public async Task RestoreAsync(byte[] content, int backupRetention = 20,
        Func<Task>? afterReplace = null)
    {
        BeginWrite();
        try
        {
            string path = AcquiredPath();
            using IDisposable write = await _writes.EnterAsync().ConfigureAwait(false);
            EnsureUnchanged(path);
            await Task.Run(() => BackupService.RestoreBytes(path, content, backupRetention,
                    () => ChangedOnDisk(path)))
                .ConfigureAwait(false);
            if (afterReplace is not null)
            {
                await afterReplace().ConfigureAwait(false);
            }
            CaptureFingerprint(path);
        }
        finally
        {
            EndWrite();
        }
    }

    /// <summary>Release the lock and dispose the write gate.</summary>
    public void Dispose()
    {
        Release();
        lock (_writeStateSync)
        {
            if (_activeWrites > 0)
            {
                // A writer is still in flight after the shutdown drain timeout.
                // Leave the gate alive for it; the process is exiting and the
                // durable protocol keeps the file whole.
                AppLog.Warn("vault session disposed with a write still in flight");
                return;
            }
        }
        _writes.Dispose();
    }

    private string AcquiredPath() => DatabasePath ??
        throw new InvalidOperationException("no vault is acquired");

    private void BeginWrite()
    {
        lock (_writeStateSync)
        {
            _activeWrites++;
        }
    }

    private void EndWrite()
    {
        TaskCompletionSource? idle = null;
        lock (_writeStateSync)
        {
            _activeWrites--;
            if (_activeWrites == 0)
            {
                idle = _writesIdle;
                _writesIdle = null;
            }
        }
        idle?.TrySetResult();
    }

    private void EnsureUnchanged(string path)
    {
        if (!ChangedOnDisk(path))
        {
            return;
        }
        AppLog.Warn("vault write refused: the database changed outside this window");
        throw new CoreException("VaultChanged",
            "the database changed outside this application; nothing was overwritten");
    }
}
