// SPDX-License-Identifier: GPL-3.0-or-later
// Headless behaviour checks for the Settings view model.
using Keepassity.Core;
using Keepassity.ViewModels;

namespace Keepassity.CoreTests;

internal static class SettingsViewModelTests
{
    public static void Run(TestCheck check)
    {
        void Check(string name, bool ok, string detail = "") => check(name, ok, detail);

        var settings = new AppSettings
        {
            Theme = "Dark",
            Language = "zh-CN",
            ClearClipboardAfter30Seconds = true,
            AutoLockEnabled = true,
            StartMinimized = false,
            AutoLockMinutes = 15,
            BackupRetention = 7,
            SyncOnSave = true,
            WebDavUrl = "https://dav.example.test/vault.kdbx",
            WebDavUsername = "alice",
        };
        var host = new FakeHost();
        var vm = new SettingsViewModel(settings, host, FakeText.Instance);

        Check("settings maps the stored appearance",
            vm.ThemeIndex == 2, vm.ThemeIndex.ToString());
        Check("settings maps the stored language",
            vm.SelectedLanguage == "zh-CN", vm.SelectedLanguage);
        Check("settings maps the stored toggles",
            vm.ClearClipboardAfter30Seconds && vm.AutoLockEnabled &&
            !vm.StartMinimized && vm.SyncOnSave);
        Check("settings maps numeric values",
            vm.AutoLockMinutes == 15 && vm.BackupRetention == 7);
        Check("settings offers the host languages",
            vm.Languages.Count == host.Languages.Count);

        vm.ThemeIndex = 1;
        Check("theme change applies and saves immediately",
            settings.Theme == "Light" && host.Themes.Contains("Light") &&
            host.Saves == 1);
        vm.ThemeIndex = 1;
        Check("unchanged theme does not save again", host.Saves == 1);

        vm.AutoLockMinutes = 999;
        Check("auto-lock minutes are clamped",
            vm.AutoLockMinutes == 240 && settings.AutoLockMinutes == 240);
        vm.BackupRetention = 0;
        Check("backup retention is clamped",
            vm.BackupRetention == 1 && settings.BackupRetention == 1);
        vm.StartMinimized = true;
        Check("toggle changes are saved",
            settings.StartMinimized && host.Saves >= 4);

        int applies = host.LanguageApplies;
        vm.SelectedLanguage = "en";
        Check("language change is applied through the host",
            host.LanguageApplies == applies + 1 && host.LastLanguage == "en");

        host.Stored.Add(Key(settings.WebDavUrl!, settings.WebDavUsername!));
        vm.RefreshStoredCredential();
        Check("stored credential is shown",
            vm.CanClearStoredPassword && vm.StoredPasswordText.Contains("alice",
                StringComparison.Ordinal));
        vm.WebDavUsername = "bob";
        Check("changing the user refreshes credential state",
            !vm.CanClearStoredPassword);
        vm.WebDavUsername = "alice";
        Check("changing the user back finds the credential again",
            vm.CanClearStoredPassword);

        vm.ClearStoredPassword();
        Check("clearing the stored credential calls the host",
            host.Deleted.Count == 1 && !host.Stored.Contains(
                Key(settings.WebDavUrl!, "alice")));
        Check("clearing reports success", host.Toasts.Contains("Stored WebDAV password cleared"));

        var result = vm.ApplyAsync(string.Empty).GetAwaiter().GetResult();
        Check("valid WebDAV settings apply", result.Ok && result.SyncNow);
        Check("applied WebDAV settings are persisted",
            settings.WebDavUrl == "https://dav.example.test/vault.kdbx" &&
            settings.WebDavUsername == "alice");

        vm.WebDavUrl = "http://insecure.example.test/vault.kdbx";
        var insecure = vm.ApplyAsync(string.Empty).GetAwaiter().GetResult();
        Check("http WebDAV URL is rejected", !insecure.Ok && insecure.Error is not null);

        vm.WebDavUrl = string.Empty;
        var missingTarget = vm.ApplyAsync("secret").GetAwaiter().GetResult();
        Check("password without a target is rejected",
            !missingTarget.Ok && host.StoredPasswords.Count == 0);

        vm.WebDavUrl = "https://dav.example.test/new.kdbx";
        vm.WebDavUsername = "carol";
        var stored = vm.ApplyAsync("new-secret").GetAwaiter().GetResult();
        Check("password is stored for a valid target",
            stored.Ok && host.StoredPasswords.Count == 1 &&
            host.StoredPasswords[0] == ("https://dav.example.test/new.kdbx",
                "carol", "new-secret"));

        host.StoreError = new InvalidOperationException("dpapi unavailable");
        var failed = vm.ApplyAsync("another").GetAwaiter().GetResult();
        Check("credential storage failure is reported",
            !failed.Ok && failed.Error is not null);

        host.SaveError = new IOException("disk full");
        var saveFailed = vm.ApplyAsync(string.Empty).GetAwaiter().GetResult();
        Check("settings save failure is reported",
            !saveFailed.Ok && saveFailed.Error is not null);
        host.SaveError = null;

        host.Stored.Add(Key(vm.WebDavUrl, vm.WebDavUsername));
        host.DeleteError = new InvalidOperationException("dpapi unavailable");
        vm.ClearStoredPassword();
        Check("credential delete failure is reported",
            host.Toasts.Any(toast => toast.Contains("Cannot clear stored password",
                StringComparison.Ordinal)));
        host.DeleteError = null;

        vm.ExportDiagnosticsAsync().GetAwaiter().GetResult();
        Check("diagnostics success is reported",
            host.Toasts.Contains("Diagnostics exported: /tmp/diagnostics.zip"));
        host.ExportError = new IOException("disk full");
        vm.ExportDiagnosticsAsync().GetAwaiter().GetResult();
        Check("diagnostics failure is reported",
            host.Toasts.Any(toast => toast.Contains("Diagnostics export failed",
                StringComparison.Ordinal)));
    }

