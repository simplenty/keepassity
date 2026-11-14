// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Keepassity.App;

/// <summary>
/// A language offered in the Settings picker. <see cref="DisplayName"/> is already
/// localized and shows each language in a form the user can recognize (English and
/// Simplified Chinese are deliberately named in their own language).
/// </summary>
public sealed record LanguageChoice(string Setting, string DisplayName, bool FollowsSystem);

/// <summary>
/// UI language service for the Windows shell.
///
/// Usage: <c>Loc.T("Unlock")</c> for a plain label and <c>Loc.T("Saved {0} entries.", n)</c>
/// for a formatted message. The English source text is the catalog key, so a
/// missing translation falls back to readable English instead of a raw key. XAML
/// uses the attached properties in <see cref="LocMarkup"/>; both paths refresh
/// when <see cref="LanguageChanged"/> fires, so the language can be switched
/// without restarting the app or recreating the window.
///
/// The persisted choice lives in <c>AppSettings.Language</c> and is applied by the
/// caller (<c>MainWindow</c>); this class only reports and resolves it.
/// </summary>
public static class Loc
{
    private static readonly HashSet<string> MissingKeys = new(StringComparer.Ordinal);
    private static LocalizationCatalog _catalog =
        LocalizationCatalog.Load(LocalizationCatalog.EnglishLanguage);

    /// <summary>Raised on the UI thread after the active language changed.</summary>
    public static event EventHandler? LanguageChanged;

    /// <summary>Resolved language tag (<c>en</c> or <c>zh-CN</c>).</summary>
    public static string Language => _catalog.Language;

    /// <summary>Stored setting: <c>system</c>, <c>en</c> or <c>zh-CN</c>.</summary>
    public static string Setting { get; private set; } = LocalizationCatalog.SystemLanguage;

    public static bool IsChinese => Language == LocalizationCatalog.ChineseLanguage;

    /// <summary>Loaded translation count for the active language (0 for English).</summary>
    public static int TranslationCount => _catalog.TranslationCount;

    /// <summary>Keys that had no translation while a non-English UI was active.</summary>
    public static IReadOnlyCollection<string> MissingTranslations => MissingKeys;

    /// <summary>
    /// Applies the saved setting before the first window content is built. The
    /// diagnostic <c>KEEPASSITY_LANGUAGE</c> override (test hooks only) wins for
    /// automated UI checks and is never written back to settings.
    /// </summary>
    public static void Initialize(string? savedSetting)
    {
        Setting = NormalizeSetting(savedSetting);
        string? forced = TestHooks.EnvironmentValue("KEEPASSITY_LANGUAGE");
        if (!string.IsNullOrWhiteSpace(forced))
        {
            Setting = NormalizeSetting(forced);
            AppLog.Info($"UI language forced by KEEPASSITY_LANGUAGE: {Setting}");
        }
        ApplyLanguage(LocalizationCatalog.NormalizeLanguage(Setting), raiseChanged: false);
        AppLog.Info($"UI language: {Language} (setting: {Setting}, " +
                    $"{TranslationCount} translations)");
    }
    /// <summary>
    /// Changes the active language and raises <see cref="LanguageChanged"/> so the
    /// XAML property bindings and the shell's dynamic labels refresh in place.
    /// Persisting the setting is the caller's job.
    /// </summary>
    public static void SetLanguage(string setting)
    {
        string normalized = NormalizeSetting(setting);
        if (normalized == Setting)
        {
            // Still reapply in case the diagnostic hook or the system language
            // moved underneath the stored setting.
            string resolved = LocalizationCatalog.NormalizeLanguage(normalized);
            if (resolved != Language)
            {
                ApplyLanguage(resolved, raiseChanged: true);
            }
            return;
        }
        Setting = normalized;
        ApplyLanguage(LocalizationCatalog.NormalizeLanguage(normalized), raiseChanged: true);
    }

    /// <summary>Languages offered in the picker, in display order.</summary>
    public static IReadOnlyList<LanguageChoice> Choices()
    {
        return new[]
        {
            new LanguageChoice(LocalizationCatalog.SystemLanguage,
                T("Use the system language"), FollowsSystem: true),
            new LanguageChoice(LocalizationCatalog.EnglishLanguage,
                T("English"), FollowsSystem: false),
            new LanguageChoice(LocalizationCatalog.ChineseLanguage,
                T("Simplified Chinese"), FollowsSystem: false),
        };
    }

