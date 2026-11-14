// SPDX-License-Identifier: GPL-3.0-or-later
// Behavioural checks for the first MVVM view model. These run headless and never
// load WinUI or the native library.
using Keepassity.Core;
using Keepassity.ViewModels;

namespace Keepassity.CoreTests;

internal static class UnlockViewModelTests
{
    private static Action<string, bool, string> _report = (_, _, _) => { };

    private static void Check(string name, bool ok) => _report(name, ok, string.Empty);

    private static void Check(string name, bool ok, string detail) => _report(name, ok, detail);

    public static void Run(Action<string, bool, string> check, string dataDirectory)
    {
        _report = check;
        MissingDatabaseIsRejected();
        EmptyPasswordIsRejected();
        WrongPasswordIsClassified();
        CorruptDatabaseIsClassified();
        UnsupportedDatabaseIsClassified();
        SuccessClearsTheBanner();
        ConcurrentAttemptsAreRejected();
        ChangingTheDatabaseClearsStaleErrors();
        PendingRemoteDownloadOpensItsBytesFirst(dataDirectory);
        PendingRemoteDownloadSurvivesAWrongPassword(dataDirectory);
    }

    private static void MissingDatabaseIsRejected()
    {
        var core = new FakeCoreClient();
        var vm = new UnlockViewModel(core, FakeText.Instance);
        bool unlocked = vm.UnlockAsync("secret").GetAwaiter().GetResult();
        Check("unlock rejects a missing database", !unlocked);
        Check("unlock missing database shows a message",
            vm.IsStatusOpen && vm.StatusTitle == "Cannot open database" &&
            vm.StatusMessage == "Choose a .kdbx database first." &&
            vm.StatusSeverity == UnlockStatusSeverity.Error && !vm.RecoveryVisible,
            $"{vm.StatusTitle} / {vm.StatusMessage}");
        Check("unlock missing database never opens the core", core.OpenCalls == 0);
    }

    private static void EmptyPasswordIsRejected()
    {
        var core = new FakeCoreClient();
        var vm = new UnlockViewModel(core, FakeText.Instance);
        vm.SetDatabasePath(@"C:\vault.kdbx");
        bool unlocked = vm.UnlockAsync(string.Empty).GetAwaiter().GetResult();
        Check("unlock rejects an empty password", !unlocked);
        Check("unlock empty password shows a message",
            vm.IsStatusOpen && vm.StatusMessage == "Enter the master password.",
            vm.StatusMessage);
        Check("unlock empty password never opens the core", core.OpenCalls == 0);
    }

    private static void WrongPasswordIsClassified()
    {
        var core = new FakeCoreClient
        {
            OnOpen = (_, _) => Task.FromException(new CoreException("BadPassword", "bad password")),
        };
        var vm = new UnlockViewModel(core, FakeText.Instance);
        vm.SetDatabasePath(@"C:\vault.kdbx");
        bool unlocked = vm.UnlockAsync("wrong").GetAwaiter().GetResult();
        Check("unlock reports a wrong password", !unlocked);
        Check("wrong password uses its own title",
            vm.StatusTitle == "Wrong master password" && vm.RecoveryVisible,
            vm.StatusTitle);
        Check("wrong password leaves the busy state", !vm.IsBusy && vm.CanUnlock);
    }

    private static void CorruptDatabaseIsClassified()
    {
        var core = new FakeCoreClient
        {
            OnOpen = (_, _) => Task.FromException(
                new CoreException("CorruptDatabase", "damaged")),
        };
        var vm = new UnlockViewModel(core, FakeText.Instance);
        vm.SetDatabasePath(@"C:\vault.kdbx");
        bool unlocked = vm.UnlockAsync("secret").GetAwaiter().GetResult();
        Check("unlock reports a damaged database", !unlocked);
        Check("damaged database keeps its core error code",
            vm.LastErrorCode == "CorruptDatabase", vm.LastErrorCode ?? "(none)");
        Check("damaged database uses its own title",
            vm.StatusTitle == "Database is damaged" && vm.RecoveryVisible,
            vm.StatusTitle);
    }

    private static void UnsupportedDatabaseIsClassified()
    {
        var core = new FakeCoreClient
        {
            OnOpen = (_, _) => Task.FromException(
                new CoreException("UnsupportedFormat", "unsupported")),
        };
        var vm = new UnlockViewModel(core, FakeText.Instance);
        vm.SetDatabasePath(@"C:\vault.kdbx");
        _ = vm.UnlockAsync("secret").GetAwaiter().GetResult();
        Check("unsupported database uses its own title",
            vm.StatusTitle == "Unsupported database", vm.StatusTitle);
    }

