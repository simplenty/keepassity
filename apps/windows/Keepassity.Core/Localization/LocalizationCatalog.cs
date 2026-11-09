// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.Json;

namespace Keepassity.Mock;

/// <summary>
/// Platform-neutral UI language catalog.
///
/// The shells ship one JSON catalog per language under
/// <c>Localization/&lt;domain&gt;.&lt;language&gt;.json</c> (embedded in this
/// assembly). A catalog maps an English source string to its translation, so the
/// English UI needs no catalog at all: an untranslated key renders as the English
/// text instead of a raw key. Keeping this logic in the platform-neutral services
/// assembly (instead of WinUI/MRT resources) buys three things the single-window
/// shell needs: the language can change at runtime without recreating the window,
/// the many labels that are built in code-behind can use the same lookup, and the
/// catalog can be unit tested without a UI.
///
/// The Windows shell wraps this class with <c>Loc</c> (event + attached
/// properties) and stores the user's choice in <c>settings.json</c>; see
/// <c>docs/adr/ADR-0031-runtime-ui-localization.md</c> for the trade-offs against
/// the x:Uid/Resources.resw approach.
/// </summary>
public sealed class LocalizationCatalog
{
    /// <summary>Settings value that follows the Windows display language.</summary>
    public const string SystemLanguage = "system";
    public const string EnglishLanguage = "en";
    public const string ChineseLanguage = "zh-CN";

    private const string ResourceInfix = ".Localization.";
    private const string ChineseResourceSuffix = ".zh-CN.json";

    private readonly Dictionary<string, string> _translations;

    private LocalizationCatalog(string language, Dictionary<string, string> translations,
        IReadOnlyList<string> duplicateKeys)
    {
        Language = language;
        _translations = translations;
        DuplicateKeys = duplicateKeys;
    }

    /// <summary>Resolved language tag: <c>en</c> or <c>zh-CN</c>.</summary>
    public string Language { get; }

    /// <summary>Translation count loaded for the active language (0 for English).</summary>
    public int TranslationCount => _translations.Count;

    /// <summary>
    /// Loaded key/value pairs. Exposed for diagnostics, the catalog completeness
    /// gate and tests; callers must treat it as read-only.
    /// </summary>
    public IReadOnlyDictionary<string, string> Translations => _translations;

    /// <summary>
    /// Keys defined by more than one catalog file. A duplicate is a packaging bug;
    /// the first definition wins and the shell logs the list once at startup.
    /// </summary>
    public IReadOnlyList<string> DuplicateKeys { get; }

    /// <summary>Loads the catalog for a resolved language tag.</summary>
    public static LocalizationCatalog Load(string language)
    {
        string normalized = NormalizeLanguage(language);
        var translations = new Dictionary<string, string>(StringComparer.Ordinal);
        var duplicates = new List<string>();
        if (normalized == ChineseLanguage)
        {
            foreach (string resource in ReadCatalogResourceNames())
            {
                using Stream? stream = typeof(LocalizationCatalog).Assembly
                    .GetManifestResourceStream(resource);
                if (stream is null)
                {
                    continue;
                }
                using JsonDocument document = JsonDocument.Parse(stream);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException(
                        $"Language catalog {resource} must be a JSON object.");
                }
                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                {
                    string key = property.Name;
                    string? value = property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString()
                        : null;
                    if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value))
                    {
                        continue;
                    }
                    if (!translations.TryAdd(key, value!))
                    {
                        duplicates.Add(key);
                    }
                }
            }
        }
        return new LocalizationCatalog(normalized, translations, duplicates);
    }

    /// <summary>
    /// Resolves a stored setting (<c>system</c>, <c>en</c>, <c>zh-CN</c>, or an
    /// arbitrary BCP-47 tag) to a language this build ships. Unknown tags fall back
    /// to English; any Chinese variant uses the Simplified Chinese catalog.
    /// </summary>
    public static string NormalizeLanguage(string? setting)
    {
        string value = (setting ?? SystemLanguage).Trim();
        if (value.Length == 0)
        {
            return DetectSystemLanguage();
        }
        if (value.Equals(SystemLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return DetectSystemLanguage();
        }
        if (value.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return ChineseLanguage;
        }
        return EnglishLanguage;
    }

    /// <summary>Language implied by the current Windows display language.</summary>
    public static string DetectSystemLanguage()
        => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals(
            "zh", StringComparison.OrdinalIgnoreCase)
            ? ChineseLanguage
            : EnglishLanguage;

    /// <summary>True when the catalog defines a translation for the key.</summary>
    public bool HasTranslation(string key)
        => Language == ChineseLanguage && _translations.ContainsKey(key);

    /// <summary>
    /// Returns the translation, or <paramref name="fallback"/> when the active
    /// language has no entry. English always uses the fallback.
    /// </summary>
    public string Get(string key, string? fallback = null)
    {
        string resolvedFallback = fallback ?? key;
        if (Language == ChineseLanguage && _translations.TryGetValue(key, out string? value))
        {
            return value;
        }
        return resolvedFallback;
    }

    /// <summary>Looks up a formatted string; placeholders use composite format syntax.</summary>
    public string Format(string key, params object?[] arguments)
        => string.Format(CultureInfo.InvariantCulture, Get(key), arguments);

    private static IEnumerable<string> ReadCatalogResourceNames()
    {
        IEnumerable<string> names = typeof(LocalizationCatalog).Assembly
            .GetManifestResourceNames();
        return names
            .Where(name => name.Contains(ResourceInfix, StringComparison.Ordinal))
            .Where(name => name.EndsWith(ChineseResourceSuffix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal);
    }
}