    /// <summary>Translates a plain English source string.</summary>
    public static string T(string english)
    {
        if (english.Length == 0)
        {
            return english;
        }
        if (IsChinese && !_catalog.HasTranslation(english))
        {
            RecordMissing(english);
        }
        return _catalog.Get(english);
    }

    /// <summary>
    /// Translates a composite format string and applies the arguments. The catalog
    /// value keeps the <c>{0}</c>/<c>{1}</c> placeholders, so word order can differ
    /// per language.
    /// </summary>
    public static string T(string format, params object?[] arguments)
    {
        string template = T(format);
        if (arguments.Length == 0)
        {
            return template;
        }
        try
        {
            return string.Format(CultureInfo.InvariantCulture, template, arguments);
        }
        catch (FormatException ex)
        {
            AppLog.Warn($"language catalog format error for \"{format}\": {ex.Message}");
            return template;
        }
    }

    /// <summary>
    /// Translates a long/multi-line string by symbolic key, with the English text
    /// passed for the untranslated fallback (paragraphs are too long to repeat as
    /// dictionary keys in the source).
    /// </summary>
    public static string TLong(string key, string english)
    {
        if (!IsChinese)
        {
            return english;
        }
        if (!_catalog.HasTranslation(key))
        {
            RecordMissing(key);
            return english;
        }
        return _catalog.Get(key, english);
    }

    /// <summary>Display name for a stored setting (used by diagnostics/tests).</summary>
    public static string DisplayName(string setting)
    {
        foreach (LanguageChoice choice in Choices())
        {
            if (choice.Setting.Equals(setting, StringComparison.OrdinalIgnoreCase))
            {
                return choice.DisplayName;
            }
        }
        return setting;
    }

    private static void ApplyLanguage(string language, bool raiseChanged)
    {
        LocalizationCatalog catalog = LocalizationCatalog.Load(language);
        if (catalog.DuplicateKeys.Count > 0)
        {
            AppLog.Warn("language catalog duplicate keys: " +
                        string.Join(", ", catalog.DuplicateKeys));
        }
        bool changed = catalog.Language != _catalog.Language;
        _catalog = catalog;
        if (!raiseChanged)
        {
            // Only touch the framework's MRT language override at startup: it is
            // documented for "before any resource is loaded", and re-setting it
            // under a live XAML tree can take the app down (observed catastrophic
            // failure). Runtime strings come from our catalog; framework-owned
            // strings catch up on the next launch, which reads the saved setting.
            ApplyPlatformLanguage(catalog.Language);
        }
        if (raiseChanged)
        {
            LanguageChanged?.Invoke(null, EventArgs.Empty);
        }
        if (changed)
        {
            AppLog.Info($"UI language switched to {catalog.Language}");
        }
    }

    /// <summary>
    /// Best-effort hint for the WinUI framework's own built-in strings (text
    /// editing commands, some control tooltips). The Windows App SDK override
    /// (Microsoft.Windows.Globalization.ApplicationLanguages) is the supported
    /// lever for unpackaged apps; the OS-level Windows.Globalization API throws
    /// without package identity. Framework strings already loaded may not refresh
    /// until the next launch, so the shell's catalog never depends on this. The
    /// override must always be a real tag: an empty value throws in 1.6–1.8.
    /// </summary>
    private static void ApplyPlatformLanguage(string language)
    {
        try
        {
            Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride =
                language == LocalizationCatalog.ChineseLanguage ? "zh-CN" : "en-US";
        }
        catch (Exception ex)
        {
            AppLog.Warn($"could not set the platform language override: {ex.Message}");
        }
    }

    private static string NormalizeSetting(string? setting)
    {
        string value = (setting ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return LocalizationCatalog.SystemLanguage;
        }
        if (value.Equals(LocalizationCatalog.SystemLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return LocalizationCatalog.SystemLanguage;
        }
        if (value.Equals(LocalizationCatalog.EnglishLanguage, StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("en-", StringComparison.OrdinalIgnoreCase))
        {
            return LocalizationCatalog.EnglishLanguage;
        }
        if (value.Equals(LocalizationCatalog.ChineseLanguage, StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return LocalizationCatalog.ChineseLanguage;
        }
        return LocalizationCatalog.SystemLanguage;
    }

    private static void RecordMissing(string key)
    {
        if (MissingKeys.Add(key))
        {
            AppLog.Warn($"missing {Language} translation: \"{key}\"");
        }
    }
}
