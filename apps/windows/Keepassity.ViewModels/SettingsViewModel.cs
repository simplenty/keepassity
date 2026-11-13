// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using Keepassity.Core;

namespace Keepassity.ViewModels;

/// <summary>
/// State and behaviour of the Settings panel. Appearance, language, clipboard,
/// auto-lock, backup retention and sync-on-save apply immediately; the WebDAV
/// source is committed only when the panel's Save button is used. The WebDAV
/// password is never stored on the view model: the view passes the current
/// password box text to <see cref="ApplyAsync"/>, which sends it to the host's
/// DPAPI-backed credential store.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private const string WebDavSourceNoteEnglish =
        "This database was opened from WebDAV; the source URL is fixed for this " +
        "session. To switch sources, lock and use Open from WebDAV… on the unlock " +
        "page. The WebDAV password is stored with Windows DPAPI; clearing it does " +
        "not touch the remote database.";

    private const string WebDavNoteEnglish =
        "The URL must point to the .kdbx file. The WebDAV password is stored " +
        "with Windows DPAPI, not in the plain settings file; clearing it does " +
        "not touch the remote database.";

    private const string LicenseBodyEnglish =
        "This program is free software: you can redistribute it and/or modify it " +
        "under the terms of the GNU General Public License as published by the Free " +
        "Software Foundation, either version 3 of the License, or (at your option) " +
        "any later version. This program is distributed in the hope that it will be " +
        "useful, but WITHOUT ANY WARRANTY; without even the implied warranty of " +
        "MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General " +
        "Public License for more details; the full license text ships in the " +
        "LICENSE file of the source repository.";

    private readonly AppSettings _settings;
    private readonly ISettingsHost _host;
    private readonly IAppText _text;
    private readonly IReadOnlyList<LanguageOption> _languages;

    private int _themeIndex;
    private string _selectedLanguage;
    private bool _clearClipboardAfter30Seconds;
    private bool _autoLockEnabled;
    private bool _startMinimized;
    private double _autoLockMinutes;
    private double _backupRetention;
    private bool _syncOnSave;
    private string _webDavUrl;
    private string _webDavUsername;

    [ObservableProperty]
    private string _storedPasswordText = string.Empty;

    [ObservableProperty]
    private bool _canClearStoredPassword;

    public SettingsViewModel(AppSettings settings, ISettingsHost host, IAppText text)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _languages = host.Languages;
        _themeIndex = settings.Theme switch
        {
            "Light" => 1,
            "Dark" => 2,
            _ => 0,
        };
        _selectedLanguage = settings.Language;
        _clearClipboardAfter30Seconds = settings.ClearClipboardAfter30Seconds;
        _autoLockEnabled = settings.AutoLockEnabled;
        _startMinimized = settings.StartMinimized;
        _autoLockMinutes = settings.AutoLockMinutes;
        _backupRetention = settings.BackupRetention;
        _syncOnSave = settings.SyncOnSave;
        _webDavUrl = settings.WebDavUrl ?? string.Empty;
        _webDavUsername = settings.WebDavUsername ?? string.Empty;
        WebDavNote = host.IsWebDavSource
            ? _text.TLong("Settings.WebDavSourceNote", WebDavSourceNoteEnglish)
            : _text.TLong("Settings.WebDavNote", WebDavNoteEnglish);
        AboutVersionLine = _text.T("Keepassity {0} · local-first KeePass-compatible client",
            host.AppVersion);
        LicenseBody = _text.TLong("About.LicenseBody", LicenseBodyEnglish);
        NextcloudNote = _text.T(
            "Nextcloud/ownCloud with two-factor authentication requires an app " +
            "password. Server URL presets are offered when opening a database from " +
            "WebDAV.");
        RefreshStoredCredential();
    }

    public IReadOnlyList<LanguageOption> Languages => _languages;

    public bool IsWebDavSource => _host.IsWebDavSource;

    public string WebDavNote { get; }

    public string NextcloudNote { get; }

    public string AboutVersionLine { get; }

    public string LicenseBody { get; }

    /// <summary>0 = system, 1 = light, 2 = dark.</summary>
    public int ThemeIndex
    {
        get => _themeIndex;
        set
        {
            if (!SetProperty(ref _themeIndex, value))
            {
                return;
            }
            _settings.Theme = value switch
            {
                1 => "Light",
                2 => "Dark",
                _ => "System",
            };
            _host.ApplyTheme(_settings.Theme);
            _host.SaveSettings();
        }
    }

    public string SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (string.IsNullOrWhiteSpace(value) ||
                !SetProperty(ref _selectedLanguage, value))
            {
                return;
            }
            _settings.Language = value;
            // The host persists the setting, applies the catalog and reopens
            // the panel so every label is rendered in the new language.
            _host.ApplyLanguage(value);
        }
    }

    public bool ClearClipboardAfter30Seconds
    {
        get => _clearClipboardAfter30Seconds;
        set => SetAndSave(ref _clearClipboardAfter30Seconds, value,
            () => _settings.ClearClipboardAfter30Seconds = value);
    }

    public bool AutoLockEnabled
    {
        get => _autoLockEnabled;
        set => SetAndSave(ref _autoLockEnabled, value,
            () => _settings.AutoLockEnabled = value);
    }

    public bool StartMinimized
    {
        get => _startMinimized;
        set => SetAndSave(ref _startMinimized, value,
            () => _settings.StartMinimized = value);
    }

    public double AutoLockMinutes
    {
        get => _autoLockMinutes;
        set
        {
            if (double.IsNaN(value))
            {
                return;
            }
            double clamped = Math.Clamp(value, 1, 240);
            SetAndSave(ref _autoLockMinutes, clamped,
                () => _settings.AutoLockMinutes = (int)clamped);
        }
    }

    public double BackupRetention
    {
        get => _backupRetention;
        set
        {
            if (double.IsNaN(value))
            {
                return;
            }
            double clamped = Math.Clamp(value, 1, 200);
            SetAndSave(ref _backupRetention, clamped,
                () => _settings.BackupRetention = (int)clamped);
        }
    }

    public bool SyncOnSave
    {
        get => _syncOnSave;
        set => SetAndSave(ref _syncOnSave, value,
            () => _settings.SyncOnSave = value);
    }

    public string WebDavUrl
    {
        get => _webDavUrl;
        set
        {
            if (SetProperty(ref _webDavUrl, value ?? string.Empty))
            {
                RefreshStoredCredential();
            }
        }
    }

    public string WebDavUsername
    {
        get => _webDavUsername;
        set
        {
            if (SetProperty(ref _webDavUsername, value ?? string.Empty))
            {
                RefreshStoredCredential();
            }
        }
    }

    /// <summary>Refresh the DPAPI credential summary for the current URL/user.</summary>
    public void RefreshStoredCredential()
    {
        string url = _webDavUrl.Trim();
        string username = _webDavUsername.Trim();
        bool stored = url.Length > 0 && username.Length > 0 &&
                      _host.HasStoredPassword(url, username);
        StoredPasswordText = stored
            ? _text.T("Password stored for {0} (Windows DPAPI).", username)
            : _text.T("No stored password for this WebDAV target.");
        CanClearStoredPassword = stored;
    }

    /// <summary>Delete only the DPAPI entry, never the remote database.</summary>
    public void ClearStoredPassword()
    {
        string url = _webDavUrl.Trim();
        string username = _webDavUsername.Trim();
        if (url.Length == 0 || username.Length == 0)
        {
            _host.ShowToast(_text.T("No stored password to clear."),
                SettingsToastSeverity.Informational);
            return;
        }
        bool cleared = _host.DeleteStoredPassword(url, username);
        _host.ShowToast(
            cleared
                ? _text.T("Stored WebDAV password cleared")
                : _text.T("No stored password to clear."),
            cleared ? SettingsToastSeverity.Success : SettingsToastSeverity.Informational);
        RefreshStoredCredential();
    }

    /// <summary>
    /// Commit the WebDAV source. The password comes from the view's password
    /// box; an empty value keeps the stored credential. Validation happens
    /// before any credential is written.
    /// </summary>
    public Task<SettingsApplyResult> ApplyAsync(string webDavPassword)
    {
        string url = _webDavUrl.Trim();
        string username = _webDavUsername.Trim();
        if (url.Length > 0 &&
            (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) ||
             parsed.Scheme != Uri.UriSchemeHttps))
        {
            return Task.FromResult(new SettingsApplyResult(false,
                Error: _text.T("WebDAV URLs must use https:// (the password is sent with " +
                               "Basic authentication, which requires TLS).")));
        }
        if (webDavPassword.Length > 0)
        {
            if (url.Length == 0 || username.Length == 0)
            {
                return Task.FromResult(new SettingsApplyResult(false,
                    Error: _text.T(
                        "Enter the WebDAV URL and username before the password.")));
            }
            try
            {
                _host.StorePassword(url, username, webDavPassword);
            }
            catch (Exception ex)
            {
                return Task.FromResult(new SettingsApplyResult(false,
                    Error: _text.T("Cannot store WebDAV credential: {0}", ex.Message)));
            }
        }
        _settings.WebDavUrl = url.Length == 0 ? null : url;
        _settings.WebDavUsername = username.Length == 0 ? null : username;
        _host.SaveSettings();
        RefreshStoredCredential();
        bool syncNow = url.Length > 0 && username.Length > 0;
        return Task.FromResult(new SettingsApplyResult(true, syncNow));
    }

    /// <summary>Export diagnostics through the host and report the outcome as a toast.</summary>
    public async Task ExportDiagnosticsAsync()
    {
        try
        {
            string path = await _host.ExportDiagnosticsAsync().ConfigureAwait(true);
            _host.ShowToast(_text.T("Diagnostics exported: {0}", path),
                SettingsToastSeverity.Success);
        }
        catch (Exception ex)
        {
            _host.ShowToast(_text.T("Diagnostics export failed: {0}", ex.Message),
                SettingsToastSeverity.Error);
        }
    }

    private void SetAndSave<T>(ref T field, T value, Action apply)
    {
        if (!SetProperty(ref field, value))
        {
            return;
        }
        apply();
        _host.SaveSettings();
    }
}
