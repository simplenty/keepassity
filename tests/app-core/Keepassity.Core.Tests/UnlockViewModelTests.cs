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

    public static void Run(Action<string, bool, string> check)
    {
        _report = check;
        MissingDatabaseIsRejected();
        EmptyPasswordIsRejected();
        WrongPasswordIsClassified();
        UnsupportedDatabaseIsClassified();
        SuccessClearsTheBanner();
        ConcurrentAttemptsAreRejected();
        ChangingTheDatabaseClearsStaleErrors();
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

        public int OpenCalls { get; private set; }

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

        public Task LockAsync()
        {
            IsOpen = false;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }
}