    private static string Key(string url, string user) => url + "\n" + user;

    private sealed class FakeText : IAppText
    {
        public static readonly FakeText Instance = new();

        public string T(string english) => english;

        public string T(string format, params object?[] arguments)
            => arguments.Length == 0 ? format : string.Format(format, arguments);

        public string TLong(string key, string english) => english;
    }

    private sealed class FakeHost : ISettingsHost
    {
        public bool IsWebDavSource { get; set; }

        public string AppVersion { get; set; } = "1.2.3";

        public IReadOnlyList<LanguageOption> Languages { get; } = new List<LanguageOption>
        {
            new LanguageOption("system", "System"),
            new LanguageOption("en", "English"),
            new LanguageOption("zh-CN", "简体中文"),
        };

        public HashSet<string> Stored { get; } = new(StringComparer.Ordinal);

        public List<(string Url, string User, string Password)> StoredPasswords { get; } =
            new();

        public List<(string Url, string User)> Deleted { get; } = new();

        public List<string> Themes { get; } = new();

        public List<string> Toasts { get; } = new();

        public int Saves { get; private set; }

        public int LanguageApplies { get; private set; }

        public string? LastLanguage { get; private set; }

        public Exception? StoreError { get; set; }

        public Exception? SaveError { get; set; }

        public Exception? DeleteError { get; set; }

        public Exception? ExportError { get; set; }

        public void ApplyTheme(string theme) => Themes.Add(theme);

        public void ApplyLanguage(string setting)
        {
            LanguageApplies++;
            LastLanguage = setting;
        }

        public void SaveSettings()
        {
            if (SaveError is not null)
            {
                throw SaveError;
            }
            Saves++;
        }

        public bool HasStoredPassword(string url, string username) =>
            Stored.Contains(url + "\n" + username);

        public bool DeleteStoredPassword(string url, string username)
        {
            if (DeleteError is not null)
            {
                throw DeleteError;
            }
            Deleted.Add((url, username));
            return Stored.Remove(url + "\n" + username);
        }

        public void StorePassword(string url, string username, string password)
        {
            if (StoreError is not null)
            {
                throw StoreError;
            }
            StoredPasswords.Add((url, username, password));
            Stored.Add(url + "\n" + username);
        }

        public Task<string> ExportDiagnosticsAsync()
            => ExportError is null
                ? Task.FromResult("/tmp/diagnostics.zip")
                : Task.FromException<string>(ExportError);

        public void ShowToast(string message, SettingsToastSeverity severity)
            => Toasts.Add(message);
    }
}
