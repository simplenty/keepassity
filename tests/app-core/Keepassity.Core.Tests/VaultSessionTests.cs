// SPDX-License-Identifier: GPL-3.0-or-later
using Keepassity.Core;

namespace Keepassity.CoreTests;

/// <summary>
/// The vault session is the one place where the single-writer rules live: the
/// cross-process lock, the in-process write gate and the content fingerprint.
/// These checks pin each rule down with a fake core and real files.
/// </summary>
internal static class VaultSessionTests
{
    public static void Run(TestCheck check, string dataDirectory)
    {
        LockIsExclusive(check, dataDirectory);
        FingerprintDetectsExternalChanges(check, dataDirectory);
        SaveIsSerialized(check, dataDirectory);
        SaveRefusesAnExternalChange(check, dataDirectory);
        ReplaceIsDurableAndBackedUp(check, dataDirectory);
        ReplaceRefusesAnExternalChange(check, dataDirectory);
        RestoreBacksUpOldBytes(check, dataDirectory);
        WriteTrackingDrainsBeforeExit(check, dataDirectory);
        FailedSaveLeavesTheSessionUsable(check, dataDirectory);
        ReadWaitsForInFlightWrite(check, dataDirectory);
        ReleaseForgetsTheFingerprint(check, dataDirectory);
    }

    private static void LockIsExclusive(TestCheck check, string dataDirectory)
    {
        (string folder, string path) = NewVault(dataDirectory, "lock", Bytes(1));
        using var first = new VaultSession();
        using var second = new VaultSession();
        check("vault session takes the lock",
            first.TryAcquire(path, out string? error) && error is null &&
            first.DatabasePath == Path.GetFullPath(path), error ?? "");
        check("a second session cannot take the same vault",
            !second.TryAcquire(path, out string? conflict) && conflict is not null,
            conflict ?? "");
        check("re-acquiring the same vault keeps the lock",
            first.TryAcquire(path, out _) && first.DatabasePath == Path.GetFullPath(path));
        first.Release();
        check("releasing the lock lets another session take it",
            second.TryAcquire(path, out _));
        second.Release();
        check("switching vaults releases the previous lock",
            first.TryAcquire(path, out _) && first.DatabasePath == Path.GetFullPath(path));
        string other = Path.Combine(folder, "other.kdbx");
        File.WriteAllBytes(other, Bytes(2));
        check("acquiring another vault moves the session",
            first.TryAcquire(other, out _) && first.DatabasePath == Path.GetFullPath(other) &&
            second.TryAcquire(path, out _));
        second.Release();
    }

    private static void FingerprintDetectsExternalChanges(TestCheck check, string dataDirectory)
    {
        (_, string path) = NewVault(dataDirectory, "fingerprint", Bytes(1, 1));
        using var session = new VaultSession();
        _ = session.TryAcquire(path, out _);
        session.CaptureFingerprint();
        check("unchanged file is not reported as changed", !session.ChangedOnDisk());
        File.WriteAllBytes(path, Bytes(2, 2));
        check("an external write is detected", session.ChangedOnDisk());
        session.CaptureFingerprint();
        check("capturing again accepts the new bytes", !session.ChangedOnDisk());
        File.Delete(path);
        check("a deleted file is reported as changed", session.ChangedOnDisk());
    }

    private static void SaveIsSerialized(TestCheck check, string dataDirectory)
    {
        (_, string path) = NewVault(dataDirectory, "serialize", Bytes(1));
        using var session = new VaultSession();
        _ = session.TryAcquire(path, out _);
        session.CaptureFingerprint();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var core = new FakeCore
        {
            OnSave = async () =>
            {
                entered.TrySetResult();
                await release.Task.ConfigureAwait(false);
            },
        };
        Task first = session.SaveAsync(core);
        entered.Task.GetAwaiter().GetResult();
        Task second = session.SaveAsync(core);
        // The second save waits on the write gate: it has not reached the core.
        check("a save waits for the gate instead of reaching the core",
            core.SaveCalls == 1, core.SaveCalls.ToString());
        release.SetResult();
        Task.WhenAll(first, second).GetAwaiter().GetResult();
        check("both queued saves reach the core exactly once",
            core.SaveCalls == 2 && core.MaxConcurrentSaves == 1,
            $"calls={core.SaveCalls} max={core.MaxConcurrentSaves}");
        check("a completed save leaves the new bytes fingerprinted",
            File.ReadAllBytes(path).SequenceEqual(core.ModelBytes) &&
            !session.ChangedOnDisk());
    }

