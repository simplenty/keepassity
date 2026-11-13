// SPDX-License-Identifier: GPL-3.0-or-later
namespace Keepassity.ViewModels;

/// <summary>A language offered in the Settings picker.</summary>
public sealed record LanguageOption(string Setting, string DisplayName);

/// <summary>Toast severity without depending on a UI framework.</summary>
public enum SettingsToastSeverity
{
    Informational,
    Success,
    Warning,
    Error,
}

/// <summary>Outcome of committing the Settings panel.</summary>
public sealed record SettingsApplyResult(bool Ok, bool SyncNow = false, string? Error = null);

/// <summary>
/// Platform operations the Settings view model needs. The shell implements this
/// port; tests use a fake. Keeping it this narrow is what lets the settings
/// decisions be tested without WinUI, DPAPI or the file system.
/// </summary>
public interface ISettingsHost
{
    bool IsWebDavSource { get; }

    string AppVersion { get; }

    IReadOnlyList<LanguageOption> Languages { get; }

    void ApplyTheme(string theme);

    void ApplyLanguage(string setting);

    void SaveSettings();

    bool HasStoredPassword(string url, string username);

    bool DeleteStoredPassword(string url, string username);

    void StorePassword(string url, string username, string password);

    Task<string> ExportDiagnosticsAsync();

    void ShowToast(string message, SettingsToastSeverity severity);
}
