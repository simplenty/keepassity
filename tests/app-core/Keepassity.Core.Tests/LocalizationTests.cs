// SPDX-License-Identifier: GPL-3.0-or-later
using Keepassity.Mock;

namespace Keepassity.CoreTests;

/// <summary>
/// Checks for the runtime language catalog: resolution, fallback, formatting and
/// catalog hygiene (duplicates and composite-format placeholder parity). The UI
/// layer is not referenced here, so these tests also run on CI without WinUI.
/// </summary>
internal static class LocalizationTests
{
    public static void Run(Action<string, bool, string> check)
    {
        void Check(string name, bool ok, string detail = "") => check(name, ok, detail);
        LocalizationCatalog english = LocalizationCatalog.Load(
            LocalizationCatalog.EnglishLanguage);
        Check("language catalog: English resolves", english.Language == "en",
            english.Language);
        Check("language catalog: English has no translations",
            english.TranslationCount == 0, english.TranslationCount.ToString());
        Check("language catalog: English falls back to the source string",
            english.Get("Unlock") == "Unlock", english.Get("Unlock"));
        Check("language catalog: English ignores translations",
            english.Get("Unlock", "fallback") == "fallback",
            english.Get("Unlock", "fallback"));

        LocalizationCatalog chinese = LocalizationCatalog.Load(
            LocalizationCatalog.ChineseLanguage);
        Check("language catalog: Chinese resolves", chinese.Language == "zh-CN",
            chinese.Language);
        Check("language catalog: Chinese catalog is not empty",
            chinese.TranslationCount > 100, chinese.TranslationCount.ToString());
        Check("language catalog: Chinese translates a known key",
            chinese.Get("Settings") == "设置", chinese.Get("Settings"));
        Check("language catalog: unknown Chinese key falls back to English",
            chinese.Get("This key is not translated") == "This key is not translated",
            chinese.Get("This key is not translated"));
        Check("language catalog: Chinese honors the explicit fallback",
            chinese.Get("Missing.Symbol.Key", "English fallback") == "English fallback",
            chinese.Get("Missing.Symbol.Key", "English fallback"));
        Check("language catalog: no duplicate keys across catalog files",
            chinese.DuplicateKeys.Count == 0,
            string.Join(", ", chinese.DuplicateKeys.Take(5)));
        Check("language catalog: formatted lookup uses the translation",
            chinese.Format("WebDAV: {0}", "https://example.test/db.kdbx") ==
            "WebDAV：https://example.test/db.kdbx",
            chinese.Format("WebDAV: {0}", "https://example.test/db.kdbx"));

        // Every translated sentence must keep exactly the same composite-format
        // placeholders as the English key; otherwise string.Format throws or the
        // sentence silently loses a value at runtime.
        var placeholderMismatches = new List<string>();
        foreach (KeyValuePair<string, string> entry in chinese.Translations)
        {
            string keyPlaceholders = Placeholders(entry.Key);
            string valuePlaceholders = Placeholders(entry.Value);
            if (keyPlaceholders != valuePlaceholders)
            {
                placeholderMismatches.Add($"{entry.Key} [{keyPlaceholders}] != " +
                                          $"[{valuePlaceholders}]");
            }
        }
        Check("language catalog: placeholder sets match for every entry",
            placeholderMismatches.Count == 0,
            string.Join("; ", placeholderMismatches.Take(3)));

        var untranslated = chinese.Translations
            .Where(entry => !AllowedIdentical(entry.Key) &&
                            string.Equals(entry.Key, entry.Value, StringComparison.Ordinal))
            .Select(entry => entry.Key)
            .ToList();
        Check("language catalog: no unintended untranslated entries",
            untranslated.Count == 0, string.Join("; ", untranslated.Take(5)));

        var lowercaseBrand = chinese.Translations
            .Where(entry => entry.Key.Contains("keepassity", StringComparison.Ordinal) ||
                            entry.Value.Contains("keepassity", StringComparison.Ordinal))
            .Select(entry => entry.Key)
            .ToList();
        check("language catalog: UI text spells the product Keepassity",
            lowercaseBrand.Count == 0, string.Join("; ", lowercaseBrand.Take(5)));

        const string systemLanguageCheck = "language resolution: known tags";
        string systemResolved = LocalizationCatalog.NormalizeLanguage("system");
        bool systemMatchesOs = systemResolved ==
            (System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
                .Equals("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en");
        Check(systemLanguageCheck, systemMatchesOs, systemResolved);
        Check("language resolution: Chinese variants map to zh-CN",
            LocalizationCatalog.NormalizeLanguage("zh-Hans") == "zh-CN" &&
            LocalizationCatalog.NormalizeLanguage("zh-CN") == "zh-CN" &&
            LocalizationCatalog.NormalizeLanguage("zh-TW") == "zh-CN");
        Check("language resolution: English variants map to en",
            LocalizationCatalog.NormalizeLanguage("en-US") == "en" &&
            LocalizationCatalog.NormalizeLanguage("EN") == "en");
        Check("language resolution: unsupported languages fall back to English",
            LocalizationCatalog.NormalizeLanguage("fr-FR") == "en");
        Check("language resolution: empty setting follows the system",
            LocalizationCatalog.NormalizeLanguage(string.Empty) == systemResolved);
    }

    private static string Placeholders(string text)
    {
        var indexes = new SortedSet<int>();
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] != '{' || index + 1 >= text.Length)
            {
                continue;
            }
            if (text[index + 1] == '{')
            {
                index++;
                continue;
            }
            int end = text.IndexOf('}', index + 1);
            if (end < 0)
            {
                break;
            }
            string token = text[(index + 1)..end];
            if (token.Length > 0 && token.All(char.IsDigit) &&
                int.TryParse(token, out int value))
            {
                indexes.Add(value);
            }
            index = end;
        }
        return string.Join(",", indexes);
    }

    /// <summary>Terms that are deliberately identical in both languages.</summary>
    private static bool AllowedIdentical(string key)
        => key is "English" or "TOTP" or "WebDAV" or "KDBX" or "Keepassity" ||
           key.StartsWith("Keepassity ", StringComparison.Ordinal);
}