    private static void SaveRefusesAnExternalChange(TestCheck check, string dataDirectory)
    {
        (_, string path) = NewVault(dataDirectory, "save-refused", Bytes(1));
        using var session = new VaultSession();
        _ = session.TryAcquire(path, out _);
        session.CaptureFingerprint();
        byte[] external = Bytes(9, 9);
        File.WriteAllBytes(path, external);
        var core = new FakeCore();

        CoreException? error = null;
        try
        {
            session.SaveAsync(core).GetAwaiter().GetResult();
        }
        catch (CoreException ex)
        {
            error = ex;
        }

        check("a save refuses a file changed outside the application",
            error?.Code == "VaultChanged" && core.SaveCalls == 0,
            error?.Code ?? "no exception");
        check("a refused save leaves the external bytes in place",
            File.ReadAllBytes(path).SequenceEqual(external));
    }

    private static void ReplaceIsDurableAndBackedUp(TestCheck check, string dataDirectory)
    {
        (string folder, string path) = NewVault(dataDirectory, "replace", Bytes(1, 1, 1));
        byte[] merged = Bytes(4, 5, 6);
        using var session = new VaultSession();
        _ = session.TryAcquire(path, out _);
        session.CaptureFingerprint();

        session.ReplaceAsync(merged, backupRetention: 5, reason: "sync")
            .GetAwaiter().GetResult();

        check("a replacement writes the new bytes",
            File.ReadAllBytes(path).SequenceEqual(merged));
        check("a replacement updates the fingerprint", !session.ChangedOnDisk());
        List<BackupEntry> backups = BackupService.ListBackups(path);
        check("a replacement backs up the previous bytes",
            backups.Count == 1 &&
            File.ReadAllBytes(backups[0].Path).SequenceEqual(Bytes(1, 1, 1)),
            backups.Count.ToString());
        check("a replacement leaves no temporary file behind",
            Directory.GetFiles(folder, "*keepassity-tmp*").Length == 0);
    }

    private static void ReplaceRefusesAnExternalChange(TestCheck check, string dataDirectory)
    {
        (_, string path) = NewVault(dataDirectory, "replace-refused", Bytes(1));
        using var session = new VaultSession();
        _ = session.TryAcquire(path, out _);
        session.CaptureFingerprint();
        byte[] external = Bytes(8, 8, 8);
        File.WriteAllBytes(path, external);

        bool refused = false;
        try
        {
            session.ReplaceAsync(Bytes(2, 2)).GetAwaiter().GetResult();
        }
        catch (CoreException ex) when (ex.Code == "VaultChanged")
        {
            refused = true;
        }

        check("a replacement refuses a file changed outside the application",
            refused && File.ReadAllBytes(path).SequenceEqual(external));
    }

    private static void RestoreBacksUpOldBytes(TestCheck check, string dataDirectory)
    {
        (_, string path) = NewVault(dataDirectory, "restore", Bytes(1, 1));
        byte[] restored = Bytes(7, 7, 7);
        var session = new VaultSession();
        _ = session.TryAcquire(path, out _);
        session.CaptureFingerprint();
        bool restoredInsideTheGate = false;
        bool fingerprintNotYetUpdatedInsideTheGate = false;

        session.RestoreAsync(restored, backupRetention: 5, afterReplace: () =>
        {
            // The reopen callback runs inside the write gate and before the new
            // fingerprint is recorded: the file already holds the restored bytes,
            // and the session still expects the pre-restore content.
            restoredInsideTheGate = File.ReadAllBytes(path).SequenceEqual(restored);
            fingerprintNotYetUpdatedInsideTheGate = session.ChangedOnDisk();
            return Task.CompletedTask;
        }).GetAwaiter().GetResult();

        check("a restore writes the restored bytes",
            File.ReadAllBytes(path).SequenceEqual(restored));
        check("the reopen callback runs after the write and before the new fingerprint",
            restoredInsideTheGate && fingerprintNotYetUpdatedInsideTheGate);
        check("a restore updates the fingerprint", !session.ChangedOnDisk());
        List<BackupEntry> backups = BackupService.ListBackups(path);
        check("a restore backs up the previous bytes",
            backups.Count == 1 &&
            File.ReadAllBytes(backups[0].Path).SequenceEqual(Bytes(1, 1)),
            backups.Count.ToString());

        bool rejectedEmpty = false;
        try
        {
            session.RestoreAsync(Array.Empty<byte>()).GetAwaiter().GetResult();
        }
        catch (ArgumentException)
        {
            rejectedEmpty = true;
        }
        check("a restore refuses an empty backup", rejectedEmpty);
        check("a refused empty restore leaves the vault untouched",
            File.ReadAllBytes(path).SequenceEqual(restored) && !session.ChangedOnDisk());
        session.Dispose();
    }