    private static void SuccessClearsTheBanner()
    {
        var core = new FakeCoreClient();
        var vm = new UnlockViewModel(core, FakeText.Instance);
        vm.SetDatabasePath(@"C:\vault.kdbx");
        // Seed a stale error, which a successful unlock must clear.
        _ = vm.UnlockAsync(string.Empty).GetAwaiter().GetResult();
        bool unlocked = vm.UnlockAsync("correct").GetAwaiter().GetResult();
        Check("unlock succeeds for the right password", unlocked);
        Check("successful unlock clears the status banner",
            !vm.IsStatusOpen && vm.StatusMessage.Length == 0 && !vm.RecoveryVisible);
        Check("successful unlock passes the selected path and password",
            core.OpenedPath == @"C:\vault.kdbx" && core.OpenedPassword == "correct");
        Check("successful unlock clears the last error code", vm.LastErrorCode is null,
            vm.LastErrorCode ?? "(none)");
        Check("successful unlock leaves the view model idle", !vm.IsBusy && vm.CanUnlock);
    }

    private static void ConcurrentAttemptsAreRejected()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var core = new FakeCoreClient
        {
            OnOpen = async (_, _) => await gate.Task.ConfigureAwait(false),
        };
        var vm = new UnlockViewModel(core, FakeText.Instance);
        vm.SetDatabasePath(@"C:\vault.kdbx");
        Task<bool> first = vm.UnlockAsync("correct");
        Check("unlock sets the busy state before awaiting",
            vm.IsBusy && !vm.CanUnlock && !vm.IsInputEnabled);
        bool second = vm.UnlockAsync("again").GetAwaiter().GetResult();
        Check("a second unlock while busy is ignored", !second);
        Check("a busy unlock does not reach the core twice", core.OpenCalls == 1,
            core.OpenCalls.ToString());
        gate.SetResult();
        Check("the in-flight unlock completes", first.GetAwaiter().GetResult());
        Check("the view model returns to idle", !vm.IsBusy && vm.CanUnlock);
    }

    private static void ChangingTheDatabaseClearsStaleErrors()
    {
        var core = new FakeCoreClient
        {
            OnOpen = (_, _) => Task.FromException(new CoreException("BadPassword", "bad")),
        };
        var vm = new UnlockViewModel(core, FakeText.Instance);
        vm.SetDatabasePath(@"C:\first.kdbx");
        _ = vm.UnlockAsync("wrong").GetAwaiter().GetResult();
        Check("a failed unlock is visible before switching", vm.IsStatusOpen);
        vm.SetDatabasePath(@"C:\second.kdbx");
        Check("switching databases clears the stale error",
            !vm.IsStatusOpen && !vm.RecoveryVisible && vm.HasDatabase);
    }

    /// <summary>
    /// A WebDAV download is opened from its bytes, so the working copy is only
    /// rewritten after the master password proved that the download decrypts.
    /// </summary>
    private static void PendingRemoteDownloadOpensItsBytesFirst(string dataDirectory)
    {
        string folder = NewFolder(dataDirectory, "pending-promote");
        string workingCopy = Path.Combine(folder, "vault.kdbx");
        byte[] oldBytes = { 1, 1, 1 };
        byte[] pendingBytes = { 7, 7, 7, 7 };
        File.WriteAllBytes(workingCopy, oldBytes);
        var states = new SyncStateStore(Path.Combine(folder, "state"));
        var core = new FakeCoreClient();
        var vm = new UnlockViewModel(core, FakeText.Instance, new RemoteVaultOpener(states));
        vm.SetDatabasePath(workingCopy);
        vm.SetPendingRemote(Pending(url: "https://example.test/pending.kdbx", workingCopy,
            pendingBytes));

        bool unlocked = vm.UnlockAsync("correct").GetAwaiter().GetResult();

        Check("a pending remote download unlocks its bytes",
            unlocked && core.OpenBytesCalls == 1, core.OpenBytesCalls.ToString());
        Check("a pending remote download never opens the stale path", core.OpenCalls == 0);
        Check("promoting a pending download writes the verified bytes",
            File.ReadAllBytes(workingCopy).SequenceEqual(pendingBytes));
        Check("a promoted pending download is forgotten", vm.PendingRemote is null);
        SyncState state = states.Load("https://example.test/pending.kdbx", "user",
            workingCopy);
        Check("promoting a pending download records a clean baseline",
            !state.LocalDirty && state.Etag == "\"e1\"" &&
            state.Sha256 == Sha(pendingBytes), state.Sha256 ?? "(none)");
    }

    private static void PendingRemoteDownloadSurvivesAWrongPassword(string dataDirectory)
    {
        string folder = NewFolder(dataDirectory, "pending-retry");
        string workingCopy = Path.Combine(folder, "vault.kdbx");
        byte[] oldBytes = { 2, 2, 2 };
        byte[] pendingBytes = { 8, 8 };
        File.WriteAllBytes(workingCopy, oldBytes);
        var states = new SyncStateStore(Path.Combine(folder, "state"));
        var core = new FakeCoreClient
        {
            OnOpenBytes = (_, _, _) => Task.FromException(
                new CoreException("BadPassword", "incorrect master password")),
        };
        var vm = new UnlockViewModel(core, FakeText.Instance, new RemoteVaultOpener(states));
        vm.SetDatabasePath(workingCopy);
        vm.SetPendingRemote(Pending(url: "https://example.test/retry.kdbx", workingCopy,
            pendingBytes));

        bool unlocked = vm.UnlockAsync("wrong").GetAwaiter().GetResult();

        Check("a wrong password rejects the pending download", !unlocked);
        Check("a wrong password uses the wrong-password title",
            vm.StatusTitle == "Wrong master password", vm.StatusTitle);
        Check("a wrong password leaves the working copy untouched",
            File.ReadAllBytes(workingCopy).SequenceEqual(oldBytes));
        Check("a wrong password keeps the pending download for a retry",
            vm.PendingRemote is not null);

        core.OnOpenBytes = null;
        bool retried = vm.UnlockAsync("correct").GetAwaiter().GetResult();
        Check("the retried password promotes the held download",
            retried && File.ReadAllBytes(workingCopy).SequenceEqual(pendingBytes) &&
            vm.PendingRemote is null);
    }

    private static PendingRemoteDatabase Pending(string url, string workingCopy,
        byte[] bytes) => new()
    {
        Url = url,
        Username = "user",
        WorkingCopyPath = workingCopy,
        Metadata = new WebDavFileInfo
        {
            ETag = "\"e1\"",
            Size = bytes.Length,
            ModifiedUnix = 42,
        },
        Bytes = bytes,
    };

    private static string NewFolder(string dataDirectory, string name)
    {
        string folder = Path.Combine(dataDirectory,
            "unlock-" + name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string Sha(byte[] bytes)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
            .ToLowerInvariant();

    private sealed class FakeText : IAppText
    {
        public static readonly FakeText Instance = new();

        public string T(string english) => english;

        public string T(string format, params object?[] arguments) =>
            arguments.Length == 0 ? format : string.Format(format, arguments);

        public string TLong(string key, string english) => english;
    }

    private sealed class FakeCoreClient : ICoreClient
    {
        public Func<string, string, Task>? OnOpen { get; init; }

        public Func<byte[], string, string?, Task>? OnOpenBytes { get; set; }

        public int OpenCalls { get; private set; }

        public int OpenBytesCalls { get; private set; }

        public byte[]? OpenedBytes { get; private set; }

        public string? OpenedPath { get; private set; }

        public string? OpenedPassword { get; private set; }

        public bool IsOpen { get; private set; }

        public string? DatabasePath { get; private set; }

        public Task OpenAsync(string path, string password)
        {
            OpenCalls++;
            OpenedPath = path;
            OpenedPassword = password;
            IsOpen = true;
            DatabasePath = path;
            return OnOpen?.Invoke(path, password) ?? Task.CompletedTask;
        }

        public Task OpenBytesAsync(byte[] kdbx, string password, string? databasePath = null)
        {
            OpenBytesCalls++;
            OpenedBytes = kdbx;
            OpenedPassword = password;
            OpenedPath = databasePath;
            IsOpen = true;
            DatabasePath = databasePath;
            return OnOpenBytes?.Invoke(kdbx, password, databasePath) ?? Task.CompletedTask;
        }

        public Task LockAsync()
        {
            IsOpen = false;
            return Task.CompletedTask;
        }

        // The unlock view model only opens and locks; the sync members of the
        // port are exercised by SyncCoordinatorTests with its own fake.
        public Task<byte[]> SaveBytesAsync()
            => throw new NotSupportedException();

        public Task SaveAsync(string path, int backupRetention = 20,
            Func<bool>? databaseChanged = null)
            => throw new NotSupportedException();

        public Task VerifyAsync(byte[] bytes, string password)
            => throw new NotSupportedException();

        public Task<SyncDecisionDto> SyncDecideAsync(string remoteMetaJson, bool localDirty)
            => throw new NotSupportedException();

        public Task SyncCommitAsync(string? etag, string? sha256)
            => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
