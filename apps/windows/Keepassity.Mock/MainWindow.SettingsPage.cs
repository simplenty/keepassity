// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Reflection;

namespace Keepassity.Mock;

/// <summary>
/// The Settings / About right-side panel. Built in code because it mixes controls,
/// stored-credential state and the About license block; every user-visible string
/// goes through <see cref="Loc"/> so the panel follows the selected language. When
/// the language picker changes, the panel is closed and rebuilt by
/// <see cref="ReopenSettingsAfterLanguageChange"/>.
/// </summary>
public sealed partial class MainWindow
{
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
        var theme = new RadioButtons
        {
            Header = Loc.T("Appearance"),
            Items =
            {
                Loc.T("Use system setting"),
                Loc.T("Light"),
                Loc.T("Dark"),
            },
            SelectedIndex = Settings.Theme switch
            {
                "Light" => 1,
                "Dark" => 2,
                _ => 0,
            },
        };
        var language = new ComboBox
        {
            Header = Loc.T("Language"),
            MinWidth = 160,
        };
        AutomationProperties.SetAutomationId(language, "SettingsLanguageCombo");
        PopulateLanguageCombo(language);
        language.SelectionChanged += (_, _) =>
        {
            if (_languagePickerUpdating)
            {
                return;
            }
            if (language.SelectedItem is ComboBoxItem { Tag: string setting } &&
                !setting.Equals(Settings.Language, StringComparison.OrdinalIgnoreCase))
            {
                ApplyLanguageSetting(setting);
                ReopenSettingsAfterLanguageChange();
            }
        };
        var clipboard = new ToggleSwitch
        {
            Header = Loc.T("Clear copied secrets after 30 seconds"),
            OnContent = Loc.T("On"),
            OffContent = Loc.T("Off"),
            IsOn = Settings.ClearClipboardAfter30Seconds,
        };
        var autoLock = new ToggleSwitch
        {
            Header = Loc.T("Lock the vault after inactivity"),
            OnContent = Loc.T("On"),
            OffContent = Loc.T("Off"),
            IsOn = Settings.AutoLockEnabled,
        };
        var startMinimized = new ToggleSwitch
        {
            Header = Loc.T("Start minimized to the notification area"),
            OnContent = Loc.T("On"),
            OffContent = Loc.T("Off"),
            IsOn = Settings.StartMinimized,
        };
        var minutes = new NumberBox
        {
            Header = Loc.T("Inactivity minutes before lock"),
            Value = Settings.AutoLockMinutes,
            Minimum = 1,
            Maximum = 240,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        };
        var retention = new NumberBox
        {
            Header = Loc.T("Backup copies to keep"),
            Value = Settings.BackupRetention,
            Minimum = 1,
            Maximum = 200,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        };
        var webdavUrl = new TextBox
        {
            Header = Loc.T("WebDAV file URL"),
            Text = Settings.WebDavUrl ?? string.Empty,
            PlaceholderText = "https://dav.example.com/keepassity.kdbx",
            IsReadOnly = _webDavSource,
        };
        var webdavUser = new TextBox
        {
            Header = Loc.T("WebDAV username"),
            Text = Settings.WebDavUsername ?? string.Empty,
            IsReadOnly = _webDavSource,
        };
        var webdavPassword = new PasswordBox
        {
            Header = Loc.T("WebDAV password (leave blank to keep the stored one)"),
            PasswordRevealMode = PasswordRevealMode.Peek,
        };
        var storedPasswordText = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        var clearStoredPassword = new Button
        {
            Content = Loc.T("Clear stored password"),
            HorizontalAlignment = HorizontalAlignment.Left,
            IsEnabled = false,
        };
        var storedPasswordRow = new StackPanel { Spacing = 6 };
        storedPasswordRow.Children.Add(storedPasswordText);
        storedPasswordRow.Children.Add(clearStoredPassword);
        void RefreshStoredCredentialState()
        {
            string currentUrl = webdavUrl.Text.Trim();
            string currentUser = webdavUser.Text.Trim();
            bool stored = currentUrl.Length > 0 && currentUser.Length > 0 &&
                          _credentials.HasPassword(currentUrl, currentUser);
            storedPasswordText.Text = stored
                ? Loc.T("Password stored for {0} (Windows DPAPI).", currentUser)
                : Loc.T("No stored password for this WebDAV target.");
            clearStoredPassword.IsEnabled = stored;
        }
        webdavUrl.TextChanged += (_, _) => RefreshStoredCredentialState();
        webdavUser.TextChanged += (_, _) => RefreshStoredCredentialState();
        clearStoredPassword.Click += (_, _) =>
        {
            string currentUrl = webdavUrl.Text.Trim();
            string currentUser = webdavUser.Text.Trim();
            if (currentUrl.Length == 0 || currentUser.Length == 0)
            {
                return;
            }
            // Deletes the DPAPI entry only; the remote database and settings are
            // untouched. No secret is ever shown or logged here.
            if (_credentials.DeletePassword(currentUrl, currentUser))
            {
                AppLog.Info("stored WebDAV credential cleared");
                ShowToast(Loc.T("Stored WebDAV password cleared"),
                    InfoBarSeverity.Success);
            }
            else
            {
                ShowToast(Loc.T("No stored password to clear."),
                    InfoBarSeverity.Informational);
            }
            RefreshStoredCredentialState();
        };
        RefreshStoredCredentialState();
        var syncOnSave = new ToggleSwitch
        {
            Header = Loc.T("Sync after saving changes"),
            OnContent = Loc.T("On"),
            OffContent = Loc.T("Off"),
            IsOn = Settings.SyncOnSave,
        };
        theme.SelectionChanged += (_, _) =>
        {
            Settings.Theme = theme.SelectedIndex switch
            {
                1 => "Light",
                2 => "Dark",
                _ => "System",
            };
            ApplyThemePreference(Settings.Theme);
            _settingsStore.Save();
        };
        clipboard.Toggled += (_, _) =>
        {
            Settings.ClearClipboardAfter30Seconds = clipboard.IsOn;
            _settingsStore.Save();
        };
        autoLock.Toggled += (_, _) =>
        {
            Settings.AutoLockEnabled = autoLock.IsOn;
            _settingsStore.Save();
        };
        startMinimized.Toggled += (_, _) =>
        {
            Settings.StartMinimized = startMinimized.IsOn;
            _settingsStore.Save();
        };
        minutes.ValueChanged += (_, _) =>
        {
            if (!double.IsNaN(minutes.Value))
            {
                Settings.AutoLockMinutes = Math.Clamp((int)minutes.Value, 1, 240);
                _settingsStore.Save();
            }
        };
        retention.ValueChanged += (_, _) =>
        {
            if (!double.IsNaN(retention.Value))
            {
                Settings.BackupRetention = Math.Clamp((int)retention.Value, 1, 200);
                _settingsStore.Save();
            }
        };
        syncOnSave.Toggled += (_, _) =>
        {
            Settings.SyncOnSave = syncOnSave.IsOn;
            _settingsStore.Save();
        };
        const string webDavSourceNoteEnglish =
            "This database was opened from WebDAV; the source URL is fixed for this " +
            "session. To switch sources, lock and use Open from WebDAV… on the unlock " +
            "page. The WebDAV password is stored with Windows DPAPI; clearing it does " +
            "not touch the remote database.";
        const string webDavNoteEnglish =
            "The URL must point to the .kdbx file. The WebDAV password is stored " +
            "with Windows DPAPI, not in the plain settings file; clearing it does " +
            "not touch the remote database.";
        const string licenseBodyEnglish =
            "This program is free software: you can redistribute it and/or modify it " +
            "under the terms of the GNU General Public License as published by the Free " +
            "Software Foundation, either version 3 of the License, or (at your option) " +
            "any later version. This program is distributed in the hope that it will be " +
            "useful, but WITHOUT ANY WARRANTY; without even the implied warranty of " +
            "MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General " +
            "Public License for more details; the full license text ships in the " +
            "LICENSE file of the source repository.";
        var content = new StackPanel { Spacing = 14 };
        content.Children.Add(theme);
        content.Children.Add(language);
        content.Children.Add(clipboard);
        content.Children.Add(autoLock);
        content.Children.Add(startMinimized);
        content.Children.Add(minutes);
        content.Children.Add(retention);
        content.Children.Add(new TextBlock
        {
            Text = "WebDAV",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 0),
        });
        content.Children.Add(webdavUrl);
        content.Children.Add(webdavUser);
        content.Children.Add(webdavPassword);
        content.Children.Add(new TextBlock
        {
            Text = Loc.T("Nextcloud/ownCloud with two-factor authentication requires an app " +
                         "password. Server URL presets are offered when opening a database from " +
                         "WebDAV."),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
        });
        content.Children.Add(storedPasswordRow);
        content.Children.Add(syncOnSave);
        content.Children.Add(new TextBlock
        {
            Text = _webDavSource
                ? Loc.TLong("Settings.WebDavSourceNote", webDavSourceNoteEnglish)
                : Loc.TLong("Settings.WebDavNote", webDavNoteEnglish),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
        });
        string appVersion = typeof(MainWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(MainWindow).Assembly.GetName().Version?.ToString(3)
            ?? "unknown";
        // Deterministic builds append "+<source revision>" to the informational
        // version; the About page shows the human-facing version only.
        int versionMetadata = appVersion.IndexOf('+');
        if (versionMetadata > 0)
        {
            appVersion = appVersion[..versionMetadata];
        }
        var aboutIcon = new Image
        {
            Width = 72,
            Height = 72,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        // The preview loads asynchronously from the assets copied next to the
        // executable; the section stays without a logo if the asset is missing.
        _ = ApplyAboutIconAsync(aboutIcon);
        content.Children.Add(aboutIcon);
        content.Children.Add(new TextBlock
        {
            Text = Loc.T("About"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 0),
        });
        content.Children.Add(new TextBlock
        {
            Text = Loc.T("Keepassity {0} · local-first KeePass-compatible client", appVersion),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
        });
        // GPL notice (M3-10): version plus the GPL-3.0-or-later short notice and
        // links to the license text and public source, as the license requires.
        content.Children.Add(new TextBlock
        {
            Text = Loc.T("License: GNU General Public License, version 3 or later " +
                         "(GPL-3.0-or-later)."),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock
        {
            Text = Loc.TLong("About.LicenseBody", licenseBodyEnglish),
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
        });
        var licenseLinks = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
        };
        licenseLinks.Children.Add(new HyperlinkButton
        {
            Content = Loc.T("GPL-3.0 license text"),
            NavigateUri = new Uri("https://www.gnu.org/licenses/gpl-3.0.html"),
            Padding = new Thickness(0),
            FontSize = 11,
        });
        licenseLinks.Children.Add(new HyperlinkButton
        {
            Content = Loc.T("Source code"),
            NavigateUri = new Uri("https://github.com/simplenty/keepassity"),
            Padding = new Thickness(0),
            FontSize = 11,
        });
        content.Children.Add(licenseLinks);
        var diagnostics = new Button
        {
            Content = Loc.T("Export diagnostics…"),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        diagnostics.Click += async (_, _) =>
        {
            try
            {
                string path = await DiagnosticsService.ExportAsync(Settings);
                ShowToast(Loc.T("Diagnostics exported: {0}", path),
                    InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                AppLog.Error("diagnostics export failed", ex);
                ShowToast(Loc.T("Diagnostics export failed: {0}", ex.Message),
                    InfoBarSeverity.Error);
            }
        };
        content.Children.Add(diagnostics);
        RightPanelResult settingsResult =
            await ShowRightPanelAsync(Loc.T("Settings"), content, Loc.T("Save"),
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
        string url = webdavUrl.Text.Trim();
        if (url.Length > 0 &&
            (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) ||
             parsed.Scheme != Uri.UriSchemeHttps))
        {
            ShowToast(Loc.T("WebDAV URLs must use https:// (the password is sent with " +
                            "Basic authentication, which requires TLS)."),
                InfoBarSeverity.Error);
            return;
        }
        string username = webdavUser.Text.Trim();
        Settings.WebDavUrl = url.Length == 0 ? null : url;
        Settings.WebDavUsername = username.Length == 0 ? null : username;
        try
        {
            if (webdavPassword.Password.Length > 0)
            {
                if (string.IsNullOrEmpty(Settings.WebDavUrl) ||
                    string.IsNullOrEmpty(Settings.WebDavUsername))
                {
                    ShowToast(Loc.T("Enter the WebDAV URL and username before the password."),
                        InfoBarSeverity.Warning);
                    return;
                }
                _credentials.SavePassword(Settings.WebDavUrl, Settings.WebDavUsername,
                    webdavPassword.Password);
            }
            _settingsStore.Save();
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T("Cannot store WebDAV credential: {0}", ex.Message),
                InfoBarSeverity.Error);
            return;
        }
        SetSyncStatus(IsSyncConfigured ? "Not synced" : "Local only");
        if (IsSyncConfigured)
        {
            ShowToast(Loc.T("WebDAV settings saved. Syncing…"),
                InfoBarSeverity.Informational);
            _ = SyncAsync(startup: true);
        }
    }
}