    private static void WriteTrackingDrainsBeforeExit(TestCheck check, string dataDirectory)
    {
        (_, string path) = NewVault(dataDirectory, "drain", Bytes(1));
        var session = new VaultSession();
        _ = session.TryAcquire(path, out _);
        session.CaptureFingerprint();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var core = new FakeCore
        {
            ModelBytes = Bytes(5, 5, 5),
            OnSave = async () =>
            {
                entered.TrySetResult();
                await release.Task.ConfigureAwait(false);
            },
        };
        Task save = session.SaveAsync(core);
        entered.Task.GetAwaiter().GetResult();

        check("a running save is reported as in flight", session.IsWriting);
        Task idle = session.WaitForIdleAsync(TimeSpan.FromSeconds(5));
        check("waiting for idle does not complete while a save runs", !idle.IsCompleted);

        bool timeoutReturned = false;
        try
        {
            session.WaitForIdleAsync(TimeSpan.FromMilliseconds(30)).GetAwaiter().GetResult();
            timeoutReturned = true;
        }
        catch (OperationCanceledException)
        {
            timeoutReturned = false;
        }
        check("waiting for idle times out without throwing",
            timeoutReturned && session.IsWriting);

        release.SetResult();
        save.GetAwaiter().GetResult();
        idle.GetAwaiter().GetResult();
        check("the save finishes and the session becomes idle",
            !session.IsWriting && File.ReadAllBytes(path).SequenceEqual(Bytes(5, 5, 5)));
        session.WaitForIdleAsync(TimeSpan.FromMilliseconds(30)).GetAwaiter().GetResult();
        check("waiting for an idle session returns immediately", !session.IsWriting);

        // Shutdown timeout: disposing the session while the writer is in flight
        // must not cut the durable write short or throw from the writer.
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredAgain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateCore = new FakeCore
        {
            ModelBytes = Bytes(6, 6),
            OnSave = async () =>
            {
                enteredAgain.TrySetResult();
                await blocked.Task.ConfigureAwait(false);
            },
        };
        Task lateSave = session.SaveAsync(lateCore);
        enteredAgain.Task.GetAwaiter().GetResult();
        session.Dispose();
        blocked.SetResult();
        bool writerSurvived = true;
        try
        {
            lateSave.GetAwaiter().GetResult();
        }
        catch (ObjectDisposedException)
        {
            writerSurvived = false;
        }
        check("disposing during a save lets the writer finish",
            writerSurvived && File.ReadAllBytes(path).SequenceEqual(Bytes(6, 6)));
    }

    private static void FailedSaveLeavesTheSessionUsable(TestCheck check, string dataDirectory)
    {
        (_, string path) = NewVault(dataDirectory, "save-failed", Bytes(1));
        using var session = new VaultSession();
        _ = session.TryAcquire(path, out _);
        session.CaptureFingerprint();
        int calls = 0;
        var core = new FakeCore
        {
            ModelBytes = Bytes(3, 3),
            OnSave = () =>
            {
                calls++;
                if (calls == 1)
                {
                    throw new IOException("the disk is full");
                }
                return Task.CompletedTask;
            },
        };

        bool failed = false;
        try
        {
            session.SaveAsync(core).GetAwaiter().GetResult();
        }
        catch (IOException)
        {
            failed = true;
        }

        check("a failed save reports the error instead of being silent", failed);
        check("a failed save releases the write gate", !session.IsWriting);
        check("the file keeps the last complete bytes after a failed save",
            File.ReadAllBytes(path).SequenceEqual(Bytes(1)));
        session.SaveAsync(core).GetAwaiter().GetResult();
        check("a retry after a failed save succeeds", !session.IsWriting &&
            File.ReadAllBytes(path).SequenceEqual(Bytes(3, 3)));
    }

