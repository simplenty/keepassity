// SPDX-License-Identifier: GPL-3.0-or-later
using Keepassity.Core;
using Keepassity.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Reflection;

namespace Keepassity.App;

/// <summary>
/// Settings / About panel host. The panel itself is a bound
/// <see cref="SettingsPanel"/>; this partial keeps only the WinUI plumbing:
/// right-panel lifetime, theme/language application, DPAPI and diagnostics.
/// </summary>
public sealed partial class MainWindow
{
    private sealed class SettingsHost : ISettingsHost
    {
        private readonly MainWindow _window;

        public SettingsHost(MainWindow window) => _window = window;

        public bool IsWebDavSource => _window._webDavSource;

        public string AppVersion
        {
            get
            {
                string version = typeof(MainWindow).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion
                    ?? typeof(MainWindow).Assembly.GetName().Version?.ToString(3)
                    ?? "unknown";
                // Deterministic builds append "+<source revision>"; the About
                // page shows the human-facing version only.
                int metadata = version.IndexOf('+');
                return metadata > 0 ? version[..metadata] : version;
            }
        }

        public IReadOnlyList<LanguageOption> Languages =>
            Loc.Choices()
                .Select(choice => new LanguageOption(choice.Setting, choice.DisplayName))
                .ToList();

        public void ApplyTheme(string theme) => _window.ApplyThemePreference(theme);

        public void ApplyLanguage(string setting)
        {
            _window.ApplyLanguageSetting(setting);
            _window.ReopenSettingsAfterLanguageChange();
        }

        public void SaveSettings() => _window._settingsStore.Save();

        public bool HasStoredPassword(string url, string username) =>
            _window._credentials.HasPassword(url, username);

        public bool DeleteStoredPassword(string url, string username) =>
            _window._credentials.DeletePassword(url, username);

        public void StorePassword(string url, string username, string password) =>
            _window._credentials.SavePassword(url, username, password);

        public Task<string> ExportDiagnosticsAsync() =>
            DiagnosticsService.ExportAsync(_window.Settings);

        public void ShowToast(string message, SettingsToastSeverity severity) =>
            _window.ShowToast(message, severity switch
            {
                SettingsToastSeverity.Success => InfoBarSeverity.Success,
                SettingsToastSeverity.Warning => InfoBarSeverity.Warning,
                SettingsToastSeverity.Error => InfoBarSeverity.Error,
                _ => InfoBarSeverity.Informational,
            });
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_rightPanelOpen)
        {
            return;
        }
        // Clear the group highlight while Settings is open. Selecting the
        // footer item directly is not supported by NavigationView and can
        // destabilize its selection loop.
        SelectNavItem(null);
        SetSettingsHighlight(true);
        var viewModel = new SettingsViewModel(Settings, new SettingsHost(this),
            LocText.Instance);
        var panel = new SettingsPanel(viewModel);
        _ = ApplyAboutIconAsync(panel.AboutIconElement);
        RightPanelResult settingsResult =
            await ShowRightPanelAsync(Loc.T("Settings"), panel, Loc.T("Save"),
                closeText: Loc.T("Cancel"));
        SetSettingsHighlight(false);
        if (ReferenceEquals(NavView.SelectedItem, null))
        {
            RestoreGroupNavSelection();
        }
        if (settingsResult != RightPanelResult.Primary)
        {
            return;
        }
        SettingsApplyResult apply = await viewModel.ApplyAsync(panel.WebDavPassword);
        if (!apply.Ok)
        {
            ShowToast(apply.Error ?? Loc.T("Cannot save settings"),
                InfoBarSeverity.Error);
            return;
        }
        SetSyncStatus(IsSyncConfigured ? "Not synced" : "Local only");
        if (apply.SyncNow)
        {
            ShowToast(Loc.T("WebDAV settings saved. Syncing…"),
                InfoBarSeverity.Informational);
            _ = SyncAsync(startup: true);
        }
    }
}