    private static void ReadWaitsForInFlightWrite(TestCheck check, string dataDirectory)
    {
        (_, string path) = NewVault(dataDirectory, "read-gate", Bytes(1));
        using var session = new VaultSession();
        _ = session.TryAcquire(path, out _);
        session.CaptureFingerprint();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var core = new FakeCore
        {
            ModelBytes = Bytes(8, 8, 8),
            OnSave = async () =>
            {
                entered.TrySetResult();
                await release.Task.ConfigureAwait(false);
            },
        };
        Task save = session.SaveAsync(core);
        entered.Task.GetAwaiter().GetResult();
        Task<byte[]> read = session.ReadBytesAsync();
        check("a sync read waits for the in-flight save", !read.IsCompleted);
        release.SetResult();
        save.GetAwaiter().GetResult();
        byte[] bytes = read.GetAwaiter().GetResult();
        check("a sync read returns the saved bytes",
            bytes.SequenceEqual(Bytes(8, 8, 8)) && !session.IsWriting);
        session.WaitForIdleAsync(TimeSpan.FromMilliseconds(30)).GetAwaiter().GetResult();

        session.Release();
        bool refused = false;
        try
        {
            session.ReadBytesAsync().GetAwaiter().GetResult();
        }
        catch (InvalidOperationException)
        {
            refused = true;
        }
        check("a released session refuses to read", refused);
    }

    private static void ReleaseForgetsTheFingerprint(TestCheck check, string dataDirectory)
    {
        (_, string path) = NewVault(dataDirectory, "release", Bytes(1));
        using var session = new VaultSession();
        _ = session.TryAcquire(path, out _);
        session.CaptureFingerprint();
        File.WriteAllBytes(path, Bytes(2));
        check("a changed vault is detected before release", session.ChangedOnDisk());
        session.Release();
        check("a released session has no database or fingerprint",
            session.DatabasePath is null && !session.ChangedOnDisk());
        bool refused = false;
        try
        {
            session.SaveAsync(new FakeCore()).GetAwaiter().GetResult();
        }
        catch (InvalidOperationException)
        {
            refused = true;
        }
        check("a released session refuses to save", refused);
    }

    private static (string Folder, string Path) NewVault(string dataDirectory, string name,
        byte[] content)
    {
        string folder = Path.Combine(dataDirectory,
            "vault-session-" + name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "vault.kdbx");
        File.WriteAllBytes(path, content);
        return (folder, path);
    }

    private static byte[] Bytes(params byte[] values) => values;

    private sealed class FakeCore : ICoreClient
    {
        public byte[] ModelBytes { get; set; } = new byte[] { 4, 2, 0 };

        public Func<Task>? OnSave { get; set; }

        public int SaveCalls { get; private set; }

        public int MaxConcurrentSaves { get; private set; }

        private int _concurrentSaves;

        public bool IsOpen => false;

        public string? DatabasePath { get; private set; }

        public async Task SaveAsync(string path, int backupRetention = 20,
            Func<bool>? databaseChanged = null)
        {
            SaveCalls++;
            _concurrentSaves++;
            MaxConcurrentSaves = Math.Max(MaxConcurrentSaves, _concurrentSaves);
            try
            {
                if (OnSave is not null)
                {
                    await OnSave().ConfigureAwait(false);
                }
                if (databaseChanged?.Invoke() == true)
                {
                    throw new CoreException("VaultChanged", "changed outside");
                }
                File.WriteAllBytes(path, ModelBytes);
                DatabasePath = path;
            }
            finally
            {
                _concurrentSaves--;
            }
        }

        public Task OpenAsync(string path, string password) => throw new NotSupportedException();

        public Task OpenBytesAsync(byte[] kdbx, string password, string? databasePath = null)
            => throw new NotSupportedException();

        public Task LockAsync() => Task.CompletedTask;

        public Task<byte[]> SaveBytesAsync() => throw new NotSupportedException();

        public Task VerifyAsync(byte[] bytes, string password) => throw new NotSupportedException();

        public Task<SyncDecisionDto> SyncDecideAsync(string remoteMetaJson, bool localDirty)
            => throw new NotSupportedException();

        public Task SyncCommitAsync(string? etag, string? sha256)
            => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
